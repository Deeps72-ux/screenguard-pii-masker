using ScreenGuard.App.Models;

namespace ScreenGuard.App.Abstractions;

/// <summary>
/// Abstraction for monitoring global hardware input events (Mouse clicks vs Trackpad taps)
/// across operating systems (macOS Quartz EventTap vs Windows SetWindowsHookEx).
/// </summary>
public interface IGlobalInputMonitor : IDisposable
{
    /// <summary>Event raised whenever a mouse or trackpad click triggers a screen capture.</summary>
    event Action<CapturedFrame> OnFrameCaptured;

    /// <summary>Starts listening for global input events.</summary>
    bool Start();

    /// <summary>Stops monitoring and cleans up hooks.</summary>
    void Stop();
}
