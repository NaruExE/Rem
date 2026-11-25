using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using REM.Models;
using REM.Modules;
using REM.Services;

namespace REM.ViewModels;

public sealed partial class MediaStudioJobItemViewModel : ObservableObject
{
    private static readonly HashSet<string> PlayableExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".mp3", ".m4a", ".wav", ".flac", ".opus"
    };

    private readonly Action<long> _onCancel;
    private readonly Action<string> _onOpenFolder;
    private readonly Action<string> _onPlayInVlc;

    public MediaStudioJobItemViewModel(
        MediaStudioJobState state,
        Action<long> onCancel,
        Action<string> onOpenFolder,
        Action<string> onPlayInVlc)
    {
        JobId = state.Id;
        OutputPath = state.OutputPath;
        InputPath = state.InputPath;
        Title = state.Title;
        ModeLabel = state.ModeLabel;
        _onCancel = onCancel;
        _onOpenFolder = onOpenFolder;
        _onPlayInVlc = onPlayInVlc;

        SyncFromState(state);
    }

    public long JobId { get; }
    public string InputPath { get; }
    public string OutputPath { get; }
    public string Title { get; }
    public string ModeLabel { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsProbing))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CanOpenFolder))]
    [NotifyPropertyChangedFor(nameof(CanPlayInVlc))]
    [NotifyPropertyChangedFor(nameof(ShowProgress))]
    private MediaStudioJobStatus _status;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetricsSummary))]
    private string _speedText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetricsSummary))]
    private string _etaText = string.Empty;

    [ObservableProperty]
    private string _statusText = "Queued";

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsRunning => Status == MediaStudioJobStatus.Running;
    public bool IsProbing => Status == MediaStudioJobStatus.Probing;
    public bool IsCompleted => Status == MediaStudioJobStatus.Completed;
    public bool IsFailed => Status == MediaStudioJobStatus.Failed;
    public bool CanCancel => Status is MediaStudioJobStatus.Queued or MediaStudioJobStatus.Probing or MediaStudioJobStatus.Running;
    public bool CanOpenFolder => IsCompleted && File.Exists(OutputPath);
    public bool CanPlayInVlc => IsCompleted && File.Exists(OutputPath) && PlayableExts.Contains(Path.GetExtension(OutputPath));
    public bool ShowProgress => Status is MediaStudioJobStatus.Probing or MediaStudioJobStatus.Running;

    public string MetricsSummary
    {
        get
        {
            if (IsCompleted)
            {
                return StatusText;
            }

            if (!string.IsNullOrWhiteSpace(SpeedText) && !string.IsNullOrWhiteSpace(EtaText))
            {
                return $"{ProgressPercent:F0}% · {SpeedText} · ETA {EtaText}";
            }

            if (!string.IsNullOrWhiteSpace(SpeedText))
            {
                return $"{ProgressPercent:F0}% · {SpeedText}";
            }

            return StatusText;
        }
    }

    public void SyncFromState(MediaStudioJobState state)
    {
        Status = state.Status;
        ProgressPercent = state.ProgressPercent;
        SpeedText = state.SpeedText;
        EtaText = state.EtaText;
        StatusText = state.StatusText;
        ErrorMessage = state.ErrorMessage;
        OnPropertyChanged(nameof(MetricsSummary));
    }

    [RelayCommand]
    private void Cancel() => _onCancel(JobId);

    [RelayCommand]
    private void OpenFolder() => _onOpenFolder(OutputPath);

    [RelayCommand]
    private void PlayInVlc() => _onPlayInVlc(OutputPath);
}

public sealed partial class MediaStudioViewModel : ObservableObject, IDisposable
{
    private readonly MediaStudioModule _module;
    private readonly DatabaseService _database;
    private readonly FfmpegStudioService _studio;
    private readonly ActionLogService _actionLog;
    private readonly List<string> _selectedFiles = [];
    private CancellationTokenSource? _probeCts;
    private bool _disposed;

    public MediaStudioViewModel(
        MediaStudioModule module,
        DatabaseService database,
        FfmpegStudioService studio,
        ActionLogService actionLog)
    {
        _module = module;
        _database = database;
        _studio = studio;
        _actionLog = actionLog;

        AccentBrush = new SolidColorBrush(Color.Parse(module.AccentHex));
        _module.StateChanged += OnModuleStateChanged;
        _module.StatusSummaryChanged += OnModuleStatusChanged;
        _studio.JobsChanged += OnJobsChanged;
        _studio.JobUpdated += OnJobUpdated;

        RefreshJobs();
        SyncModuleHeader();
    }

    public string Name => _module.Name;
    public Geometry IconGeometry => _module.IconGeometry;
    public IBrush AccentBrush { get; }

    public Action? RequestCloseToDashboard { get; set; }
    public Func<Task<IReadOnlyList<string>>>? PickFilesRequested { get; set; }
    public Func<Task<string?>>? PickFolderRequested { get; set; }

    public ObservableCollection<MediaStudioJobItemViewModel> Jobs { get; } = [];
    public IReadOnlyList<string> VideoFormatOptions { get; } = ["mp4", "mkv", "webm", "gif"];
    public IReadOnlyList<string> AudioFormatOptions { get; } = ["mp3", "m4a", "wav", "flac", "opus"];
    public IReadOnlyList<string> ImageFormatOptions { get; } = ["webp", "png", "jpg"];
    public IReadOnlyList<string> AudioBitrateOptions { get; } = ["128", "192", "256", "320"];
    public IReadOnlyList<string> VideoQualityOptions { get; } = ["High (CRF 18)", "Balanced (CRF 22)", "Compact (CRF 26)"];
    public IReadOnlyList<string> MaxWidthOptions { get; } = ["Original", "1920", "1280", "854", "640", "480"];

    [ObservableProperty]
    private bool _isHeaderVisible = true;

    [ObservableProperty]
    private string _stateLabel = "Running (UI)";

    [ObservableProperty]
    private string _statusSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDiscordMode))]
    [NotifyPropertyChangedFor(nameof(IsTrimMode))]
    [NotifyPropertyChangedFor(nameof(IsVideoMode))]
    [NotifyPropertyChangedFor(nameof(IsAudioMode))]
    [NotifyPropertyChangedFor(nameof(IsImageMode))]
    private MediaStudioMode _selectedMode = MediaStudioMode.DiscordCompress;

    public bool IsDiscordMode => SelectedMode == MediaStudioMode.DiscordCompress;
    public bool IsTrimMode => SelectedMode == MediaStudioMode.TrimClip;
    public bool IsVideoMode => SelectedMode == MediaStudioMode.ConvertVideo;
    public bool IsAudioMode => SelectedMode == MediaStudioMode.ConvertAudio;
    public bool IsImageMode => SelectedMode == MediaStudioMode.ConvertImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFiles))]
    private string _selectedFilesLabel = "No media selected yet — drop files here or click Browse";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProbeSummary))]
    private string _probeSummary = string.Empty;

    [ObservableProperty]
    private bool _isProbing;

    [ObservableProperty]
    private string _outputFolder = string.Empty;

    [ObservableProperty]
    private string _targetSizeMbText = "10";

    [ObservableProperty]
    private string _trimStartText = "00:00:00";

    [ObservableProperty]
    private string _trimEndText = "";

    [ObservableProperty]
    private bool _useStreamCopy = true;

    [ObservableProperty]
    private string _selectedVideoFormat = "mp4";

    [ObservableProperty]
    private string _selectedAudioFormat = "mp3";

    [ObservableProperty]
    private string _selectedImageFormat = "webp";

    [ObservableProperty]
    private string _selectedAudioBitrate = "192";

    [ObservableProperty]
    private string _selectedVideoQuality = "Balanced (CRF 22)";

    [ObservableProperty]
    private string _selectedMaxWidth = "Original";

    [ObservableProperty]
    private bool _stripMetadata = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusNotice))]
    private string _statusNotice = string.Empty;

    [ObservableProperty]
    private bool _isJobsEmpty = true;

    public bool HasSelectedFiles => _selectedFiles.Count > 0;
    public bool HasProbeSummary => !string.IsNullOrWhiteSpace(ProbeSummary);
    public bool HasStatusNotice => !string.IsNullOrWhiteSpace(StatusNotice);

    [RelayCommand]
    private void SelectDiscordMode() => SelectedMode = MediaStudioMode.DiscordCompress;

    [RelayCommand]
    private void SelectTrimMode() => SelectedMode = MediaStudioMode.TrimClip;

    [RelayCommand]
    private void SelectVideoMode() => SelectedMode = MediaStudioMode.ConvertVideo;

    [RelayCommand]
    private void SelectAudioMode() => SelectedMode = MediaStudioMode.ConvertAudio;

    [RelayCommand]
    private void SelectImageMode() => SelectedMode = MediaStudioMode.ConvertImage;

    [RelayCommand]
    private void SetDiscordPresetMb(string mb) => TargetSizeMbText = mb;

    [RelayCommand]
    private async Task BrowseInputFilesAsync()
    {
        if (PickFilesRequested is null)
        {
            return;
        }

        var files = await PickFilesRequested();
        if (files is { Count: > 0 })
        {
            SetInputFiles(files);
        }
    }

    public void SetInputFiles(IEnumerable<string> paths)
    {
        var valid = paths
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (valid.Count == 0)
        {
            return;
        }

        _selectedFiles.Clear();
        _selectedFiles.AddRange(valid);
        SelectedFilesLabel = valid.Count == 1
            ? Path.GetFileName(valid[0])
            : $"{valid.Count} files selected ({Path.GetFileName(valid[0])}, ...)";
        StatusNotice = string.Empty;

        _ = ProbeFirstSelectedAsync(valid[0]);
    }

    private async Task ProbeFirstSelectedAsync(string path)
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        var cts = new CancellationTokenSource();
        _probeCts = cts;

        IsProbing = true;
        ProbeSummary = "Inspecting media with ffprobe...";
        try
        {
            var probe = await _studio.ProbeAsync(path, cts.Token);
            if (_disposed || cts.IsCancellationRequested)
            {
                return;
            }

            if (probe is null)
            {
                ProbeSummary = MediaProbeInfo.FormatBytes(new FileInfo(path).Length);
                return;
            }

            ProbeSummary = probe.SummaryText;
            if (probe.DurationSeconds > 0 && string.IsNullOrWhiteSpace(TrimEndText))
            {
                TrimEndText = probe.DurationLabel;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ProbeSummary = $"Probe unavailable: {ex.Message}";
        }
        finally
        {
            IsProbing = false;
        }
    }

    [RelayCommand]
    private async Task BrowseOutputFolderAsync()
    {
        if (PickFolderRequested is null)
        {
            return;
        }

        var folder = await PickFolderRequested();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            OutputFolder = folder;
        }
    }

    [RelayCommand]
    private void ClearOutputFolder() => OutputFolder = string.Empty;

    [RelayCommand]
    private void StartConversion()
    {
        if (_selectedFiles.Count == 0)
        {
            StatusNotice = "Pick or drop at least one input file first.";
            return;
        }

        var targetMb = double.TryParse(TargetSizeMbText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mb)
            ? Math.Clamp(mb, 1.0, 500.0)
            : 10.0;
        var audioBitrate = int.TryParse(SelectedAudioBitrate, out var ab) ? ab : 192;
        var crf = SelectedVideoQuality.Contains("18", StringComparison.Ordinal) ? 18
            : SelectedVideoQuality.Contains("26", StringComparison.Ordinal) ? 26
            : 22;
        var maxWidth = int.TryParse(SelectedMaxWidth, out var mw) ? mw : 0;

        var format = SelectedMode switch
        {
            MediaStudioMode.ConvertVideo => SelectedVideoFormat,
            MediaStudioMode.ConvertAudio => SelectedAudioFormat,
            MediaStudioMode.ConvertImage => SelectedImageFormat,
            _ => "mp4"
        };

        var queuedCount = 0;
        foreach (var input in _selectedFiles)
        {
            var ext = SelectedMode switch
            {
                MediaStudioMode.TrimClip => Path.GetExtension(input),
                MediaStudioMode.DiscordCompress => ".mp4",
                _ => $".{format.ToLowerInvariant()}"
            };
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = ".mp4";
            }

            var suffix = SelectedMode switch
            {
                MediaStudioMode.DiscordCompress => $"_discord{targetMb:0.#}mb",
                MediaStudioMode.TrimClip => "_clip",
                _ => "_converted"
            };

            var dir = !string.IsNullOrWhiteSpace(OutputFolder) && Directory.Exists(OutputFolder)
                ? OutputFolder
                : Path.GetDirectoryName(input) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var baseName = Path.GetFileNameWithoutExtension(input);
            var outPath = EnsureUniquePath(Path.Combine(dir, $"{baseName}{suffix}{ext}"));

            _studio.EnqueueJob(new MediaStudioJobRequest(
                InputPath: input,
                OutputPath: outPath,
                Mode: SelectedMode,
                TargetSizeMb: targetMb,
                TrimStart: TrimStartText,
                TrimEnd: TrimEndText,
                StreamCopy: UseStreamCopy,
                OutputFormat: format,
                AudioBitrateKbps: audioBitrate,
                VideoCrf: crf,
                MaxWidth: maxWidth,
                StripMetadata: StripMetadata));
            queuedCount++;
        }

        StatusNotice = $"Queued {queuedCount} job{(queuedCount == 1 ? "" : "s")}.";
    }

    [RelayCommand]
    private void ClearFinished() => _studio.ClearFinishedJobs();

    [RelayCommand]
    private void MinimizeToHeadless() => _module.RunHeadless();

    [RelayCommand]
    private void PauseModule() => _module.Pause();

    [RelayCommand]
    private void Kill() => _module.Kill();

    [RelayCommand]
    private void BackToDashboard() => RequestCloseToDashboard?.Invoke();

    [RelayCommand]
    private void ToggleHeader()
    {
        var next = !IsHeaderVisible;
        IsHeaderVisible = next;
        _database.SaveSetting(DatabaseService.KeyShowAppHeaderBar, next ? "true" : "false");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _module.StateChanged -= OnModuleStateChanged;
        _module.StatusSummaryChanged -= OnModuleStatusChanged;
        _studio.JobsChanged -= OnJobsChanged;
        _studio.JobUpdated -= OnJobUpdated;
    }

    private static string EnsureUniquePath(string candidate)
    {
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var dir = Path.GetDirectoryName(candidate) ?? ".";
        var name = Path.GetFileNameWithoutExtension(candidate);
        var ext = Path.GetExtension(candidate);
        for (var i = 2; i < 1000; i++)
        {
            var next = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(next))
            {
                return next;
            }
        }

        return Path.Combine(dir, $"{name}_{DateTime.Now:HHmmss}{ext}");
    }

    private void OnJobsChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            Dispatcher.UIThread.Post(RefreshJobs);
        }
    }

    private void OnJobUpdated(object? sender, MediaStudioJobState state)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var existing = Jobs.FirstOrDefault(j => j.JobId == state.Id);
            if (existing is not null)
            {
                existing.SyncFromState(state);
            }
            else
            {
                RefreshJobs();
            }
        });
    }

    private void RefreshJobs()
    {
        var snapshot = _studio.GetJobsSnapshot();
        Jobs.Clear();
        foreach (var job in snapshot)
        {
            Jobs.Add(new MediaStudioJobItemViewModel(
                job,
                id => _studio.CancelJob(id),
                OpenInExplorer,
                path => _module.RequestPlayInVlc(path)));
        }

        IsJobsEmpty = Jobs.Count == 0;
    }

    private void OpenInExplorer(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(Path.GetDirectoryName(filePath)))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(filePath)}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _actionLog.Log("Warning", $"Could not open Explorer: {ex.Message}");
        }
    }

    private void OnModuleStateChanged(object? sender, ModuleState e) =>
        Dispatcher.UIThread.Post(SyncModuleHeader);

    private void OnModuleStatusChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(SyncModuleHeader);

    private void SyncModuleHeader()
    {
        StateLabel = _module.State switch
        {
            ModuleState.RunningUI => "Running (UI)",
            ModuleState.RunningHeadless => "Headless",
            ModuleState.Paused => "Paused",
            _ => "Stopped"
        };
        StatusSummary = _module.StatusSummary;
    }
}
