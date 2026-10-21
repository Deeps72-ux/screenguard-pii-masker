using System.Runtime.InteropServices;

namespace ScreenGuard.App.Platforms.Windows;

/// <summary>
/// Opts the process into per-monitor DPI awareness so that Win32 metrics, hook coordinates
/// and GDI capture all use physical pixels. Without this, on a laptop scaled to 125%/150%
/// Windows reports a virtualized (smaller) screen and only part of the screen is captured.
/// Must run before any window, DC or hook is created.
/// </summary>
public static class WindowsDpiAwareness
{
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    private const int PROCESS_PER_MONITOR_DPI_AWARE = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    public static void Enable()
    {
        // Windows 10 1703+
        try
        {
            if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                return;
        }
        catch (EntryPointNotFoundException) { }

        // Windows 8.1+
        try
        {
            if (SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE) == 0)
                return;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        // Vista+ (system-DPI aware)
        try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
    }
}
