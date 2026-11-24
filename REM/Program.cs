using System.Runtime.InteropServices;
using Avalonia;

namespace REM;

internal static class Program
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconWarning = 0x00000030;
    private const uint MbSetForeground = 0x00010000;
    private const uint MbTopMost = 0x00040000;

    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\REM_SingleInstance_Mutex_v1",
            out var createdNew);

        if (!createdNew)
        {
            if (OperatingSystem.IsWindows())
            {
                MessageBoxW(
                    IntPtr.Zero,
                    "REM is already running.\n\nPlease check your taskbar or system tray instead of launching a second instance.",
                    "REM — Already Running",
                    MbOk | MbIconWarning | MbSetForeground | MbTopMost);
            }

            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        GC.KeepAlive(singleInstanceMutex);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
                CompositionMode = [Win32CompositionMode.RedirectionSurface]
            })
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = 8 * 1024 * 1024
            })
            .LogToTrace();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
