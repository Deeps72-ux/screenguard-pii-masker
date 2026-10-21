namespace ScreenGuard.App.Models;

/// <summary>
/// Represents the result of a screen capture event.
/// </summary>
public class CaptureResult
{
    /// <summary>Unique capture identifier (e.g. "capture_20261021_231825_985").</summary>
    public string CaptureId { get; set; } = "";

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

    /// <summary>
    /// How the masked image was produced: "ocr" (detected regions masked) or
    /// "full-frame" (OCR unavailable or failed, so the whole frame was redacted).
    /// </summary>
    public string RedactionMode { get; set; } = "ocr";

    /// <summary>"ner" when the on-device name model ran, "labels-only" otherwise.</summary>
    public string NameRecognition { get; set; } = "labels-only";

    /// <summary>Pixel-based detectors that ran on this frame, e.g. ["face"].</summary>
    public List<string> VisualDetectors { get; set; } = [];
}
