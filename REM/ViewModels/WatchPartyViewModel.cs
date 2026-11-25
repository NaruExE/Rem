using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using REM.Models;
using REM.Modules;
using REM.Services;

namespace REM.ViewModels;

public sealed partial class WatchPartyViewModel : ObservableObject, IDisposable
{
    private readonly WatchPartyModule _module;
    private readonly DatabaseService _database;
    private readonly WatchPartyService _service;
    private readonly VideoPlayerService _videoPlayer;
    private readonly ActionLogService _actionLog;
    private bool _disposed;

    public WatchPartyViewModel(
        WatchPartyModule module,
        DatabaseService database,
        WatchPartyService service,
        VideoPlayerService videoPlayer,
        ActionLogService actionLog)
    {
        _module = module;
        _database = database;
        _service = service;
        _videoPlayer = videoPlayer;
        _actionLog = actionLog;

        AccentBrush = new SolidColorBrush(Color.Parse(module.AccentHex));

        _module.StateChanged += OnModuleStateChanged;
        _module.StatusSummaryChanged += OnModuleStatusChanged;
        _service.StateChanged += OnServiceStateChanged;
        _service.PeersChanged += OnPeersChanged;
        _service.ChatMessageReceived += OnChatMessageReceived;
        _service.StatusTextChanged += OnServiceStatusTextChanged;
        _videoPlayer.PlaybackStateChanged += OnVlcPlaybackChanged;

        Nickname = Environment.UserName;
        HostOnlyControls = _service.HostOnlyControls;
        SyncAllFromService();
        SyncVlcSnapshot(_videoPlayer.GetSnapshot());
    }

    public string Name => _module.Name;
    public Geometry IconGeometry => _module.IconGeometry;
    public IBrush AccentBrush { get; }

    public Action? RequestCloseToDashboard { get; set; }
    public Func<Task<string?>>? PickVideoFileRequested { get; set; }
    public Func<Task<string?>>? ReadClipboardTextRequested { get; set; }
    public Func<string, Task>? WriteClipboardTextRequested { get; set; }

    public ObservableCollection<string> Peers { get; } = [];
    public ObservableCollection<WatchPartyChatMessage> ChatMessages { get; } = [];

    [ObservableProperty]
    private bool _isHeaderVisible = true;

    [ObservableProperty]
    private bool _hostOnlyControls;

    partial void OnHostOnlyControlsChanged(bool value) => _service.HostOnlyControls = value;

    [ObservableProperty]
    private string _stateLabel = "Running (UI)";

    [ObservableProperty]
    private string _statusSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJoinTab))]
    private bool _isHostTab = true;

    public bool IsJoinTab => !IsHostTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirectLanMode))]
    private bool _useCloudflareTunnel = true;

    public bool IsDirectLanMode => !UseCloudflareTunnel;

    [ObservableProperty]
    private string _nickname = "User";

    [ObservableProperty]
    private string _roomPin = string.Empty;

    [ObservableProperty]
    private string _joinUrlInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGeneratedRoomUrl))]
    private string _generatedRoomUrl = string.Empty;

    public bool HasGeneratedRoomUrl => !string.IsNullOrWhiteSpace(GeneratedRoomUrl);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartOrJoin))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartOrJoin))]
    private bool _isConnecting;

    public bool CanStartOrJoin => !IsConnected && !IsConnecting;

    [ObservableProperty]
    private string _connectionStatusText = "Not connected";

    [ObservableProperty]
    private string _mediaUrlInput = string.Empty;

    [ObservableProperty]
    private string _chatInput = string.Empty;

    [ObservableProperty]
    private string _vlcNowPlayingTitle = "No video loaded in VLC";

    [ObservableProperty]
    private string _vlcPlaybackStatusText = "Open a local movie or share a YouTube URL";

    [ObservableProperty]
    private bool _vlcIsPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _noticeText = string.Empty;

    public bool HasNotice => !string.IsNullOrWhiteSpace(NoticeText);

    [ObservableProperty]
    private bool _isPeersEmpty = true;

    [RelayCommand]
    private void SelectHostTab() => IsHostTab = true;

    [RelayCommand]
    private void SelectJoinTab() => IsHostTab = false;

    [RelayCommand]
    private void SelectCloudflareMode() => UseCloudflareTunnel = true;

    [RelayCommand]
    private void SelectDirectLanMode() => UseCloudflareTunnel = false;

    [RelayCommand]
    private async Task StartHostingAsync()
    {
        if (IsConnecting || IsConnected)
        {
            return;
        }

        IsConnecting = true;
        NoticeText = string.Empty;
        try
        {
            var mode = UseCloudflareTunnel
                ? WatchPartyConnectionMode.CloudflareTunnel
                : WatchPartyConnectionMode.DirectLan;

            await _service.StartHostAsync(Nickname, RoomPin, mode);
            SyncAllFromService();
        }
        catch (Exception ex)
        {
            NoticeText = $"Could not start room: {ex.Message}";
            _actionLog.Log("Warning", $"WatchParty start failed: {ex.Message}");
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private async Task JoinRoomAsync()
    {
        if (IsConnecting || IsConnected)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(JoinUrlInput))
        {
            NoticeText = "Paste the host's room URL first (wss://...trycloudflare.com/ws or ws://...).";
            return;
        }

        IsConnecting = true;
        NoticeText = string.Empty;
        try
        {
            await _service.JoinRoomAsync(JoinUrlInput, Nickname, RoomPin);
            SyncAllFromService();
        }
        catch (Exception ex)
        {
            NoticeText = $"Could not join room: {ex.Message}";
            _actionLog.Log("Warning", $"WatchParty join failed: {ex.Message}");
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private async Task LeaveOrStopRoomAsync()
    {
        NoticeText = string.Empty;
        await _service.LeaveOrStopAsync();
        SyncAllFromService();
    }

    [RelayCommand]
    private async Task CopyRoomLinkAsync()
    {
        if (string.IsNullOrWhiteSpace(GeneratedRoomUrl) || WriteClipboardTextRequested is null)
        {
            return;
        }

        await WriteClipboardTextRequested(GeneratedRoomUrl);
        NoticeText = "Room link copied to clipboard.";
    }

    [RelayCommand]
    private async Task PasteRoomLinkAsync()
    {
        if (ReadClipboardTextRequested is null)
        {
            return;
        }

        var text = await ReadClipboardTextRequested();
        if (!string.IsNullOrWhiteSpace(text))
        {
            JoinUrlInput = text.Trim();
        }
    }

    [RelayCommand]
    private async Task PickLocalVideoAsync()
    {
        if (PickVideoFileRequested is null)
        {
            return;
        }

        var path = await PickVideoFileRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await _service.PlayLocalFileInVlcAsync(path);
        }
        catch (Exception ex)
        {
            NoticeText = $"Could not open file in VLC: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ShareMediaUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(MediaUrlInput))
        {
            return;
        }

        var url = MediaUrlInput.Trim();
        NoticeText = string.Empty;
        try
        {
            await _service.ShareUrlAsync(url);
            MediaUrlInput = string.Empty;
        }
        catch (Exception ex)
        {
            NoticeText = $"Could not resolve or play URL: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenVlcWindow() => _module.RequestOpenVlcUi();

    [RelayCommand]
    private Task ToggleVlcPlayPauseAsync() => _videoPlayer.TogglePauseAsync();

    [RelayCommand]
    private Task ForceSyncNowAsync() => _service.ForceSyncNowAsync();

    [RelayCommand]
    private async Task SendChatAsync()
    {
        if (string.IsNullOrWhiteSpace(ChatInput))
        {
            return;
        }

        var text = ChatInput;
        ChatInput = string.Empty;
        await _service.SendChatAsync(text);
    }

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
        _module.StateChanged -= OnModuleStateChanged;
        _module.StatusSummaryChanged -= OnModuleStatusChanged;
        _service.StateChanged -= OnServiceStateChanged;
        _service.PeersChanged -= OnPeersChanged;
        _service.ChatMessageReceived -= OnChatMessageReceived;
        _service.StatusTextChanged -= OnServiceStatusTextChanged;
        _videoPlayer.PlaybackStateChanged -= OnVlcPlaybackChanged;
    }

    private void OnServiceStateChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            Dispatcher.UIThread.Post(SyncAllFromService);
        }
    }

    private void OnPeersChanged(object? sender, IReadOnlyList<string> peers)
    {
        if (!_disposed)
        {
            Dispatcher.UIThread.Post(() => ApplyPeers(peers));
        }
    }

    private void OnChatMessageReceived(object? sender, WatchPartyChatMessage msg)
    {
        if (!_disposed)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ChatMessages.Count >= 150)
                {
                    ChatMessages.RemoveAt(0);
                }

                ChatMessages.Add(msg);
            });
        }
    }

    private void OnServiceStatusTextChanged(object? sender, string status)
    {
        if (!_disposed)
        {
            Dispatcher.UIThread.Post(() => ConnectionStatusText = status);
        }
    }

    private void OnVlcPlaybackChanged(object? sender, VideoPlaybackSnapshot snapshot)
    {
        if (!_disposed)
        {
            Dispatcher.UIThread.Post(() => SyncVlcSnapshot(snapshot));
        }
    }

    private void SyncAllFromService()
    {
        IsConnected = _service.IsConnected;
        GeneratedRoomUrl = _service.RoomUrl;
        ConnectionStatusText = _service.StatusText;
        ApplyPeers(_service.GetPeerNames());

        ChatMessages.Clear();
        foreach (var msg in _service.GetChatHistory())
        {
            ChatMessages.Add(msg);
        }

        SyncModuleHeader();
    }

    private void ApplyPeers(IReadOnlyList<string> peers)
    {
        Peers.Clear();
        foreach (var peer in peers)
        {
            Peers.Add(peer);
        }

        IsPeersEmpty = Peers.Count == 0;
    }

    private void SyncVlcSnapshot(VideoPlaybackSnapshot snapshot)
    {
        VlcIsPlaying = snapshot.IsPlaying;
        if (string.IsNullOrWhiteSpace(snapshot.CurrentTitle) && string.IsNullOrWhiteSpace(snapshot.CurrentSourceUrl))
        {
            VlcNowPlayingTitle = "No video loaded in VLC";
            VlcPlaybackStatusText = "Open a local movie or share a YouTube URL";
            return;
        }

        VlcNowPlayingTitle = snapshot.CurrentTitle ?? Path.GetFileName(snapshot.CurrentSourceUrl) ?? "Playing in VLC";
        var state = snapshot.IsPlaying ? "Playing" : (snapshot.IsPaused ? "Paused" : "Ready");
        VlcPlaybackStatusText = $"{state} · {FormatTime(snapshot.PositionMs)} / {FormatTime(snapshot.DurationMs)} · {snapshot.PlaybackSpeed:0.##}x";
    }

    private static string FormatTime(long ms)
    {
        if (ms <= 0)
        {
            return "0:00";
        }

        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
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
