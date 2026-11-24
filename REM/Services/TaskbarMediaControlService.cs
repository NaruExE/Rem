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
    private const int ThbnClicked = 0x1800;

    private const uint BtnPrevId = 1001;
    private const uint BtnPlayPauseId = 1002;
    private const uint BtnNextId = 1003;

    private const uint ThbIcon = 0x00000002;
    private const uint ThbTooltip = 0x00000004;
    private const uint ThbFlags = 0x00000008;

    private const uint ThbfEnabled = 0x00000000;
    private const uint ThbfDisabled = 0x00000001;

    private enum ActiveTarget
    {
        None,
        Spotify,
        AudioStreamer
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
    private readonly ActionLogService _actionLog;
    private readonly uint _taskbarCreatedMsg;

    private ITaskbarList3? _taskbarList;
    private IntPtr _hwnd;
    private bool _buttonsAdded;
    private bool _hookAttached;
    private ActiveTarget _lastActiveTarget = ActiveTarget.None;

    private IntPtr _iconPrev;
    private IntPtr _iconPlay;
    private IntPtr _iconPause;
    private IntPtr _iconNext;
    private bool _disposed;

    public TaskbarMediaControlService(
        Window window,
        SpotifySoloistService soloist,
        AudioPlaybackEngine audioEngine,
        ActionLogService actionLog)
    {
        _window = window;
        _soloist = soloist;
        _audioEngine = audioEngine;
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

        if (!_buttonsAdded)
        {
            EnsureButtonsAttached();
            return;
        }

        try
        {
            var buttons = BuildCurrentButtons();
            _taskbarList.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, buttons);
            _taskbarList.SetThumbnailTooltip(_hwnd, BuildTooltip());
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

        if (soloistPlaying && !audioPlaying) return ActiveTarget.Spotify;
        if (audioPlaying && !soloistPlaying) return ActiveTarget.AudioStreamer;

        if (_lastActiveTarget == ActiveTarget.Spotify && soloistHasTrack) return ActiveTarget.Spotify;
        if (_lastActiveTarget == ActiveTarget.AudioStreamer && audioHasTrack) return ActiveTarget.AudioStreamer;

        if (soloistHasTrack) return ActiveTarget.Spotify;
        if (audioHasTrack) return ActiveTarget.AudioStreamer;
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
        }
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
        _window.Opened -= OnWindowOpened;
        _soloist.StatusChanged -= OnSoloistStatusChanged;
        _audioEngine.PlaybackStateChanged -= OnAudioStateChanged;

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

