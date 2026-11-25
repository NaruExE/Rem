using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using REM.Models;
using REM.Modules;
using REM.Services;

namespace REM.ViewModels;

public enum MediaGrabberFilterTab
{
    Active,
    Completed,
    Failed
}

public sealed record DownloadEngineOption(DownloadEngineKind Kind, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class MediaGrabberJobItemViewModel : ObservableObject
{
    private static readonly HashSet<string> PlayableMediaExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".ts", ".flv", ".mp3", ".flac", ".wav", ".m4a", ".aac", ".opus"
    };

    private readonly Func<long, Task> _onPause;
    private readonly Func<long, Task> _onResume;
    private readonly Func<long, Task> _onCancel;
    private readonly Func<long, Task> _onRetry;
    private readonly Action<string?> _onOpenFolder;
    private readonly Action<string> _onPlayInRem;

    public MediaGrabberJobItemViewModel(
        DownloadJobState state,
        Func<long, Task> onPause,
        Func<long, Task> onResume,
        Func<long, Task> onCancel,
        Func<long, Task> onRetry,
        Action<string?> onOpenFolder,
        Action<string> onPlayInRem)
    {
        HistoryId = state.HistoryId;
        Url = state.Url;
        _onPause = onPause;
        _onResume = onResume;
        _onCancel = onCancel;
        _onRetry = onRetry;
        _onOpenFolder = onOpenFolder;
        _onPlayInRem = onPlayInRem;

        SyncFromState(state);
    }

    public long HistoryId { get; }
    public string Url { get; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _engineLabel = string.Empty;

    [ObservableProperty]
    private string _formatPreset = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenFolder))]
    [NotifyPropertyChangedFor(nameof(CanPlayInRem))]
    private string? _filePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(ShowProgress))]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyPropertyChangedFor(nameof(CanOpenFolder))]
    [NotifyPropertyChangedFor(nameof(CanPlayInRem))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private DownloadJobStatus _status;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _etaText = string.Empty;

    [ObservableProperty]
    private string _fileSizeDisplay = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    private bool _isAria2;

    public bool IsActive => Status is DownloadJobStatus.Queued or DownloadJobStatus.Probing or DownloadJobStatus.Downloading;
    public bool IsPaused => Status == DownloadJobStatus.Paused;
    public bool IsCompleted => Status == DownloadJobStatus.Completed;
    public bool IsFailed => Status is DownloadJobStatus.Failed or DownloadJobStatus.Cancelled;
    public bool ShowProgress => IsActive || IsPaused;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool CanPause => _isAria2 && Status == DownloadJobStatus.Downloading;
    public bool CanResume => _isAria2 && Status == DownloadJobStatus.Paused;
    public bool CanCancel => IsActive || IsPaused;
    public bool CanRetry => IsFailed;
    public bool CanOpenFolder => Status == DownloadJobStatus.Completed && !string.IsNullOrWhiteSpace(FilePath);
    public bool CanPlayInRem =>
        Status == DownloadJobStatus.Completed &&
        !string.IsNullOrWhiteSpace(FilePath) &&
        PlayableMediaExts.Contains(Path.GetExtension(FilePath));

    public string StatusLabel => Status switch
    {
        DownloadJobStatus.Queued => "Queued",
        DownloadJobStatus.Probing => "Probing...",
        DownloadJobStatus.Downloading => $"{ProgressPercent:F1}%",
        DownloadJobStatus.Paused => "Paused",
        DownloadJobStatus.Completed => "Completed",
        DownloadJobStatus.Cancelled => "Cancelled",
        DownloadJobStatus.Failed => "Failed",
        _ => Status.ToString()
    };

    public string MetricsSummary
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(SpeedText)) parts.Add(SpeedText);
            if (!string.IsNullOrWhiteSpace(EtaText)) parts.Add($"ETA {EtaText}");
            if (!string.IsNullOrWhiteSpace(FileSizeDisplay)) parts.Add(FileSizeDisplay);
            return string.Join(" · ", parts);
        }
    }

    public void SyncFromState(DownloadJobState state)
    {
        _isAria2 = state.Engine == DownloadEngineKind.Aria2c;
        Title = state.Title;
        EngineLabel = state.EngineLabel;
        FormatPreset = state.FormatPreset;
        FilePath = state.FilePath;
        ProgressPercent = state.ProgressPercent;
        SpeedText = state.SpeedText;
        EtaText = state.EtaText;
        FileSizeDisplay = state.FileSizeBytes > 0 ? FormatFileSize(state.FileSizeBytes) : string.Empty;
        ErrorMessage = state.ErrorMessage;
        Status = state.Status;

        OnPropertyChanged(nameof(MetricsSummary));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
    }

    [RelayCommand]
    private Task PauseAsync() => _onPause(HistoryId);

    [RelayCommand]
    private Task ResumeAsync() => _onResume(HistoryId);

    [RelayCommand]
    private Task CancelAsync() => _onCancel(HistoryId);

    [RelayCommand]
    private Task RetryAsync() => _onRetry(HistoryId);

    [RelayCommand]
    private void OpenFolder() => _onOpenFolder(FilePath);

    [RelayCommand]
    private void PlayInRem()
    {
        if (!string.IsNullOrWhiteSpace(FilePath))
        {
            _onPlayInRem(FilePath);
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

public sealed partial class MediaGrabberViewModel : ObservableObject, IDisposable
{
    private readonly MediaGrabberModule _module;
    private readonly DatabaseService _database;
    private readonly DownloadQueueService _queue;
    private readonly YtDlpDownloadService _ytDlpDownload;
    private readonly DialogService _dialogService;
    private readonly ActionLogService _actionLog;

    private long _profileId;
    private CancellationTokenSource? _toastCts;
    private bool _disposed;

    public MediaGrabberViewModel(
        MediaGrabberModule module,
        long profileId,
        DatabaseService database,
        DownloadQueueService queue,
        YtDlpDownloadService ytDlpDownload,
        DialogService dialogService,
        ActionLogService actionLog)
    {
        _module = module;
        _profileId = profileId;
        _database = database;
        _queue = queue;
        _ytDlpDownload = ytDlpDownload;
        _dialogService = dialogService;
        _actionLog = actionLog;

        AccentBrush = new SolidColorBrush(Color.Parse(module.AccentHex));

        EngineOptions =
        [
            new(DownloadEngineKind.Auto, "Auto"),
            new(DownloadEngineKind.YtDlp, "yt-dlp"),
            new(DownloadEngineKind.Aria2c, "aria2c")
        ];
        _selectedEngine = EngineOptions[0];

        FormatPresets = DownloadFormatPresets.All;
        _selectedFormatPreset = FormatPresets[0];

        _module.StateChanged += OnModuleStateChanged;
        _module.StatusSummaryChanged += OnModuleStatusSummaryChanged;
        _queue.JobsChanged += OnQueueJobsChanged;
        _queue.JobUpdated += OnQueueJobUpdated;

        RefreshModuleHeaderStatus();
        _ = InitializeViewAsync();
    }

    public string Name => _module.Name;
    public IBrush AccentBrush { get; }
    public Geometry IconGeometry => _module.IconGeometry;
    public IReadOnlyList<DownloadEngineOption> EngineOptions { get; }
    public IReadOnlyList<string> FormatPresets { get; }

    public ObservableCollection<MediaGrabberJobItemViewModel> FilteredJobs { get; } = [];
    private readonly List<MediaGrabberJobItemViewModel> _allCards = [];

    public Action? RequestCloseToDashboard { get; set; }
    public Func<Task<string?>>? PickFolderRequested { get; set; }
    public Func<string, Task<string?>>? PickExecutableRequested { get; set; }
    public Func<Task<string?>>? ReadClipboardTextRequested { get; set; }

    [ObservableProperty]
    private bool _isHeaderVisible = true;

    [ObservableProperty]
    private string _stateLabel = "Running (UI)";

    [ObservableProperty]
    private string _statusSummary = string.Empty;

    [ObservableProperty]
    private string _urlInput = string.Empty;

    [ObservableProperty]
    private DownloadEngineOption _selectedEngine;

    [ObservableProperty]
    private string _selectedFormatPreset;

    [ObservableProperty]
    private bool _isBatchPasteOpen;

    [ObservableProperty]
    private string _batchUrlsText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActiveTab))]
    [NotifyPropertyChangedFor(nameof(IsCompletedTab))]
    [NotifyPropertyChangedFor(nameof(IsFailedTab))]
    private MediaGrabberFilterTab _selectedTab = MediaGrabberFilterTab.Active;

    public bool IsActiveTab => SelectedTab == MediaGrabberFilterTab.Active;
    public bool IsCompletedTab => SelectedTab == MediaGrabberFilterTab.Completed;
    public bool IsFailedTab => SelectedTab == MediaGrabberFilterTab.Failed;

    [ObservableProperty]
    private int _activeCount;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private bool _isFilteredListEmpty = true;

    [ObservableProperty]
    private string _totalSpeedDisplay = "↓ 0 KB/s";

    [ObservableProperty]
    private string _activeCountSummary = "0 active";

    [ObservableProperty]
    private string _downloadFolderPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusNotice))]
    private string _statusNotice = string.Empty;

    public bool HasStatusNotice => !string.IsNullOrWhiteSpace(StatusNotice);

    // Settings Modal
    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private string _settingsDownloadFolder = string.Empty;

    [ObservableProperty]
    private string _settingsYtDlpPath = string.Empty;

    [ObservableProperty]
    private string _settingsAria2cPath = string.Empty;

    [ObservableProperty]
    private string _settingsFfmpegPath = string.Empty;

    [ObservableProperty]
    private int _settingsMaxConcurrent = 3;

    [ObservableProperty]
    private int _settingsAria2Connections = 16;

    [ObservableProperty]
    private bool _isUpdatingYtDlp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasYtDlpUpdateOutput))]
    private string _ytDlpUpdateOutput = string.Empty;

    public bool HasYtDlpUpdateOutput => !string.IsNullOrWhiteSpace(YtDlpUpdateOutput);

    public void OnProfileLoaded()
    {
        if (!_disposed)
        {
            _ = InitializeViewAsync();
        }
    }

    private async Task InitializeViewAsync()
    {
        try
        {
            var profileId = _queue.ActiveProfileId > 0 ? _queue.ActiveProfileId : _profileId;
            if (profileId > 0 && _queue.ActiveProfileId != profileId)
            {
                await _queue.LoadProfileAsync(profileId);
            }

            if (profileId > 0)
            {
                var settings = await _database.GetMediaGrabberSettingsAsync(profileId);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!_disposed)
                    {
                        DownloadFolderPath = settings.DefaultDownloadFolder;
                    }
                });
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    RebuildAllCardsFromQueue();
                }
            });
        }
        catch (Exception ex)
        {
            _actionLog.Log("Warning", $"Media Grabber init error: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleHeader() => IsHeaderVisible = !IsHeaderVisible;

    [RelayCommand]
    private void BackToDashboard() => RequestCloseToDashboard?.Invoke();

    [RelayCommand]
    private void MinimizeToHeadless()
    {
        var close = RequestCloseToDashboard;
        _module.RunHeadless();
        close?.Invoke();
    }

    [RelayCommand]
    private void PauseModule()
    {
        var close = RequestCloseToDashboard;
        _module.Pause();
        close?.Invoke();
    }

    [RelayCommand]
    private void Kill()
    {
        var close = RequestCloseToDashboard;
        _module.Kill();
        close?.Invoke();
    }

    [RelayCommand]
    private async Task PasteClipboardAsync()
    {
        if (ReadClipboardTextRequested is null) return;
        var text = await ReadClipboardTextRequested();
        if (!string.IsNullOrWhiteSpace(text))
        {
            UrlInput = text.Trim();
        }
    }

    [RelayCommand]
    private void ToggleBatchPaste() => IsBatchPasteOpen = !IsBatchPasteOpen;

    [RelayCommand]
    private async Task GrabAsync()
    {
        var raw = UrlInput.Trim();
        if (string.IsNullOrWhiteSpace(raw) || _disposed)
        {
            return;
        }

        UrlInput = string.Empty;
        try
        {
            await _queue.EnqueueAsync(raw, titleOverride: null, SelectedEngine.Kind, SelectedFormatPreset);
            SelectedTab = MediaGrabberFilterTab.Active;
            ApplyFilterAndCounts();
            ShowToast("Queued download.");
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Download Queue Error", "Could not queue download.", ex.Message);
        }
    }

    [RelayCommand]
    private async Task GrabBatchAsync()
    {
        var raw = BatchUrlsText;
        if (string.IsNullOrWhiteSpace(raw) || _disposed)
        {
            return;
        }

        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (lines.Count == 0)
        {
            return;
        }

        BatchUrlsText = string.Empty;
        IsBatchPasteOpen = false;
        SelectedTab = MediaGrabberFilterTab.Active;

        var queued = 0;
        foreach (var line in lines)
        {
            try
            {
                await _queue.EnqueueAsync(line, titleOverride: null, SelectedEngine.Kind, SelectedFormatPreset);
                queued++;
            }
            catch (Exception ex)
            {
                _actionLog.Log("Warning", $"Batch queue skipped '{line}': {ex.Message}");
            }
        }

        ApplyFilterAndCounts();
        ShowToast($"Queued {queued} batch download{(queued == 1 ? "" : "s")}.");
    }

    [RelayCommand]
    private void SelectActiveTab()
    {
        SelectedTab = MediaGrabberFilterTab.Active;
        ApplyFilterAndCounts();
    }

    [RelayCommand]
    private void SelectCompletedTab()
    {
        SelectedTab = MediaGrabberFilterTab.Completed;
        ApplyFilterAndCounts();
    }

    [RelayCommand]
    private void SelectFailedTab()
    {
        SelectedTab = MediaGrabberFilterTab.Failed;
        ApplyFilterAndCounts();
    }

    [RelayCommand]
    private async Task ClearCompletedAsync()
    {
        await _queue.ClearCompletedAsync();
        ShowToast("Cleared completed downloads.");
    }

    [RelayCommand]
    private void OpenDownloadFolder() => OpenFolderInExplorer(DownloadFolderPath, selectFile: false);

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var profileId = _queue.ActiveProfileId > 0 ? _queue.ActiveProfileId : _profileId;
        var s = await _database.GetMediaGrabberSettingsAsync(profileId);
        SettingsDownloadFolder = s.DefaultDownloadFolder;
        SettingsYtDlpPath = s.YtDlpPath;
        SettingsAria2cPath = s.Aria2cPath;
        SettingsFfmpegPath = s.FfmpegPath;
        SettingsMaxConcurrent = s.MaxConcurrentDownloads;
        SettingsAria2Connections = s.Aria2Connections;
        YtDlpUpdateOutput = string.Empty;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private async Task BrowseDownloadFolderAsync()
    {
        if (PickFolderRequested is null) return;
        var folder = await PickFolderRequested();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            SettingsDownloadFolder = folder;
        }
    }

    [RelayCommand]
    private async Task BrowseAria2cAsync()
    {
        if (PickExecutableRequested is null) return;
        var exe = await PickExecutableRequested("Select aria2c.exe");
        if (!string.IsNullOrWhiteSpace(exe))
        {
            SettingsAria2cPath = exe;
        }
    }

    [RelayCommand]
    private async Task BrowseFfmpegAsync()
    {
        if (PickExecutableRequested is null) return;
        var exe = await PickExecutableRequested("Select ffmpeg.exe");
        if (!string.IsNullOrWhiteSpace(exe))
        {
            SettingsFfmpegPath = exe;
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var profileId = _queue.ActiveProfileId > 0 ? _queue.ActiveProfileId : _profileId;
        var next = new MediaGrabberSettings(
            DefaultDownloadFolder: SettingsDownloadFolder.Trim(),
            YtDlpPath: SettingsYtDlpPath.Trim(),
            Aria2cPath: SettingsAria2cPath.Trim(),
            FfmpegPath: SettingsFfmpegPath.Trim(),
            MaxConcurrentDownloads: Math.Clamp(SettingsMaxConcurrent, 1, 8),
            Aria2Connections: Math.Clamp(SettingsAria2Connections, 1, 16));

        await _database.SaveMediaGrabberSettingsAsync(profileId, next);
        var loaded = await _database.GetMediaGrabberSettingsAsync(profileId);
        DownloadFolderPath = loaded.DefaultDownloadFolder;
        IsSettingsOpen = false;
        ShowToast("Media Grabber settings saved.");
    }

    [RelayCommand]
    private async Task UpdateYtDlpAsync()
    {
        if (IsUpdatingYtDlp || _disposed) return;

        IsUpdatingYtDlp = true;
        YtDlpUpdateOutput = "Updating yt-dlp.exe...";
        try
        {
            var profileId = _queue.ActiveProfileId > 0 ? _queue.ActiveProfileId : _profileId;
            var settings = await _database.GetMediaGrabberSettingsAsync(profileId);
            var (_, output) = await _ytDlpDownload.UpdateYtDlpAsync(settings, CancellationToken.None);
            YtDlpUpdateOutput = output;
        }
        catch (Exception ex)
        {
            YtDlpUpdateOutput = $"Update failed: {ex.Message}";
        }
        finally
        {
            if (!_disposed)
            {
                IsUpdatingYtDlp = false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _module.StateChanged -= OnModuleStateChanged;
        _module.StatusSummaryChanged -= OnModuleStatusSummaryChanged;
        _queue.JobsChanged -= OnQueueJobsChanged;
        _queue.JobUpdated -= OnQueueJobUpdated;
        CancelToastTimer();
        FilteredJobs.Clear();
        _allCards.Clear();
    }

    private void OnQueueJobsChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed) RebuildAllCardsFromQueue();
        });

    private void OnQueueJobUpdated(object? sender, DownloadJobState state) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            var card = _allCards.FirstOrDefault(c => c.HistoryId == state.HistoryId);
            if (card is null)
            {
                RebuildAllCardsFromQueue();
                return;
            }

            var prevBucket = GetBucket(card.Status);
            card.SyncFromState(state);
            if (prevBucket != GetBucket(card.Status))
            {
                ApplyFilterAndCounts();
            }
            else
            {
                UpdateStatusStripStats();
            }
        });

    private void RebuildAllCardsFromQueue()
    {
        _allCards.Clear();
        foreach (var state in _queue.GetAllJobsSnapshot())
        {
            _allCards.Add(new MediaGrabberJobItemViewModel(
                state,
                _queue.PauseJobAsync,
                _queue.ResumeJobAsync,
                _queue.CancelJobAsync,
                _queue.RetryJobAsync,
                path => OpenFolderInExplorer(path, selectFile: true),
                path => _module.RequestPlayInVlc(path)));
        }

        ApplyFilterAndCounts();
    }

    private void ApplyFilterAndCounts()
    {
        ActiveCount = _allCards.Count(c => c.IsActive || c.IsPaused);
        CompletedCount = _allCards.Count(c => c.IsCompleted);
        FailedCount = _allCards.Count(c => c.IsFailed);

        FilteredJobs.Clear();
        foreach (var card in _allCards)
        {
            var match = SelectedTab switch
            {
                MediaGrabberFilterTab.Completed => card.IsCompleted,
                MediaGrabberFilterTab.Failed => card.IsFailed,
                _ => card.IsActive || card.IsPaused
            };

            if (match)
            {
                FilteredJobs.Add(card);
            }
        }

        IsFilteredListEmpty = FilteredJobs.Count == 0;
        UpdateStatusStripStats();
    }

    private void UpdateStatusStripStats()
    {
        var speed = _queue.TotalSpeedBytesPerSec;
        var active = _queue.ActiveCount;
        TotalSpeedDisplay = $"↓ {BandwidthMonitor.FormatRate(speed)}";
        ActiveCountSummary = $"{active} active download{(active == 1 ? "" : "s")}";
    }

    private static MediaGrabberFilterTab GetBucket(DownloadJobStatus status) => status switch
    {
        DownloadJobStatus.Completed => MediaGrabberFilterTab.Completed,
        DownloadJobStatus.Failed or DownloadJobStatus.Cancelled => MediaGrabberFilterTab.Failed,
        _ => MediaGrabberFilterTab.Active
    };

    private static void OpenFolderInExplorer(string? path, bool selectFile)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (selectFile && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                var dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                }
            }
        }
        catch
        {
        }
    }

    private void ShowToast(string message)
    {
        CancelToastTimer();
        StatusNotice = message;

        var cts = new CancellationTokenSource();
        _toastCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3500, cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested && !_disposed)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!cts.IsCancellationRequested && string.Equals(StatusNotice, message, StringComparison.Ordinal))
                        {
                            StatusNotice = string.Empty;
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void CancelToastTimer()
    {
        var old = Interlocked.Exchange(ref _toastCts, null);
        if (old is not null)
        {
            try { old.Cancel(); } catch { }
            finally { old.Dispose(); }
        }
    }

    private void OnModuleStateChanged(object? sender, ModuleState state) => RefreshModuleHeaderStatus();
    private void OnModuleStatusSummaryChanged(object? sender, EventArgs e) => RefreshModuleHeaderStatus();

    private void RefreshModuleHeaderStatus()
    {
        StateLabel = _module.State switch
        {
            ModuleState.RunningUI => "Running (UI)",
            ModuleState.RunningHeadless => "Running (Headless)",
            ModuleState.Paused => "Paused",
            _ => "Stopped"
        };
        StatusSummary = _module.StatusSummary;
    }
}
