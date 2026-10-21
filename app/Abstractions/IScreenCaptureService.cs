using ScreenGuard.App.Models;

namespace ScreenGuard.App.Abstractions;

/// <summary>
/// Abstraction for performing immediate screen captures across platforms (macOS ScreenCaptureKit, Windows GDI/DXGI).
/// Follows Interface Segregation (ISP) and Dependency Inversion (DIP).
/// </summary>
public interface IScreenCaptureService
{
    /// <summary>Captures the current screen display.</summary>
    CapturedFrame? CaptureScreen();

    /// <summary>
    /// Captures the display containing the given screen point. Platforms that capture a
    /// fixed display ignore the point.
    /// </summary>
    CapturedFrame? CaptureScreenAt(int x, int y) => CaptureScreen();
}
