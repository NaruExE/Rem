using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace REM.Services;

/// <summary>
/// Attaches native Windows taskbar hover thumbnail media controls (Prev / Play-Pause / Next)
/// controlling whichever REM audio engine (Spotify Soloist or Audio Streamer) is active.
/// </summary>
public sealed class TaskbarMediaControlService : IDisposable
{
    private const int WmCommand = 0x0111;
    private const int WmHotKey = 0x0312;
    private const int WmAppCommand = 0x0319;
    private const int ThbnClicked = 0x1800;
    private const uint ModNoRepeat = 0x4000;

    private const uint VkMediaNextTrack = 0xB0;
    private const uint VkMediaPrevTrack = 0xB1;
    private const uint VkMediaStop = 0xB2;
    private const uint VkMediaPlayPause = 0xB3;

    private const int AppCmdMediaNext = 11;
    private const int AppCmdMediaPrev = 12;
    private const int AppCmdMediaStop = 13;
    private const int AppCmdMediaPlayPause = 14;
    private const int AppCmdMediaPlay = 46;
    private const int AppCmdMediaPause = 47;

    private const uint BtnPrevId = 1001;
    private const uint BtnPlayPauseId = 1002;
    private const uint BtnNextId = 1003;

    private const uint HotKeyPlayPauseId = 0x5201;
    private const uint HotKeyNextId = 0x5202;
    private const uint HotKeyPrevId = 0x5203;
    private const uint HotKeyStopId = 0x5204;

    private const uint ThbIcon = 0x00000002;
    private const uint ThbTooltip = 0x00000004;
    private const uint ThbFlags = 0x00000008;

    private const uint ThbfEnabled = 0x00000000;
    private const uint ThbfDisabled = 0x00000001;

    private enum ActiveTarget
    {
        None,
        Spotify,
        AudioStreamer,
        VideoPlayer
    }

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        [PreserveSig] int SetProgressState(IntPtr hwnd, int tbpFlags);
        [PreserveSig] int RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        [PreserveSig] int UnregisterTab(IntPtr hwndTab);
        [PreserveSig] int SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        [PreserveSig] int SetTabActive(IntPtr hwndTab, IntPtr hwndInsertBefore, uint dwReserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] pButtons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] pButtons);
        [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        [PreserveSig] int SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string? pszDescription);
        [PreserveSig] int SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? pszTip);
        [PreserveSig] int SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitsPerPel, int[] lpBits);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitsPerPel, byte[] lpBits);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly Window _window;
    private readonly SpotifySoloistService _soloist;
    private readonly AudioPlaybackEngine _audioEngine;
    private readonly VideoPlayerService? _videoEngine;
    private readonly ActionLogService _actionLog;
    private readonly uint _taskbarCreatedMsg;

    private ITaskbarList3? _taskbarList;
    private IntPtr _hwnd;
    private bool _buttonsAdded;
    private bool _hookAttached;
    private bool _hotKeysRegistered;
    private ActiveTarget _lastActiveTarget = ActiveTarget.None;

    private IntPtr _iconPrev;
    private IntPtr _iconPlay;
    private IntPtr _iconPause;
    private IntPtr _iconNext;
    private string _lastRenderedStateKey = string.Empty;
    private string _lastSoloistPreKey = string.Empty;
    private string _lastAudioPreKey = string.Empty;
    private string _lastVideoPreKey = string.Empty;
    private bool _disposed;

    public TaskbarMediaControlService(
        Window window,
        SpotifySoloistService soloist,
        AudioPlaybackEngine audioEngine,
        ActionLogService actionLog,
        VideoPlayerService? videoEngine = null)
    {
        _window = window;
        _soloist = soloist;
        _audioEngine = audioEngine;
        _videoEngine = videoEngine;
        _actionLog = actionLog;

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarButtonCreated");
        CreateIcons();

        _window.Opened += OnWindowOpened;
        _soloist.StatusChanged += OnSoloistStatusChanged;
        _audioEngine.PlaybackStateChanged += OnAudioStateChanged;
        if (_videoEngine is not null)
        {
            _videoEngine.PlaybackStateChanged += OnVideoStateChanged;
        }

        if (_window.TryGetPlatformHandle() is { Handle: var h } && h != IntPtr.Zero)
        {
            InitializeForWindow(h);
        }
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_window.TryGetPlatformHandle() is { Handle: var h } && h != IntPtr.Zero)
        {
            InitializeForWindow(h);
            Dispatcher.UIThread.Post(EnsureButtonsAttached);
        }
    }

    private void InitializeForWindow(IntPtr hwnd)
    {
        _hwnd = hwnd;
        if (!_hookAttached)
        {
            _hookAttached = true;
            Win32Properties.AddWndProcHookCallback(_window, WndProcHook);
        }

        if (_taskbarList is null && OperatingSystem.IsWindows())
        {
            try
            {
                var clsid = new Guid("56FDF344-FD6D-11D0-958A-006097C9A090");
                var comType = Type.GetTypeFromCLSID(clsid);
                if (comType is not null && Activator.CreateInstance(comType) is ITaskbarList3 tbl)
                {
                    _taskbarList = tbl;
                    _taskbarList.HrInit();
                }
            }
            catch (Exception ex)
            {
                _actionLog.Log("Warning", $"Taskbar media controls unavailable: {ex.Message}");
                _taskbarList = null;
            }
        }
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed)
        {
            return IntPtr.Zero;
        }

        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            _hwnd = hWnd != IntPtr.Zero ? hWnd : _hwnd;
            _buttonsAdded = false;
            EnsureButtonsAttached();
            return IntPtr.Zero;
        }

        if (msg == WmHotKey)
        {
            var id = unchecked((uint)wParam.ToInt64());
            uint? mappedBtn = id switch
            {
                HotKeyPrevId => BtnPrevId,
                HotKeyPlayPauseId => BtnPlayPauseId,
                HotKeyNextId => BtnNextId,
                _ => null
            };
            if (mappedBtn.HasValue)
            {
                handled = true;
                HandleThumbButtonClick(mappedBtn.Value);
            }
            else if (id == HotKeyStopId)
            {
                handled = true;
                HandleStopMediaCommand();
            }

            return IntPtr.Zero;
        }

        if (msg == WmAppCommand)
        {
            var appCmd = (int)((lParam.ToInt64() >> 16) & 0x0FFF);
            switch (appCmd)
            {
                case AppCmdMediaPlayPause:
                case AppCmdMediaPlay:
                case AppCmdMediaPause:
                    if (ResolveTarget() != ActiveTarget.None)
                    {
                        handled = true;
                        HandleThumbButtonClick(BtnPlayPauseId);
                    }
                    break;
                case AppCmdMediaNext:
                    if (ResolveTarget() != ActiveTarget.None)
                    {
                        handled = true;
                        HandleThumbButtonClick(BtnNextId);
                    }
                    break;
                case AppCmdMediaPrev:
                    if (ResolveTarget() != ActiveTarget.None)
                    {
                        handled = true;
                        HandleThumbButtonClick(BtnPrevId);
                    }
                    break;
                case AppCmdMediaStop:
                    if (ResolveTarget() != ActiveTarget.None)
                    {
                        handled = true;
                        HandleStopMediaCommand();
                    }
                    break;
            }

            return IntPtr.Zero;
        }

        if (msg == WmCommand)
        {
            var rawWParam = wParam.ToInt64();
            var cmdId = (uint)(rawWParam & 0xFFFF);
            var hiWord = (int)((rawWParam >> 16) & 0xFFFF);
            if (hiWord == ThbnClicked && cmdId is BtnPrevId or BtnPlayPauseId or BtnNextId)
            {
                handled = true;
                HandleThumbButtonClick(cmdId);
            }
        }

        return IntPtr.Zero;
    }

    private void OnSoloistStatusChanged(object? sender, Models.GoLibrespotStatus? status)
    {
        if (_disposed)
        {
            return;
        }

        if (status is { Stopped: false, Paused: false, Track: not null })
        {
            _lastActiveTarget = ActiveTarget.Spotify;
        }
        else if (_lastActiveTarget == ActiveTarget.None && status is { Track: not null })
        {
            _lastActiveTarget = ActiveTarget.Spotify;
        }

        var key = $"{_soloist.IsDaemonRunning}|{status?.Stopped}|{status?.Paused}|{status?.Track?.Uri}";
        if (string.Equals(key, _lastSoloistPreKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastSoloistPreKey = key;
        Dispatcher.UIThread.Post(UpdateButtonsAndTooltip);
    }

    private void OnAudioStateChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_audioEngine.IsPlaying)
        {
            _lastActiveTarget = ActiveTarget.AudioStreamer;
        }
        else if (_lastActiveTarget == ActiveTarget.None && _audioEngine.CurrentTrack is not null)
        {
            _lastActiveTarget = ActiveTarget.AudioStreamer;
        }

        var key = $"{_audioEngine.IsPlaying}|{_audioEngine.CurrentTrack?.Title}";
        if (string.Equals(key, _lastAudioPreKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastAudioPreKey = key;
        Dispatcher.UIThread.Post(UpdateButtonsAndTooltip);
    }

    private void OnVideoStateChanged(object? sender, Models.VideoPlaybackSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        if (snapshot.IsPlaying)
        {
            _lastActiveTarget = ActiveTarget.VideoPlayer;
        }
        else if (_lastActiveTarget == ActiveTarget.None && !string.IsNullOrWhiteSpace(snapshot.CurrentTitle))
        {
            _lastActiveTarget = ActiveTarget.VideoPlayer;
        }

        var key = $"{snapshot.IsPlaying}|{snapshot.IsPaused}|{snapshot.CurrentTitle}";
        if (string.Equals(key, _lastVideoPreKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastVideoPreKey = key;
        Dispatcher.UIThread.Post(UpdateButtonsAndTooltip);
    }

    private void EnsureButtonsAttached()
    {
        if (_disposed || _taskbarList is null || _hwnd == IntPtr.Zero || _buttonsAdded)
        {
            return;
        }

        try
        {
            var buttons = BuildCurrentButtons();
            var hr = _taskbarList.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons);
            if (hr >= 0)
            {
                _buttonsAdded = true;
                _lastRenderedStateKey = string.Empty;
                UpdateButtonsAndTooltip();
            }
        }
        catch
        {
            // Ignore if shell taskbar handle isn't ready yet.
        }
    }

    private void UpdateButtonsAndTooltip()
    {
        if (_disposed || _taskbarList is null || _hwnd == IntPtr.Zero)
        {
            return;
        }

        SyncGlobalMediaHotKeys();

        if (!_buttonsAdded)
        {
            EnsureButtonsAttached();
            return;
        }

        try
        {
            var buttons = BuildCurrentButtons();
            var tooltip = BuildTooltip();
            var stateKey = $"{buttons[0].dwFlags}|{buttons[1].dwFlags}|{buttons[1].hIcon}|{buttons[2].dwFlags}|{tooltip}";
            if (string.Equals(stateKey, _lastRenderedStateKey, StringComparison.Ordinal))
            {
                return;
            }

            _lastRenderedStateKey = stateKey;
            _taskbarList.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, buttons);
            _taskbarList.SetThumbnailTooltip(_hwnd, tooltip);
        }
        catch
        {
            // Ignore transient shell update failures.
        }
    }

    private ActiveTarget ResolveTarget()
    {
        var soloistStatus = _soloist.CurrentStatus;
        var soloistHasTrack = _soloist.IsDaemonRunning && soloistStatus is { Stopped: false, Track: not null };
        var soloistPlaying = soloistHasTrack && soloistStatus is { Paused: false };

        var audioHasTrack = _audioEngine.CurrentTrack is not null;
        var audioPlaying = audioHasTrack && _audioEngine.IsPlaying;

        var videoHasTrack = _videoEngine is { IsInitialized: true } &&
                            (_videoEngine.IsPlaying || _videoEngine.IsPaused || !string.IsNullOrWhiteSpace(_videoEngine.CurrentTitle));
        var videoPlaying = videoHasTrack && _videoEngine!.IsPlaying;

        if (soloistPlaying && !audioPlaying && !videoPlaying) return ActiveTarget.Spotify;
        if (audioPlaying && !soloistPlaying && !videoPlaying) return ActiveTarget.AudioStreamer;
        if (videoPlaying && !soloistPlaying && !audioPlaying) return ActiveTarget.VideoPlayer;

        if (_lastActiveTarget == ActiveTarget.Spotify && soloistHasTrack) return ActiveTarget.Spotify;
        if (_lastActiveTarget == ActiveTarget.AudioStreamer && audioHasTrack) return ActiveTarget.AudioStreamer;
        if (_lastActiveTarget == ActiveTarget.VideoPlayer && videoHasTrack) return ActiveTarget.VideoPlayer;

        if (soloistHasTrack) return ActiveTarget.Spotify;
        if (audioHasTrack) return ActiveTarget.AudioStreamer;
        if (videoHasTrack) return ActiveTarget.VideoPlayer;
        return ActiveTarget.None;
    }

    private THUMBBUTTON[] BuildCurrentButtons()
    {
        var target = ResolveTarget();
        bool canPrev;
        bool canPlayPause;
        bool canNext;
        bool isPlaying;
        string prevTip = "Previous";
        string playTip;

        switch (target)
        {
            case ActiveTarget.Spotify:
                var st = _soloist.CurrentStatus;
                isPlaying = st is { Stopped: false, Paused: false, Track: not null };
                canPrev = true;
                canPlayPause = true;
                canNext = true;
                playTip = isPlaying ? "Pause Spotify" : "Play Spotify";
                break;

            case ActiveTarget.AudioStreamer:
                isPlaying = _audioEngine.IsPlaying;
                canPrev = !_audioEngine.IsLive && _audioEngine.DurationSeconds > 0;
                canPlayPause = true;
                canNext = false;
                prevTip = "Restart track";
                playTip = isPlaying ? "Pause Audio" : "Resume Audio";
                break;

            case ActiveTarget.VideoPlayer:
                isPlaying = _videoEngine?.IsPlaying == true;
                canPrev = _videoEngine is { DurationMs: > 0 };
                canPlayPause = _videoEngine is not null;
                canNext = false;
                prevTip = "Restart video";
                playTip = isPlaying ? "Pause Video" : "Resume Video";
                break;

            default:
                isPlaying = false;
                canPrev = false;
                canPlayPause = false;
                canNext = false;
                playTip = "Play / Pause (no active track)";
                break;
        }

        return
        [
            new THUMBBUTTON
            {
                dwMask = ThbIcon | ThbTooltip | ThbFlags,
                iId = BtnPrevId,
                hIcon = _iconPrev,
                szTip = prevTip,
                dwFlags = canPrev ? ThbfEnabled : ThbfDisabled
            },
            new THUMBBUTTON
            {
                dwMask = ThbIcon | ThbTooltip | ThbFlags,
                iId = BtnPlayPauseId,
                hIcon = isPlaying ? _iconPause : _iconPlay,
                szTip = playTip,
                dwFlags = canPlayPause ? ThbfEnabled : ThbfDisabled
            },
            new THUMBBUTTON
            {
                dwMask = ThbIcon | ThbTooltip | ThbFlags,
                iId = BtnNextId,
                hIcon = _iconNext,
                szTip = "Next",
                dwFlags = canNext ? ThbfEnabled : ThbfDisabled
            }
        ];
    }

    private string BuildTooltip()
    {
        var target = ResolveTarget();
        if (target == ActiveTarget.Spotify && _soloist.CurrentStatus is { Track: { } spTrack } spStatus)
        {
            var verb = spStatus.Paused ? "Paused" : "Playing";
            return $"REM · {verb}: {spTrack.Name} — {spTrack.ArtistsDisplay}";
        }

        if (target == ActiveTarget.AudioStreamer && _audioEngine.CurrentTrack is { } audioTrack)
        {
            var verb = _audioEngine.IsPlaying ? "Playing" : "Paused";
            return $"REM · {verb}: {audioTrack.Title}";
        }

        if (target == ActiveTarget.VideoPlayer && _videoEngine is { CurrentTitle: { } vidTitle })
        {
            var verb = _videoEngine.IsPlaying ? "Playing" : "Paused";
            return $"REM · {verb}: {vidTitle}";
        }

        return "REM · RAM Efficient Manager";
    }

    private void HandleThumbButtonClick(uint btnId)
    {
        var target = ResolveTarget();
        switch (target)
        {
            case ActiveTarget.Spotify:
                switch (btnId)
                {
                    case BtnPrevId:
                        _ = _soloist.PrevAsync();
                        break;
                    case BtnPlayPauseId:
                        _ = _soloist.PlayPauseAsync();
                        break;
                    case BtnNextId:
                        _ = _soloist.NextAsync();
                        break;
                }
                break;

            case ActiveTarget.AudioStreamer:
                switch (btnId)
                {
                    case BtnPrevId:
                        if (!_audioEngine.IsLive && _audioEngine.DurationSeconds > 0)
                        {
                            _audioEngine.SeekSeconds(0);
                        }
                        break;
                    case BtnPlayPauseId:
                        _ = _audioEngine.TogglePauseAsync();
                        break;
                }
                break;

            case ActiveTarget.VideoPlayer when _videoEngine is not null:
                switch (btnId)
                {
                    case BtnPrevId:
                        if (_videoEngine.DurationMs > 0)
                        {
                            _ = _videoEngine.SeekAsync(0);
                        }
                        break;
                    case BtnPlayPauseId:
                        _ = _videoEngine.TogglePauseAsync();
                        break;
                }
                break;
        }
    }

    private void HandleStopMediaCommand()
    {
        switch (ResolveTarget())
        {
            case ActiveTarget.Spotify:
                _ = _soloist.PauseAsync();
                break;
            case ActiveTarget.AudioStreamer:
                _ = _audioEngine.PauseIfPlayingAsync();
                break;
            case ActiveTarget.VideoPlayer when _videoEngine is not null:
                _ = _videoEngine.PauseAsync();
                break;
        }
    }

    private void SyncGlobalMediaHotKeys()
    {
        if (_disposed || !OperatingSystem.IsWindows() || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var shouldRegister = ResolveTarget() != ActiveTarget.None;
        if (shouldRegister == _hotKeysRegistered)
        {
            return;
        }

        if (shouldRegister)
        {
            RegisterHotKey(_hwnd, (int)HotKeyPlayPauseId, ModNoRepeat, VkMediaPlayPause);
            RegisterHotKey(_hwnd, (int)HotKeyNextId, ModNoRepeat, VkMediaNextTrack);
            RegisterHotKey(_hwnd, (int)HotKeyPrevId, ModNoRepeat, VkMediaPrevTrack);
            RegisterHotKey(_hwnd, (int)HotKeyStopId, ModNoRepeat, VkMediaStop);
            _hotKeysRegistered = true;
        }
        else
        {
            UnregisterHotKeys();
        }
    }

    private void UnregisterHotKeys()
    {
        if (!_hotKeysRegistered || _hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
        {
            return;
        }

        try { UnregisterHotKey(_hwnd, (int)HotKeyPlayPauseId); } catch { }
        try { UnregisterHotKey(_hwnd, (int)HotKeyNextId); } catch { }
        try { UnregisterHotKey(_hwnd, (int)HotKeyPrevId); } catch { }
        try { UnregisterHotKey(_hwnd, (int)HotKeyStopId); } catch { }
        _hotKeysRegistered = false;
    }

    private void CreateIcons()
    {
        _iconPrev = BuildVectorIcon(DrawPrevPixel);
        _iconPlay = BuildVectorIcon(DrawPlayPixel);
        _iconPause = BuildVectorIcon(DrawPausePixel);
        _iconNext = BuildVectorIcon(DrawNextPixel);
    }

    private static IntPtr BuildVectorIcon(Func<int, int, bool> isLit)
    {
        const int size = 24;
        var argb = new int[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // Keep transparent (0x00000000) except lit white glyph pixels (0xFFF2F4F8)
                argb[y * size + x] = isLit(x, y) ? unchecked((int)0xFFF2F4F8) : 0;
            }
        }

        var maskRowBytes = ((size + 15) / 16) * 2;
        var mask = new byte[maskRowBytes * size]; // all zeroes -> alpha channel governs transparency

        var hColor = CreateBitmap(size, size, 1, 32, argb);
        var hMask = CreateBitmap(size, size, 1, 1, mask);
        try
        {
            var info = new ICONINFO
            {
                fIcon = true,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = hMask,
                hbmColor = hColor
            };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            if (hColor != IntPtr.Zero) DeleteObject(hColor);
            if (hMask != IntPtr.Zero) DeleteObject(hMask);
        }
    }

    private static bool DrawPlayPixel(int x, int y)
    {
        if (y is < 5 or > 18) return false;
        var dy = Math.Abs(y - 11.5);
        var maxX = 18.0 - (dy * 1.65);
        return x >= 7 && x <= maxX;
    }

    private static bool DrawPausePixel(int x, int y)
    {
        if (y is < 5 or > 18) return false;
        return x is (>= 6 and <= 9) or (>= 14 and <= 17);
    }

    private static bool DrawPrevPixel(int x, int y)
    {
        if (y is < 5 or > 18) return false;
        if (x is >= 5 and <= 7) return true;
        var dy = Math.Abs(y - 11.5);
        var minX = 9.0 + (dy * 1.45);
        return x >= minX && x <= 18;
    }

    private static bool DrawNextPixel(int x, int y)
    {
        if (y is < 5 or > 18) return false;
        if (x is >= 16 and <= 18) return true;
        var dy = Math.Abs(y - 11.5);
        var maxX = 14.0 - (dy * 1.45);
        return x >= 5 && x <= maxX;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterHotKeys();
        _window.Opened -= OnWindowOpened;
        _soloist.StatusChanged -= OnSoloistStatusChanged;
        _audioEngine.PlaybackStateChanged -= OnAudioStateChanged;
        if (_videoEngine is not null)
        {
            _videoEngine.PlaybackStateChanged -= OnVideoStateChanged;
        }

        if (_hookAttached)
        {
            try
            {
                Win32Properties.RemoveWndProcHookCallback(_window, WndProcHook);
            }
            catch
            {
                // Ignore if window already closed.
            }
        }

        if (OperatingSystem.IsWindows() && _taskbarList is not null && Marshal.IsComObject(_taskbarList))
        {
            try
            {
                Marshal.ReleaseComObject(_taskbarList);
            }
            catch
            {
            }
            _taskbarList = null;
        }

        DestroyIconSafe(ref _iconPrev);
        DestroyIconSafe(ref _iconPlay);
        DestroyIconSafe(ref _iconPause);
        DestroyIconSafe(ref _iconNext);
    }

    private static void DestroyIconSafe(ref IntPtr icon)
    {
        if (icon != IntPtr.Zero)
        {
            DestroyIcon(icon);
            icon = IntPtr.Zero;
        }
    }
}

