namespace SkanCapture.App.Models;

/// <summary>
/// Represents the result of a screen capture event.
/// </summary>
public class CaptureResult
{
    /// <summary>Image width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Image height in pixels.</summary>
    public int Height { get; set; }

    /// <summary>Input device that triggered the capture (e.g., "External Mouse", "Trackpad").</summary>
    public string InputSource { get; set; } = "Unknown";

    /// <summary>X coordinate of the click that triggered the capture.</summary>
    public double ClickX { get; set; }

    /// <summary>Y coordinate of the click that triggered the capture.</summary>
    public double ClickY { get; set; }

    /// <summary>Timestamp of the capture event.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Path where the original screenshot was saved.</summary>
    public string OriginalImagePath { get; set; } = "";

    /// <summary>Path where the PII-masked screenshot was saved.</summary>
    public string MaskedImagePath { get; set; } = "";

    /// <summary>Number of PII regions detected and masked.</summary>
    public int PiiCount { get; set; }
}
