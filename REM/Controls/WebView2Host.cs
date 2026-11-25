using System.Drawing;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Web.WebView2.Core;
using REM.Services;

namespace REM.Controls;

/// <summary>
/// Hardened Windows NativeControlHost wrapping Microsoft Edge WebView2 for embed playback.
/// Strictly lazy-loaded: CoreWebView2 is NOT created until NavigateAsync/Navigate is called.
/// Blocks pop-up windows, blocks unexpected top-level domain hijacks, filters ad domains,
/// and supports hard Shutdown() so msedgewebview2.exe exits cleanly.
/// </summary>
public sealed class WebView2Host : NativeControlHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;

    private static readonly string[] DefaultBlockedAdDomains =
    [
        "doubleclick.net",
        "googlesyndication.com",
        "googleadservices.com",
        "adservice.google.com",
        "popads.net",
        "popcash.net",
        "propellerads.com",
        "exoclick.com",
        "juicyads.com",
        "trafficjunky.com",
        "adsterra.com",
        "adnxs.com",
        "outbrain.com",
        "taboola.com",
        "mc.yandex.ru",
        "histats.com",
        "acscdn.com",
        "onclickalgo.com",
        "pushance.com",
        "whos.amung.us",
        "realsrv.com",
        "tsyndicate.com",
        "dtscout.com",
        "monetag.com"
    ];

    private readonly HashSet<string> _blockedDomains = new(DefaultBlockedAdDomains, StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _initGate = new(1, 1);

    private IntPtr _hostHwnd;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private uint _registeredBrowserPid;
    private string? _allowedEmbedHost;
    private string? _pendingAllowedNavigationUri;
    private string? _pendingUrlBeforeHandle;
    private bool _isShuttingDown;

    public WebView2Host()
    {
        LayoutUpdated += (_, _) => UpdateControllerBounds();
    }

    public bool IsWebViewInitialized => _controller is not null && _core is not null;
    public string? CurrentUrl => _core?.Source;
    public event EventHandler<string>? NavigationCompleted;
    public event EventHandler<string>? BlockedAttempt;

    public void SetAdditionalBlockedDomains(IEnumerable<string> domains)
    {
        _blockedDomains.Clear();
        foreach (var domain in DefaultBlockedAdDomains)
        {
            _blockedDomains.Add(domain);
        }

        foreach (var raw in domains)
        {
            var cleaned = raw.Trim().TrimStart('.').ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                _blockedDomains.Add(cleaned);
            }
        }
    }

    public Task Navigate(string url, CancellationToken cancellationToken = default) =>
        NavigateAsync(url, cancellationToken);

    public async Task NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("Target URL cannot be empty.", nameof(url));
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Invalid HTTP/HTTPS embed URL: '{url}'.", nameof(url));
        }

        _isShuttingDown = false;
        _allowedEmbedHost = uri.Host;

        if (_hostHwnd == IntPtr.Zero)
        {
            _pendingUrlBeforeHandle = uri.AbsoluteUri;
            return;
        }

        await EnsureInitializedAsync(cancellationToken);
        if (_core is null || _controller is null || _isShuttingDown)
        {
            return;
        }

        UpdateControllerBounds();
        _controller.IsVisible = IsVisible;
        _pendingAllowedNavigationUri = uri.AbsoluteUri;
        _core.Navigate(uri.AbsoluteUri);
    }

    public async Task ShutdownAsync()
    {
        _isShuttingDown = true;
        _pendingUrlBeforeHandle = null;
        _pendingAllowedNavigationUri = null;
        _allowedEmbedHost = null;

        await _initGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await DisposeWebViewOnUiAsync().ConfigureAwait(true);
        }
        finally
        {
            _initGate.Release();
        }
    }

    public void Shutdown()
    {
        _isShuttingDown = true;
        _pendingUrlBeforeHandle = null;
        _pendingAllowedNavigationUri = null;
        _allowedEmbedHost = null;

        if (Dispatcher.UIThread.CheckAccess())
        {
            DisposeWebViewSyncOnUi();
        }
        else
        {
            Dispatcher.UIThread.Invoke(DisposeWebViewSyncOnUi);
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return base.CreateNativeControlCore(parent);
        }

        // Only create a lightweight empty Win32 child HWND here.
        // CoreWebView2 remains strictly uninitialized until NavigateAsync() is called.
        _hostHwnd = CreateWindowExW(
            dwExStyle: 0,
            lpClassName: "STATIC",
            lpWindowName: string.Empty,
            dwStyle: WsChild | WsVisible | WsClipChildren,
            x: 0,
            y: 0,
            nWidth: 1,
            nHeight: 1,
            hWndParent: parent.Handle,
            hMenu: IntPtr.Zero,
            hInstance: IntPtr.Zero,
            lpParam: IntPtr.Zero);

        if (_hostHwnd == IntPtr.Zero)
        {
            return base.CreateNativeControlCore(parent);
        }

        if (!string.IsNullOrWhiteSpace(_pendingUrlBeforeHandle))
        {
            var deferredUrl = _pendingUrlBeforeHandle;
            _pendingUrlBeforeHandle = null;
            Dispatcher.UIThread.Post(() => _ = NavigateAsync(deferredUrl));
        }

        return new PlatformHandle(_hostHwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Shutdown();

        if (_hostHwnd != IntPtr.Zero)
        {
            DestroyWindow(_hostHwnd);
            _hostHwnd = IntPtr.Zero;
        }

        base.DestroyNativeControlCore(control);
    }

    protected override Avalonia.Size ArrangeOverride(Avalonia.Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        UpdateControllerBounds();
        return arranged;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || _isShuttingDown)
        {
            return;
        }

        if (_controller is not null && _core is not null)
        {
            return;
        }

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_isShuttingDown || _hostHwnd == IntPtr.Zero || (_controller is not null && _core is not null))
            {
                return;
            }

            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "REM",
                "WebView2",
                "MoviesTv");
            Directory.CreateDirectory(userDataDir);

            _environment ??= await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataDir).ConfigureAwait(true);

            if (_isShuttingDown || _hostHwnd == IntPtr.Zero)
            {
                return;
            }

            _controller = await _environment.CreateCoreWebView2ControllerAsync(_hostHwnd).ConfigureAwait(true);
            _core = _controller.CoreWebView2;

            if (_registeredBrowserPid != 0)
            {
                MemoryTrimService.UnregisterTrackedRootPid(_registeredBrowserPid);
            }

            _registeredBrowserPid = _core.BrowserProcessId;
            MemoryTrimService.RegisterTrackedRootPid(_registeredBrowserPid);

            ConfigureHardenedSettings(_core.Settings);
            _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);

            _core.NewWindowRequested += OnNewWindowRequested;
            _core.NavigationStarting += OnNavigationStarting;
            _core.NavigationCompleted += OnCoreNavigationCompleted;
            _core.WebResourceRequested += OnWebResourceRequested;
            _core.ProcessFailed += OnCoreProcessFailed;

            UpdateControllerBounds();
        }
        finally
        {
            _initGate.Release();
        }
    }

    private static void ConfigureHardenedSettings(CoreWebView2Settings settings)
    {
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsBuiltInErrorPageEnabled = true;
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        BlockedAttempt?.Invoke(this, $"Blocked popup window: {e.Uri}");
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_isShuttingDown)
        {
            e.Cancel = true;
            return;
        }

        if (string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_pendingAllowedNavigationUri) &&
            string.Equals(e.Uri, _pendingAllowedNavigationUri, StringComparison.OrdinalIgnoreCase))
        {
            _pendingAllowedNavigationUri = null;
            return;
        }

        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var targetUri) ||
            string.IsNullOrWhiteSpace(_allowedEmbedHost) ||
            !IsSameOrSubdomainHost(targetUri.Host, _allowedEmbedHost))
        {
            e.Cancel = true;
            BlockedAttempt?.Invoke(this, $"Blocked top-level hijack redirect: {e.Uri}");
        }
    }

    private void OnCoreNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var current = _core?.Source;
        if (!string.IsNullOrWhiteSpace(current))
        {
            NavigationCompleted?.Invoke(this, current);
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = _environment;
        if (env is null || string.IsNullOrWhiteSpace(e.Request?.Uri))
        {
            return;
        }

        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var reqUri))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_allowedEmbedHost) &&
            IsSameOrSubdomainHost(reqUri.Host, _allowedEmbedHost))
        {
            return;
        }

        if (IsBlockedAdHost(reqUri.Host))
        {
            e.Response = env.CreateWebResourceResponse(
                Stream.Null,
                403,
                "Blocked",
                "Content-Type: text/plain");
            BlockedAttempt?.Invoke(this, $"Blocked ad resource: {reqUri.Host}");
        }
    }

    private void OnCoreProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        BlockedAttempt?.Invoke(this, $"WebView2 process failure ({e.ProcessFailedKind}).");
    }

    private bool IsBlockedAdHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        foreach (var blocked in _blockedDomains)
        {
            if (IsSameOrSubdomainHost(host, blocked))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSameOrSubdomainHost(string candidateHost, string rootHost)
    {
        if (string.IsNullOrWhiteSpace(candidateHost) || string.IsNullOrWhiteSpace(rootHost))
        {
            return false;
        }

        var c = candidateHost.Trim().TrimEnd('.');
        var r = rootHost.Trim().TrimEnd('.');
        if (string.Equals(c, r, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return c.EndsWith("." + r, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateControllerBounds()
    {
        var controller = _controller;
        if (controller is null || _isShuttingDown)
        {
            return;
        }

        try
        {
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            var width = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
            controller.Bounds = new Rectangle(0, 0, width, height);
            controller.IsVisible = IsVisible;
        }
        catch
        {
            // Ignore transient bounds update while tearing down.
        }
    }

    private async Task DisposeWebViewOnUiAsync()
    {
        DisposeWebViewSyncOnUi();
        await Task.Yield();
    }

    private void DisposeWebViewSyncOnUi()
    {
        var core = _core;
        var controller = _controller;

        _core = null;
        _controller = null;
        _environment = null;

        if (_registeredBrowserPid != 0)
        {
            MemoryTrimService.UnregisterTrackedRootPid(_registeredBrowserPid);
            _registeredBrowserPid = 0;
        }

        if (core is not null)
        {
            try
            {
                core.NewWindowRequested -= OnNewWindowRequested;
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnCoreNavigationCompleted;
                core.WebResourceRequested -= OnWebResourceRequested;
                core.ProcessFailed -= OnCoreProcessFailed;
                core.Navigate("about:blank");
            }
            catch
            {
                // Ignore if process already exited.
            }
        }

        if (controller is not null)
        {
            try
            {
                controller.IsVisible = false;
                controller.Close();
            }
            catch
            {
                // Ignore close failure if already torn down.
            }
        }

        core = null;
        controller = null;

        // Promptly release COM RCWs so msedgewebview2.exe terminates quickly.
        GC.Collect(2, GCCollectionMode.Optimized);
        GC.WaitForPendingFinalizers();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);
}
