using SkiaSharp;

namespace ScreenGuard.App.Models;

/// <summary>
/// Cross-platform representation of a captured screen frame triggered by an input event.
/// </summary>
public class CapturedFrame : IDisposable
{
    /// <summary>The captured frame bitmap.</summary>
    public SKBitmap Bitmap { get; set; }

    /// <summary>Hardware source of the input event ("External Mouse", "Trackpad", or "Synthetic").</summary>
    public string InputSource { get; set; } = "Unknown";

    /// <summary>X coordinate of the cursor at the moment of capture.</summary>
    public double ClickX { get; set; }

    /// <summary>Y coordinate of the cursor at the moment of capture.</summary>
    public double ClickY { get; set; }

    /// <summary>Timestamp in epoch milliseconds.</summary>
    public long TimestampMs { get; set; }

    /// <summary>Frame width in pixels.</summary>
    public int Width => Bitmap.Width;

    /// <summary>Frame height in pixels.</summary>
    public int Height => Bitmap.Height;

    public CapturedFrame(SKBitmap bitmap, string inputSource, double clickX, double clickY, long timestampMs)
    {
        Bitmap = bitmap;
        InputSource = inputSource;
        ClickX = clickX;
        ClickY = clickY;
        TimestampMs = timestampMs;
    }

    public void Dispose()
    {
        Bitmap.Dispose();
        GC.SuppressFinalize(this);
    }
}
