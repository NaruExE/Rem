using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LibVLCSharp.Shared;
using REM.Models;

namespace REM.Services;

/// <summary>
/// Dedicated hardware-decoded LibVLC video playback engine with strictly async controls,
/// embedded audio/subtitle switching, headless video-track detach, and 10-second SQLite auto-resume.
/// </summary>
public sealed class VideoPlayerService : IDisposable, IAsyncDisposable
{
    public static readonly float[] SupportedPlaybackSpeeds = [0.5f, 1.0f, 1.25f, 1.5f, 2.0f];

    private static readonly (string? VlcValue, string Label)[] SupportedAspectRatios =
    [
        (null, "Default"),
        ("16:9", "16:9"),
        ("4:3", "4:3"),
        ("21:9", "21:9"),
        ("1:1", "1:1")
    ];

    private readonly DatabaseService _database;
    private readonly VideoMediaResolverService _resolver;
    private readonly ActionLogService _actionLog;
    private readonly string _bandwidthProviderKey;
    private readonly string _volumeSettingKey;
    private readonly string _qualitySettingKey;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _currentMedia;
    private CancellationTokenSource? _autoSaveCts;
    private CancellationTokenSource? _idleUnloadCts;

    private long _archivedReadBytes;
    private long _currentMediaLastReadBytes;
    private bool _coreInitialized;

    private int _volume = 80;
    private bool _isMuted;
    private float _playbackSpeed = 1.0f;
    private long _subtitleDelayMs;
    private string _qualityPreset = "best";
    private VideoHeadlessMode _headlessMode = VideoHeadlessMode.KeepVideoStream;
    private VideoLoopMode _loopMode = VideoLoopMode.Off;
    private bool _isShuffled;
    private long? _abLoopStartMs;
    private long? _abLoopEndMs;
    private int _abSeekInFlight;
    private int _aspectRatioIndex;
    private int _savedVideoTrackId = -1;
    private long? _currentPlaylistItemId;
    private string? _currentTitle;
    private string? _currentSourceUrl;
    private ResolvedVideoMedia? _currentResolvedMedia;
    private string? _cachedAudioOnlyUrl;
    private readonly List<string> _activeExternalSubUris = [];
    private bool _needsVideoSurfaceRebind;
    private bool _disposed;

    public VideoPlayerService(
        DatabaseService database,
        VideoMediaResolverService resolver,
        ActionLogService actionLog,
        string bandwidthProviderKey = "libvlc-video",
        string volumeSettingKey = DatabaseService.KeyVideoVolume,
        string qualitySettingKey = DatabaseService.KeyVideoQuality)
    {
        _database = database;
        _resolver = resolver;
        _actionLog = actionLog;
        _bandwidthProviderKey = string.IsNullOrWhiteSpace(bandwidthProviderKey) ? "libvlc-video" : bandwidthProviderKey;
        _volumeSettingKey = string.IsNullOrWhiteSpace(volumeSettingKey) ? DatabaseService.KeyVideoVolume : volumeSettingKey;
        _qualitySettingKey = string.IsNullOrWhiteSpace(qualitySettingKey) ? DatabaseService.KeyVideoQuality : qualitySettingKey;

        if (int.TryParse(_database.GetSetting(_volumeSettingKey), out var savedVol))
        {
            _volume = Math.Clamp(savedVol, 0, 100);
        }

        _qualityPreset = NormalizeQualityPreset(_database.GetSetting(_qualitySettingKey));

        BandwidthMonitor.RegisterByteProvider(_bandwidthProviderKey, GetCumulativeReadBytes);
    }

    public MediaPlayer? MediaPlayer => _player;
    public bool IsInitialized => _libVlc is not null && _player is not null;
    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsBuffering { get; private set; }
    public float BufferingPercent { get; private set; }
    public bool IsHeadlessAudioOnly { get; private set; }
    public long PositionMs { get; private set; }
    public long DurationMs { get; private set; }
    public int Volume => _volume;
    public bool IsMuted => _isMuted;
    public float PlaybackSpeed => _playbackSpeed;
    public long SubtitleDelayMs => _subtitleDelayMs;
    public string QualityPreset => _qualityPreset;
    public VideoHeadlessMode HeadlessMode => _headlessMode;
    public VideoLoopMode LoopMode => _loopMode;
    public bool IsShuffled => _isShuffled;
    public long? AbLoopStartMs => _abLoopStartMs;
    public long? AbLoopEndMs => _abLoopEndMs;
    public string AspectRatioLabel => SupportedAspectRatios[_aspectRatioIndex].Label;
    public int? QualityMaxHeight => _qualityPreset switch
    {
        "1080" => 1080,
        "720" => 720,
        "480" => 480,
        "360" => 360,
        "240" => 240,
        _ => null
    };
    public long? CurrentPlaylistItemId => _currentPlaylistItemId;
    public string? CurrentTitle => _currentTitle;
    public string? CurrentSourceUrl => _currentSourceUrl;

    public event EventHandler<VideoPlaybackSnapshot>? PlaybackStateChanged;
    public event EventHandler<long>? PositionChanged;
    public event EventHandler<long>? DurationChanged;
    public event EventHandler<float>? BufferingChanged;
    public event EventHandler? TracksChanged;
    public event EventHandler<long?>? EndReached;
    public event EventHandler<string>? PlaybackError;

    public VideoPlaybackSnapshot GetSnapshot()
    {
        var player = _player;
        var audioId = -1;
        var spuId = -1;
        if (player is not null)
        {
            try
            {
                audioId = player.AudioTrack;
                spuId = player.Spu;
            }
            catch
            {
                // Ignore if player is tearing down.
            }
        }

        return new VideoPlaybackSnapshot(
            IsPlaying: IsPlaying,
            IsPaused: IsPaused,
            IsBuffering: IsBuffering,
            BufferingPercent: BufferingPercent,
            PositionMs: PositionMs,
            DurationMs: DurationMs,
            Volume: _volume,
            IsMuted: _isMuted,
            PlaybackSpeed: _playbackSpeed,
            CurrentTitle: _currentTitle,
            CurrentSourceUrl: _currentSourceUrl,
            CurrentPlaylistItemId: _currentPlaylistItemId,
            ActiveAudioTrackId: audioId,
            ActiveSubtitleTrackId: spuId,
            SubtitleDelayMs: _subtitleDelayMs,
            QualityPreset: _qualityPreset,
            LoopMode: _loopMode,
            IsShuffled: _isShuffled,
            AbLoopStartMs: _abLoopStartMs,
            AbLoopEndMs: _abLoopEndMs,
            AspectRatioLabel: AspectRatioLabel);
    }

    public async Task<MediaPlayer> EnsurePlayerReadyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                EnsureInitializedLocked();
                return _player!;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ResolvedVideoMedia> PlaySourceAsync(
        string input,
        string? referer = null,
        string? titleOverride = null,
        long startPositionMs = 0,
        long? playlistItemId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await SaveCurrentProgressAsync(CancellationToken.None).ConfigureAwait(false);
        var resolved = await _resolver.ResolveAsync(
            input,
            referer,
            titleOverride,
            maxHeight: QualityMaxHeight,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                EnsureInitializedLocked();

                try
                {
                    _player!.Stop();
                }
                catch
                {
                    // Ignore stop failure on idle player.
                }

                SnapshotCurrentMediaReadBytesLocked();
                _currentMedia?.Dispose();
                _currentMedia = null;

                var resumeStartMs = Math.Max(0, startPositionMs);
                var media = BuildMediaLocked(resolved, resumeStartMs, IsHeadlessAudioOnly);

                _currentMedia = media;
                _currentResolvedMedia = resolved;
                _cachedAudioOnlyUrl = null;
                _activeExternalSubUris.Clear();
                _currentTitle = resolved.Title;
                _currentSourceUrl = resolved.OriginalInput;
                _currentPlaylistItemId = playlistItemId;
                _savedVideoTrackId = -1;
                _subtitleDelayMs = 0;
                _abLoopStartMs = null;
                _abLoopEndMs = null;
                _needsVideoSurfaceRebind = IsHeadlessAudioOnly;

                PositionMs = resumeStartMs;
                DurationMs = 0;
                IsBuffering = false;
                BufferingPercent = 0;

                _player!.Volume = _volume;
                _player.Mute = _isMuted;
                try { _player.SetSpuDelay(0); } catch { }
                try { _player.AspectRatio = SupportedAspectRatios[_aspectRatioIndex].VlcValue; } catch { }
                _player.Play(media);
                _player.SetRate(_playbackSpeed);

                IsPlaying = true;
                IsPaused = false;
            }, cancellationToken).ConfigureAwait(false);

            StartAutoSaveLoop();
            RaiseStateChanged();
            return resolved;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ResolvedVideoMedia> PlayPlaylistItemAsync(
        VideoPlaylistItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var resumeMs = item.LastPositionMs >= 2000 &&
            (item.DurationMs <= 0 || item.LastPositionMs < item.DurationMs - 5000)
            ? item.LastPositionMs
            : 0;

        return PlaySourceAsync(
            input: item.SourceUrl,
            referer: item.Referer,
            titleOverride: item.Title,
            startPositionMs: resumeMs,
            playlistItemId: item.Id,
            cancellationToken: cancellationToken);
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _player is null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (_player is { IsPlaying: true })
                {
                    _player.SetPause(true);
                }

                if (_player is not null && _player.Time > 0)
                {
                    PositionMs = _player.Time;
                }

                IsPlaying = false;
                if (_currentMedia is not null)
                {
                    IsPaused = true;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        await SaveCurrentProgressAsync(CancellationToken.None).ConfigureAwait(false);
        RaiseStateChanged();
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _player is null || _currentMedia is null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (_player is not null && !_player.IsPlaying)
                {
                    _player.SetPause(false);
                    _player.SetRate(_playbackSpeed);
                    IsPlaying = true;
                    IsPaused = false;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        StartAutoSaveLoop();
        RaiseStateChanged();
    }

    public Task TogglePauseAsync(CancellationToken cancellationToken = default) =>
        IsPlaying ? PauseAsync(cancellationToken) : ResumeAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await SaveCurrentProgressAsync(CancellationToken.None).ConfigureAwait(false);
        StopAutoSaveLoop();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (_player is not null)
                {
                    try { _player.Stop(); } catch { }
                }

                IsPlaying = false;
                IsPaused = false;
                IsBuffering = false;
                BufferingPercent = 0;
                PositionMs = 0;
                _abLoopStartMs = null;
                _abLoopEndMs = null;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        RaiseStateChanged();
    }

    public async Task SeekAsync(long positionMs, CancellationToken cancellationToken = default)
    {
        var player = _player;
        if (_disposed || player is null || _currentMedia is null)
        {
            return;
        }

        var clamped = DurationMs > 0
            ? Math.Clamp(positionMs, 0, DurationMs)
            : Math.Max(0, positionMs);

        await Task.Run(() =>
        {
            try
            {
                player.Time = clamped;
                PositionMs = clamped;
            }
            catch
            {
                // Ignore seek error on unseekable live stream.
            }
        }, cancellationToken).ConfigureAwait(false);

        PositionChanged?.Invoke(this, PositionMs);
        RaiseStateChanged();
    }

    public async Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(volume, 0, 100);
        _volume = clamped;

        var player = _player;
        if (player is not null)
        {
            try
            {
                player.Volume = clamped;
            }
            catch
            {
                // Ignore if tearing down.
            }
        }

        await _database.SaveSettingAsync(_volumeSettingKey, clamped.ToString(CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);
        RaiseStateChanged();
    }

    public async Task SetMuteAsync(bool muted, CancellationToken cancellationToken = default)
    {
        _isMuted = muted;
        var player = _player;
        if (player is not null)
        {
            await Task.Run(() =>
            {
                try { player.Mute = muted; } catch { }
            }, cancellationToken).ConfigureAwait(false);
        }

        RaiseStateChanged();
    }

    public Task ToggleMuteAsync(CancellationToken cancellationToken = default) =>
        SetMuteAsync(!_isMuted, cancellationToken);

    public void SetLoopMode(VideoLoopMode mode)
    {
        if (_disposed)
        {
            return;
        }

        _loopMode = mode;
        RaiseStateChanged();
    }

    public VideoLoopMode CycleLoopMode()
    {
        var next = _loopMode switch
        {
            VideoLoopMode.Off => VideoLoopMode.One,
            VideoLoopMode.One => VideoLoopMode.All,
            _ => VideoLoopMode.Off
        };
        SetLoopMode(next);
        return next;
    }

    public void SetShuffle(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        _isShuffled = enabled;
        RaiseStateChanged();
    }

    public bool ToggleShuffle()
    {
        SetShuffle(!_isShuffled);
        return _isShuffled;
    }

    public string CycleAbLoop()
    {
        if (_disposed || _currentMedia is null)
        {
            return "Load a video first to set an A-B loop.";
        }

        var player = _player;
        var currentMs = player is { Time: > 0 } ? player.Time : PositionMs;

        if (_abLoopStartMs is null)
        {
            _abLoopStartMs = Math.Max(0, currentMs);
            _abLoopEndMs = null;
            RaiseStateChanged();
            return $"A-B Loop: A set at {FormatMsShort(_abLoopStartMs.Value)} (press again for B)";
        }

        if (_abLoopEndMs is null)
        {
            if (currentMs <= _abLoopStartMs.Value + 500)
            {
                _abLoopStartMs = null;
                _abLoopEndMs = null;
                RaiseStateChanged();
                return "A-B Loop cleared (B must be at least 0.5s after A).";
            }

            _abLoopEndMs = currentMs;
            var startMs = _abLoopStartMs.Value;
            RaiseStateChanged();
            _ = SeekAsync(startMs, CancellationToken.None);
            return $"A-B Loop: {FormatMsShort(startMs)} → {FormatMsShort(_abLoopEndMs.Value)}";
        }

        _abLoopStartMs = null;
        _abLoopEndMs = null;
        RaiseStateChanged();
        return "A-B Loop cleared";
    }

    public void ClearAbLoop()
    {
        _abLoopStartMs = null;
        _abLoopEndMs = null;
        RaiseStateChanged();
    }

    public async Task<string> CycleAspectRatioAsync(CancellationToken cancellationToken = default)
    {
        _aspectRatioIndex = (_aspectRatioIndex + 1) % SupportedAspectRatios.Length;
        var (vlcValue, label) = SupportedAspectRatios[_aspectRatioIndex];

        var player = _player;
        if (player is not null && !_disposed)
        {
            await Task.Run(() =>
            {
                try { player.AspectRatio = vlcValue; } catch { }
            }, cancellationToken).ConfigureAwait(false);
        }

        RaiseStateChanged();
        return label;
    }

    public async Task<bool> NextFrameAsync(CancellationToken cancellationToken = default)
    {
        var player = _player;
        if (_disposed || player is null || _currentMedia is null)
        {
            return false;
        }

        await Task.Run(() =>
        {
            try
            {
                player.NextFrame();
                if (player.Time >= 0)
                {
                    PositionMs = player.Time;
                }

                IsPlaying = false;
                IsPaused = true;
            }
            catch
            {
            }
        }, cancellationToken).ConfigureAwait(false);

        PositionChanged?.Invoke(this, PositionMs);
        RaiseStateChanged();
        return true;
    }

    public async Task<string?> TakeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var player = _player;
        if (_disposed || player is null || _currentMedia is null || IsHeadlessAudioOnly)
        {
            return null;
        }

        var picturesDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var snapshotsDir = Path.Combine(
            string.IsNullOrWhiteSpace(picturesDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : picturesDir,
            "REM Snapshots");
        Directory.CreateDirectory(snapshotsDir);

        var filePath = Path.Combine(snapshotsDir, $"REM_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        var ok = await Task.Run(() =>
        {
            try
            {
                return player.TakeSnapshot(0, filePath, 0, 0);
            }
            catch
            {
                return false;
            }
        }, cancellationToken).ConfigureAwait(false);

        if (ok)
        {
            _actionLog.Log("Video", $"Saved video snapshot: {filePath}");
            return filePath;
        }

        return null;
    }

    private static string FormatMsShort(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ts.TotalHours >= 1.0
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    public async Task SetPlaybackSpeedAsync(float speed, CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(speed, 0.25f, 4.0f);
        _playbackSpeed = clamped;

        var player = _player;
        if (player is not null)
        {
            await Task.Run(() =>
            {
                try { player.SetRate(clamped); } catch { }
            }, cancellationToken).ConfigureAwait(false);
        }

        RaiseStateChanged();
    }

    public async Task<IReadOnlyList<VideoTrackOption>> GetAudioTracksAsync(CancellationToken cancellationToken = default)
    {
        var player = _player;
        if (_disposed || player is null)
        {
            return [];
        }

        return await Task.Run<IReadOnlyList<VideoTrackOption>>(() =>
        {
            try
            {
                return player.AudioTrackDescription
                    .Select(t => new VideoTrackOption(t.Id, t.Name ?? $"Track {t.Id}"))
                    .ToList();
            }
            catch
            {
                return [];
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetAudioTrackAsync(int trackId, CancellationToken cancellationToken = default)
    {
        var player = _player;
        if (_disposed || player is null)
        {
            return false;
        }

        var ok = await Task.Run(() =>
        {
            try { return player.SetAudioTrack(trackId); } catch { return false; }
        }, cancellationToken).ConfigureAwait(false);

        if (ok)
        {
            RaiseStateChanged();
        }

        return ok;
    }

    public async Task<IReadOnlyList<VideoTrackOption>> GetSubtitleTracksAsync(CancellationToken cancellationToken = default)
    {
        var player = _player;
        if (_disposed || player is null)
        {
            return [];
        }

        return await Task.Run<IReadOnlyList<VideoTrackOption>>(() =>
        {
            try
            {
                return player.SpuDescription
                    .Select(t => new VideoTrackOption(t.Id, t.Name ?? $"Subtitle {t.Id}"))
                    .ToList();
            }
            catch
            {
                return [];
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetSubtitleTrackAsync(int trackId, CancellationToken cancellationToken = default)
    {
        var player = _player;
        if (_disposed || player is null)
        {
            return false;
        }

        var ok = await Task.Run(() =>
        {
            try { return player.SetSpu(trackId); } catch { return false; }
        }, cancellationToken).ConfigureAwait(false);

        if (ok)
        {
            RaiseStateChanged();
        }

        return ok;
    }

    public async Task<bool> LoadExternalSubtitleAsync(string pathOrUrl, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(pathOrUrl))
        {
            return false;
        }

        var trimmed = pathOrUrl.Trim().Trim('"');
        string subUri;
        if (File.Exists(trimmed))
        {
            var utf8Path = await EnsureUtf8SubtitleFileAsync(trimmed, cancellationToken).ConfigureAwait(false);
            subUri = new Uri(Path.GetFullPath(utf8Path)).AbsoluteUri;
        }
        else if (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) &&
                 (string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(parsed.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)))
        {
            subUri = parsed.AbsoluteUri;
        }
        else
        {
            throw new FileNotFoundException("Subtitle file or URL was not found.", trimmed);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool added;
        try
        {
            if (_disposed || _player is null || _currentMedia is null)
            {
                return false;
            }

            var player = _player;
            added = await Task.Run(() =>
            {
                try
                {
                    return player.AddSlave(MediaSlaveType.Subtitle, subUri, true);
                }
                catch
                {
                    return false;
                }
            }, cancellationToken).ConfigureAwait(false);

            if (added && !_activeExternalSubUris.Contains(subUri, StringComparer.Ordinal))
            {
                _activeExternalSubUris.Add(subUri);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (added)
        {
            _actionLog.Log("Video", $"Loaded external subtitle: {Path.GetFileName(trimmed)}");
            TracksChanged?.Invoke(this, EventArgs.Empty);
            RaiseStateChanged();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(350, CancellationToken.None).ConfigureAwait(false);
                    if (!_disposed)
                    {
                        TracksChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch
                {
                }
            });
        }

        return added;
    }

    public async Task<bool> SetSubtitleDelayMsAsync(long delayMs, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return false;
        }

        var clamped = Math.Clamp(delayMs, -300_000L, 300_000L);
        _subtitleDelayMs = clamped;

        var player = _player;
        if (player is not null && _currentMedia is not null)
        {
            await Task.Run(() =>
            {
                try
                {
                    // LibVLC SetSpuDelay expects microseconds.
                    player.SetSpuDelay(clamped * 1000L);
                }
                catch
                {
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        RaiseStateChanged();
        return true;
    }

    public Task<bool> NudgeSubtitleDelayMsAsync(long deltaMs, CancellationToken cancellationToken = default) =>
        SetSubtitleDelayMsAsync(_subtitleDelayMs + deltaMs, cancellationToken);

    public async Task<bool> SetQualityPresetAsync(string preset, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var normalized = NormalizeQualityPreset(preset);
        var changed = !string.Equals(_qualityPreset, normalized, StringComparison.Ordinal);
        _qualityPreset = normalized;

        if (changed)
        {
            await _database.SaveSettingAsync(_qualitySettingKey, normalized, cancellationToken)
                .ConfigureAwait(false);
            RaiseStateChanged();
        }

        var resolved = _currentResolvedMedia;
        if (resolved is null || resolved.IsLocalFile || _currentMedia is null)
        {
            return false;
        }

        var player = _player;
        long resumeMs = 0;
        if (DurationMs > 0)
        {
            var rawPos = player?.Time > 0 ? player.Time : PositionMs;
            if (rawPos >= 1000 && rawPos < DurationMs - 2000)
            {
                resumeMs = rawPos;
            }
        }

        var savedSubs = _activeExternalSubUris.ToList();
        var savedDelayMs = _subtitleDelayMs;
        var itemId = _currentPlaylistItemId;
        var title = _currentTitle;

        await PlaySourceAsync(
            input: resolved.OriginalInput,
            referer: resolved.Referer,
            titleOverride: title,
            startPositionMs: resumeMs,
            playlistItemId: itemId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (savedDelayMs != 0)
        {
            await SetSubtitleDelayMsAsync(savedDelayMs, cancellationToken).ConfigureAwait(false);
        }

        foreach (var subUri in savedSubs)
        {
            await LoadExternalSubtitleAsync(subUri, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public void MarkSurfaceDetached()
    {
        if (!_disposed && _currentMedia is not null)
        {
            _needsVideoSurfaceRebind = true;
        }
    }

    public async Task DetachVideoSurfaceAsync(
        VideoHeadlessMode mode = VideoHeadlessMode.KeepVideoStream,
        CancellationToken cancellationToken = default)
    {
        IsHeadlessAudioOnly = true;
        _headlessMode = mode;
        if (_currentMedia is not null)
        {
            _needsVideoSurfaceRebind = true;
        }

        var player = _player;
        if (_disposed || player is null)
        {
            return;
        }

        await Task.Run(() =>
        {
            try
            {
                var currentVidTrack = player.VideoTrack;
                if (currentVidTrack >= 0)
                {
                    _savedVideoTrackId = currentVidTrack;
                }

                player.SetVideoTrack(-1);
                player.Hwnd = IntPtr.Zero;
            }
            catch
            {
                // Ignore if no active media.
            }
        }, cancellationToken).ConfigureAwait(false);

        var resolved = _currentResolvedMedia;
        if (mode == VideoHeadlessMode.KeepVideoStream || resolved is null || resolved.IsLocalFile)
        {
            _actionLog.Log("Video", "Detached video surface (headless, keeping stream).");
            RaiseStateChanged();
            return;
        }

        string? audioOnlyUrl = null;
        if (!string.IsNullOrWhiteSpace(resolved.AudioSlaveUrl))
        {
            audioOnlyUrl = resolved.AudioSlaveUrl;
        }
        else if (!string.IsNullOrWhiteSpace(_cachedAudioOnlyUrl))
        {
            audioOnlyUrl = _cachedAudioOnlyUrl;
        }
        else if (resolved.UsedYtDlp)
        {
            audioOnlyUrl = await _resolver.ResolveAudioOnlyUrlAsync(resolved.OriginalInput, resolved.Referer, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(audioOnlyUrl))
            {
                _cachedAudioOnlyUrl = audioOnlyUrl;
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || !IsHeadlessAudioOnly || _player is null || _currentResolvedMedia is not { } cur || !ReferenceEquals(cur, resolved))
            {
                return;
            }

            var wasPlaying = IsPlaying || _player.IsPlaying;
            var isLive = DurationMs <= 0;
            var resumePosMs = isLive
                ? 0
                : Math.Max(0, _player.Time > 0 ? _player.Time : PositionMs);

            await Task.Run(() =>
            {
                try { _player.Stop(); } catch { }

                SnapshotCurrentMediaReadBytesLocked();
                _currentMedia?.Dispose();
                _currentMedia = null;

                Media media;
                if (!string.IsNullOrWhiteSpace(audioOnlyUrl))
                {
                    media = new Media(_libVlc!, audioOnlyUrl, FromType.FromLocation);
                }
                else
                {
                    media = new Media(_libVlc!, cur.PlayableUrl, FromType.FromLocation);
                    media.AddOption(":adaptive-logic=lowest");
                    media.AddOption(":adaptive-maxheight=144");
                }

                if (!string.IsNullOrWhiteSpace(cur.Referer))
                {
                    media.AddOption($":http-referrer={cur.Referer}");
                }

                media.AddOption(":no-video");
                if (resumePosMs >= 1000)
                {
                    var startSec = resumePosMs / 1000.0;
                    media.AddOption($":start-time={startSec.ToString("0.###", CultureInfo.InvariantCulture)}");
                }

                _currentMedia = media;
                PositionMs = resumePosMs;
                _player.Volume = _volume;
                _player.Mute = _isMuted;
                try { _player.AspectRatio = SupportedAspectRatios[_aspectRatioIndex].VlcValue; } catch { }
                _player.Play(media);
                _player.SetRate(_playbackSpeed);

                if (!wasPlaying)
                {
                    try { _player.SetPause(true); } catch { }
                    IsPlaying = false;
                    IsPaused = true;
                }
                else
                {
                    IsPlaying = true;
                    IsPaused = false;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        _actionLog.Log("Video", "Switched headless playback to audio-only stream (bandwidth saver).");
        RaiseStateChanged();
    }

    public async Task ReattachVideoSurfaceAsync(CancellationToken cancellationToken = default)
    {
        IsHeadlessAudioOnly = false;
        var player = _player;
        if (_disposed || player is null || _currentMedia is null || _currentResolvedMedia is null)
        {
            return;
        }

        if (!_needsVideoSurfaceRebind && player.Hwnd != IntPtr.Zero && player.VideoTrack >= 0)
        {
            RaiseStateChanged();
            return;
        }

        // Wait up to 600ms for LibVLCSharp.Avalonia.VideoView to finish creating & binding its native HWND.
        for (var i = 0; i < 24 && !_disposed; i++)
        {
            if (player.Hwnd != IntPtr.Zero)
            {
                break;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        List<string> savedSubs;
        long savedDelayMs;
        bool wasPlaying;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || _player is null || _currentResolvedMedia is not { } resolved)
            {
                return;
            }

            savedSubs = _activeExternalSubUris.ToList();
            savedDelayMs = _subtitleDelayMs;
            wasPlaying = IsPlaying || _player.IsPlaying;
            var isLive = DurationMs <= 0;
            var resumePosMs = isLive
                ? 0
                : Math.Max(0, _player.Time > 0 ? _player.Time : PositionMs);

            await Task.Run(() =>
            {
                try { _player.Stop(); } catch { }

                SnapshotCurrentMediaReadBytesLocked();
                _currentMedia?.Dispose();
                _currentMedia = null;

                var media = BuildMediaLocked(resolved, resumePosMs, headlessAudioOnly: false);
                _currentMedia = media;
                _needsVideoSurfaceRebind = false;

                PositionMs = resumePosMs;
                _player.Volume = _volume;
                _player.Mute = _isMuted;
                try { _player.AspectRatio = SupportedAspectRatios[_aspectRatioIndex].VlcValue; } catch { }
                _player.Play(media);
                _player.SetRate(_playbackSpeed);

                if (savedDelayMs != 0)
                {
                    try { _player.SetSpuDelay(savedDelayMs * 1000L); } catch { }
                }

                foreach (var subUri in savedSubs)
                {
                    try { _player.AddSlave(MediaSlaveType.Subtitle, subUri, true); } catch { }
                }

                if (!wasPlaying)
                {
                    try { _player.SetPause(true); } catch { }
                    IsPlaying = false;
                    IsPaused = true;
                }
                else
                {
                    IsPlaying = true;
                    IsPaused = false;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        _actionLog.Log("Video", "Rebound video output surface after headless/view switch.");
        RaiseStateChanged();
        TracksChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveCurrentProgressAsync(CancellationToken cancellationToken = default)
    {
        var itemId = _currentPlaylistItemId;
        if (!itemId.HasValue || itemId.Value <= 0)
        {
            return;
        }

        var player = _player;
        var posMs = PositionMs;
        var durMs = DurationMs;
        if (player is not null)
        {
            try
            {
                if (player.Time > 0)
                {
                    posMs = player.Time;
                }

                if (player.Length > 0)
                {
                    durMs = player.Length;
                }
            }
            catch
            {
                // Use cached position/duration.
            }
        }

        if (posMs <= 0)
        {
            return;
        }

        try
        {
            await _database.UpdateVideoItemProgressAsync(
                itemId.Value,
                posMs,
                durMs > 0 ? durMs : null,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Ignore progress write error on teardown.
        }
    }

    public async Task StopAndDisposeEngineAsync()
    {
        if (_disposed)
        {
            return;
        }

        await SaveCurrentProgressAsync(CancellationToken.None).ConfigureAwait(false);
        StopAutoSaveLoop();

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeEngineLocked();
        }
        finally
        {
            _gate.Release();
        }

        RaiseStateChanged();
        MemoryTrimService.ScheduleTrim();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        BandwidthMonitor.RegisterByteProvider(_bandwidthProviderKey, null);

        try
        {
            SaveCurrentProgressAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
        }

        StopAutoSaveLoop();
        _gate.Wait();
        try
        {
            DisposeEngineLocked();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        BandwidthMonitor.RegisterByteProvider(_bandwidthProviderKey, null);
        await SaveCurrentProgressAsync(CancellationToken.None).ConfigureAwait(false);
        StopAutoSaveLoop();

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeEngineLocked();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void StartAutoSaveLoop()
    {
        StopAutoSaveLoop();
        if (_disposed || !_currentPlaylistItemId.HasValue)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _autoSaveCts = cts;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            try
            {
                while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
                {
                    if (IsPlaying)
                    {
                        await SaveCurrentProgressAsync(cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void StopAutoSaveLoop()
    {
        var current = Interlocked.Exchange(ref _autoSaveCts, null);
        if (current is null)
        {
            return;
        }

        try { current.Cancel(); } catch (ObjectDisposedException) { }
        finally { current.Dispose(); }
    }

    private long GetCumulativeReadBytes()
    {
        var archived = Interlocked.Read(ref _archivedReadBytes);
        var media = Volatile.Read(ref _currentMedia);
        if (media is null)
        {
            return archived;
        }

        try
        {
            var stats = media.Statistics;
            long read = Math.Max(stats.ReadBytes, stats.DemuxReadBytes);
            var current = Interlocked.Read(ref _currentMediaLastReadBytes);
            if (read > current)
            {
                Interlocked.Exchange(ref _currentMediaLastReadBytes, read);
                current = read;
            }

            return archived + current;
        }
        catch
        {
            return archived + Interlocked.Read(ref _currentMediaLastReadBytes);
        }
    }

    private void SnapshotCurrentMediaReadBytesLocked()
    {
        var media = _currentMedia;
        long latest = Interlocked.Exchange(ref _currentMediaLastReadBytes, 0);
        if (media is not null)
        {
            try
            {
                var stats = media.Statistics;
                long read = Math.Max(stats.ReadBytes, stats.DemuxReadBytes);
                if (read > latest)
                {
                    latest = read;
                }
            }
            catch
            {
                // Ignore if native media is tearing down.
            }
        }

        if (latest > 0)
        {
            Interlocked.Add(ref _archivedReadBytes, latest);
        }
    }

    private void EnsureInitializedLocked()
    {
        if (!_coreInitialized)
        {
            Core.Initialize();
            _coreInitialized = true;
        }

        _libVlc ??= new LibVLC(
            "--quiet",
            "--no-osd",
            "--avcodec-hw=any",
            "--network-caching=2500",
            "--http-reconnect");

        if (_player is null)
        {
            _player = new MediaPlayer(_libVlc);
            _player.Playing += OnPlayerPlaying;
            _player.Paused += OnPlayerPaused;
            _player.Stopped += OnPlayerStopped;
            _player.EndReached += OnPlayerEndReached;
            _player.EncounteredError += OnPlayerError;
            _player.TimeChanged += OnPlayerTimeChanged;
            _player.LengthChanged += OnPlayerLengthChanged;
            _player.Buffering += OnPlayerBuffering;
            _player.ESAdded += OnPlayerEsChanged;
            _player.ESDeleted += OnPlayerEsChanged;
            _player.ESSelected += OnPlayerEsSelected;
        }
    }

    private void DisposeEngineLocked()
    {
        CancelIdleUnload();
        if (_player is not null)
        {
            _player.Playing -= OnPlayerPlaying;
            _player.Paused -= OnPlayerPaused;
            _player.Stopped -= OnPlayerStopped;
            _player.EndReached -= OnPlayerEndReached;
            _player.EncounteredError -= OnPlayerError;
            _player.TimeChanged -= OnPlayerTimeChanged;
            _player.LengthChanged -= OnPlayerLengthChanged;
            _player.Buffering -= OnPlayerBuffering;
            _player.ESAdded -= OnPlayerEsChanged;
            _player.ESDeleted -= OnPlayerEsChanged;
            _player.ESSelected -= OnPlayerEsSelected;

            try
            {
                _player.Stop();
            }
            catch
            {
                // Ignore stop failure on teardown.
            }

            _player.Dispose();
            _player = null;
        }

        SnapshotCurrentMediaReadBytesLocked();
        _currentMedia?.Dispose();
        _currentMedia = null;

        _libVlc?.Dispose();
        _libVlc = null;

        IsPlaying = false;
        IsPaused = false;
        IsBuffering = false;
        IsHeadlessAudioOnly = false;
        BufferingPercent = 0;
        PositionMs = 0;
        DurationMs = 0;
        _subtitleDelayMs = 0;
        _abLoopStartMs = null;
        _abLoopEndMs = null;
        _aspectRatioIndex = 0;
        _savedVideoTrackId = -1;
        _currentPlaylistItemId = null;
        _currentTitle = null;
        _currentSourceUrl = null;
        _currentResolvedMedia = null;
        _cachedAudioOnlyUrl = null;
        _activeExternalSubUris.Clear();
        _headlessMode = VideoHeadlessMode.KeepVideoStream;
        _needsVideoSurfaceRebind = false;
    }

    private Media BuildMediaLocked(ResolvedVideoMedia resolved, long startPositionMs, bool headlessAudioOnly)
    {
        var media = resolved.IsLocalFile
            ? new Media(_libVlc!, resolved.PlayableUrl, FromType.FromPath)
            : new Media(_libVlc!, resolved.PlayableUrl, FromType.FromLocation);

        if (!string.IsNullOrWhiteSpace(resolved.Referer))
        {
            media.AddOption($":http-referrer={resolved.Referer}");
        }

        if (!string.IsNullOrWhiteSpace(resolved.AudioSlaveUrl))
        {
            media.AddOption($":input-slave={resolved.AudioSlaveUrl}");
        }

        if (!resolved.IsLocalFile)
        {
            if (QualityMaxHeight is { } maxHeight)
            {
                media.AddOption($":adaptive-maxheight={maxHeight}");
                media.AddOption(maxHeight <= 360 ? ":adaptive-logic=lowest" : ":adaptive-logic=rate");
            }
            else
            {
                media.AddOption(":adaptive-logic=highest");
            }
        }

        if (startPositionMs >= 1000)
        {
            var startSec = startPositionMs / 1000.0;
            media.AddOption($":start-time={startSec.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        if (headlessAudioOnly)
        {
            media.AddOption(":no-video");
        }

        return media;
    }

    private static string NormalizeQualityPreset(string? preset) =>
        preset?.Trim().ToLowerInvariant() switch
        {
            "1080" or "1080p" => "1080",
            "720" or "720p" => "720",
            "480" or "480p" => "480",
            "360" or "360p" => "360",
            "240" or "240p" => "240",
            _ => "best"
        };

    private void RaiseStateChanged() =>
        PlaybackStateChanged?.Invoke(this, GetSnapshot());

    private void ScheduleIdleUnload()
    {
        CancelIdleUnload();
        if (_disposed || !IsInitialized)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _idleUnloadCts, cts);
        if (prev is not null)
        {
            try { prev.Cancel(); } catch { }
            prev.Dispose();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10), cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested && !_disposed && !IsPlaying && IsInitialized)
                {
                    await StopAndDisposeEngineAsync().ConfigureAwait(false);
                    _actionLog.Log("Video", "Auto-unloaded idle LibVLC video engine after 10 minutes.");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        });
    }

    private void CancelIdleUnload()
    {
        var cts = Interlocked.Exchange(ref _idleUnloadCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    private void OnPlayerPlaying(object? sender, EventArgs e)
    {
        CancelIdleUnload();
        IsPlaying = true;
        IsPaused = false;
        IsBuffering = false;
        RaiseStateChanged();
    }

    private void OnPlayerPaused(object? sender, EventArgs e)
    {
        IsPlaying = false;
        IsPaused = true;
        ScheduleIdleUnload();
        RaiseStateChanged();
    }

    private void OnPlayerStopped(object? sender, EventArgs e)
    {
        IsPlaying = false;
        IsPaused = false;
        IsBuffering = false;
        ScheduleIdleUnload();
        RaiseStateChanged();
    }

    private void OnPlayerEndReached(object? sender, EventArgs e)
    {
        var finishedItemId = _currentPlaylistItemId;
        IsPlaying = false;
        IsPaused = false;
        IsBuffering = false;
        PositionMs = 0;

        // Always offload database, loop replay & auto-advance off LibVLC's internal event thread.
        _ = Task.Run(async () =>
        {
            if (_loopMode == VideoLoopMode.One && _currentResolvedMedia is not null && !_disposed)
            {
                try
                {
                    await ReplayCurrentFromStartAsync().ConfigureAwait(false);
                    return;
                }
                catch
                {
                    // Fall through cleanly if replay fails.
                }
            }

            if (finishedItemId is > 0)
            {
                try
                {
                    await _database.UpdateVideoItemProgressAsync(
                        finishedItemId.Value,
                        0,
                        DurationMs > 0 ? DurationMs : null,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            ScheduleIdleUnload();
            RaiseStateChanged();
            EndReached?.Invoke(this, finishedItemId);
        });
    }

    private async Task ReplayCurrentFromStartAsync()
    {
        if (_disposed || _player is null || _currentResolvedMedia is not { } resolved)
        {
            return;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed || _player is null || _currentResolvedMedia is not { } cur)
            {
                return;
            }

            var savedSubs = _activeExternalSubUris.ToList();
            var savedDelayMs = _subtitleDelayMs;
            var useAudioOnlyUrl = IsHeadlessAudioOnly &&
                                  _headlessMode == VideoHeadlessMode.AudioOnlyStream &&
                                  (!string.IsNullOrWhiteSpace(cur.AudioSlaveUrl) || !string.IsNullOrWhiteSpace(_cachedAudioOnlyUrl));
            var audioOnlyUrl = cur.AudioSlaveUrl ?? _cachedAudioOnlyUrl;

            await Task.Run(() =>
            {
                try { _player.Stop(); } catch { }

                SnapshotCurrentMediaReadBytesLocked();
                _currentMedia?.Dispose();
                _currentMedia = null;

                Media media;
                if (useAudioOnlyUrl && !string.IsNullOrWhiteSpace(audioOnlyUrl))
                {
                    media = new Media(_libVlc!, audioOnlyUrl, FromType.FromLocation);
                    if (!string.IsNullOrWhiteSpace(cur.Referer))
                    {
                        media.AddOption($":http-referrer={cur.Referer}");
                    }

                    media.AddOption(":no-video");
                }
                else
                {
                    media = BuildMediaLocked(cur, 0, IsHeadlessAudioOnly);
                }

                _currentMedia = media;
                PositionMs = 0;

                _player.Volume = _volume;
                _player.Mute = _isMuted;
                try { _player.AspectRatio = SupportedAspectRatios[_aspectRatioIndex].VlcValue; } catch { }
                _player.Play(media);
                _player.SetRate(_playbackSpeed);

                if (savedDelayMs != 0)
                {
                    try { _player.SetSpuDelay(savedDelayMs * 1000L); } catch { }
                }

                foreach (var subUri in savedSubs)
                {
                    try { _player.AddSlave(MediaSlaveType.Subtitle, subUri, true); } catch { }
                }

                IsPlaying = true;
                IsPaused = false;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        RaiseStateChanged();
    }

    private void OnPlayerError(object? sender, EventArgs e)
    {
        IsPlaying = false;
        IsBuffering = false;
        var title = _currentTitle ?? "unknown";
        _actionLog.Log("Error", $"LibVLC video error on '{title}'.");
        _ = Task.Run(() =>
        {
            RaiseStateChanged();
            PlaybackError?.Invoke(this, $"LibVLC encountered a video playback error on '{title}'.");
        });
    }

    private void OnPlayerTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        var time = Math.Max(0, e.Time);
        PositionMs = time;
        PositionChanged?.Invoke(this, time);

        if (_abLoopStartMs is { } start &&
            _abLoopEndMs is { } end &&
            end > start &&
            time >= end &&
            Interlocked.CompareExchange(ref _abSeekInFlight, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var player = _player;
                    if (player is not null && !_disposed)
                    {
                        player.Time = start;
                        PositionMs = start;
                        PositionChanged?.Invoke(this, start);
                    }

                    await Task.Delay(250).ConfigureAwait(false);
                }
                catch
                {
                }
                finally
                {
                    Interlocked.Exchange(ref _abSeekInFlight, 0);
                }
            });
        }
    }

    private void OnPlayerLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        if (e.Length > 0)
        {
            DurationMs = e.Length;
            DurationChanged?.Invoke(this, DurationMs);
        }
    }

    private void OnPlayerBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        BufferingPercent = Math.Clamp(e.Cache, 0f, 100f);
        var nowBuffering = BufferingPercent < 99.5f;
        var changed = IsBuffering != nowBuffering;
        IsBuffering = nowBuffering;
        BufferingChanged?.Invoke(this, BufferingPercent);
        if (changed)
        {
            RaiseStateChanged();
        }
    }

    private void OnPlayerEsChanged(object? sender, MediaPlayerESAddedEventArgs e) =>
        TracksChanged?.Invoke(this, EventArgs.Empty);

    private void OnPlayerEsChanged(object? sender, MediaPlayerESDeletedEventArgs e) =>
        TracksChanged?.Invoke(this, EventArgs.Empty);

    private void OnPlayerEsSelected(object? sender, MediaPlayerESSelectedEventArgs e) =>
        TracksChanged?.Invoke(this, EventArgs.Empty);

    private static async Task<string> EnsureUtf8SubtitleFileAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        var raw = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (raw.Length == 0)
        {
            return fullPath;
        }

        // UTF-8 BOM already present -> pass original file directly.
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
        {
            return fullPath;
        }

        string decoded;
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
        {
            decoded = Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
        }
        else if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
        {
            decoded = Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);
        }
        else
        {
            try
            {
                var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                strictUtf8.GetString(raw);
                return fullPath;
            }
            catch (DecoderFallbackException)
            {
                decoded = DecodeLegacySubtitleBytes(raw);
            }
        }

        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "REM",
            "subtitles-utf8");
        Directory.CreateDirectory(cacheDir);

        var info = new FileInfo(fullPath);
        var sigBytes = Encoding.UTF8.GetBytes($"{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        var hashHex = Convert.ToHexString(SHA256.HashData(sigBytes)).Substring(0, 16);
        var ext = Path.GetExtension(fullPath);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".srt";
        }

        var outPath = Path.Combine(cacheDir, $"{hashHex}{ext}");
        await File.WriteAllTextAsync(outPath, decoded, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken)
            .ConfigureAwait(false);
        return outPath;
    }

    private static string DecodeLegacySubtitleBytes(byte[] raw)
    {
        // Heuristic: detect Arabic Windows-1256 vs Western Windows-1252.
        var arabicHighBytes = 0;
        var totalHighBytes = 0;
        foreach (var b in raw)
        {
            if (b >= 0x80)
            {
                totalHighBytes++;
                if ((b >= 0xC1 && b <= 0xDA) || (b >= 0xE1 && b <= 0xFA))
                {
                    arabicHighBytes++;
                }
            }
        }

        var useCp1256 = totalHighBytes > 0 && arabicHighBytes * 2 >= totalHighBytes;
        var sb = new StringBuilder(raw.Length);
        foreach (var b in raw)
        {
            sb.Append(useCp1256 ? DecodeCp1256Byte(b) : DecodeCp1252Byte(b));
        }

        return sb.ToString();
    }

    private static char DecodeCp1252Byte(byte b) => b switch
    {
        < 0x80 => (char)b,
        0x80 => '\u20AC', 0x82 => '\u201A', 0x83 => '\u0192', 0x84 => '\u201E',
        0x85 => '\u2026', 0x86 => '\u2020', 0x87 => '\u2021', 0x88 => '\u02C6',
        0x89 => '\u2030', 0x8A => '\u0160', 0x8B => '\u2039', 0x8C => '\u0152',
        0x8E => '\u017D', 0x91 => '\u2018', 0x92 => '\u2019', 0x93 => '\u201C',
        0x94 => '\u201D', 0x95 => '\u2022', 0x96 => '\u2013', 0x97 => '\u2014',
        0x98 => '\u02DC', 0x99 => '\u2122', 0x9A => '\u0161', 0x9B => '\u203A',
        0x9C => '\u0153', 0x9E => '\u017E', 0x9F => '\u0178',
        _ => (char)b
    };

    private static char DecodeCp1256Byte(byte b) => b switch
    {
        < 0x80 => (char)b,
        0x80 => '\u20AC', 0x81 => '\u067E', 0x82 => '\u201A', 0x83 => '\u0192',
        0x84 => '\u201E', 0x85 => '\u2026', 0x86 => '\u2020', 0x87 => '\u2021',
        0x88 => '\u02C6', 0x89 => '\u2030', 0x8A => '\u0679', 0x8B => '\u2039',
        0x8C => '\u0152', 0x8D => '\u0686', 0x8E => '\u0698', 0x8F => '\u0688',
        0x90 => '\u06AF', 0x91 => '\u2018', 0x92 => '\u2019', 0x93 => '\u201C',
        0x94 => '\u201D', 0x95 => '\u2022', 0x96 => '\u2013', 0x97 => '\u2014',
        0x98 => '\u06A9', 0x99 => '\u2122', 0x9A => '\u0691', 0x9B => '\u203A',
        0x9C => '\u0153', 0x9D => '\u200C', 0x9E => '\u200D', 0x9F => '\u06BA',
        0xA1 => '\u060C', 0xAA => '\u06BE', 0xBA => '\u061B', 0xBF => '\u061F',
        0xC0 => '\u06C1', 0xC1 => '\u0621', 0xC2 => '\u0622', 0xC3 => '\u0623',
        0xC4 => '\u0624', 0xC5 => '\u0625', 0xC6 => '\u0626', 0xC7 => '\u0627',
        0xC8 => '\u0628', 0xC9 => '\u0629', 0xCA => '\u062A', 0xCB => '\u062B',
        0xCC => '\u062C', 0xCD => '\u062D', 0xCE => '\u062E', 0xCF => '\u062F',
        0xD0 => '\u0630', 0xD1 => '\u0631', 0xD2 => '\u0632', 0xD3 => '\u0633',
        0xD4 => '\u0634', 0xD5 => '\u0635', 0xD6 => '\u0636', 0xD8 => '\u0637',
        0xD9 => '\u0638', 0xDA => '\u0639', 0xDB => '\u063A', 0xDC => '\u0640',
        0xDD => '\u0641', 0xDE => '\u0642', 0xDF => '\u0643', 0xE0 => '\u00E0',
        0xE1 => '\u0644', 0xE2 => '\u00E2', 0xE3 => '\u0645', 0xE4 => '\u0646',
        0xE5 => '\u0647', 0xE6 => '\u0648', 0xE7 => '\u00E7', 0xE8 => '\u00E8',
        0xE9 => '\u00E9', 0xEA => '\u00EA', 0xEB => '\u00EB', 0xEC => '\u0649',
        0xED => '\u064A', 0xEE => '\u00EE', 0xEF => '\u00EF', 0xF0 => '\u064B',
        0xF1 => '\u064C', 0xF2 => '\u064D', 0xF3 => '\u064E', 0xF4 => '\u00F4',
        0xF5 => '\u064F', 0xF6 => '\u0650', 0xF8 => '\u0651', 0xF9 => '\u00F9',
        0xFA => '\u0652', 0xFB => '\u00FB', 0xFC => '\u00FC', 0xFD => '\u200E',
        0xFE => '\u200F', 0xFF => '\u06D2',
        _ => (char)b
    };
}
