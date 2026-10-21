using SkiaSharp;
using ScreenGuard.App.Models;

namespace ScreenGuard.App.Abstractions;

/// <summary>
/// Detects PII directly in pixels (faces today; e.g. signatures or QR codes later), as opposed
/// to <see cref="IPiiDetector"/>, which works on OCR text. New visual detectors implement this
/// interface and are registered in <c>VisualDetectorFactory</c>; the capture pipeline does not
/// change (Open/Closed).
/// </summary>
public interface IVisualPiiDetector : IDisposable
{
    /// <summary>Short identifier recorded in reports, e.g. "face".</summary>
    string Name { get; }

    /// <summary>Human-readable description for startup logs.</summary>
    string Description { get; }

    /// <summary>
    /// Returns regions to redact, in <paramref name="frame"/> pixel coordinates.
    /// Throws on failure so the caller can fail closed.
    /// </summary>
    List<PiiMatch> Detect(SKBitmap frame);
}
