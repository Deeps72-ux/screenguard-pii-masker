using SkiaSharp;
using ScreenGuard.App.Models;
using ScreenGuard.App.Services;

namespace ScreenGuard.App.Abstractions;

/// <summary>
/// Abstraction for OCR text extraction engines (Tesseract CLI, Native, etc.).
/// </summary>
public interface IOcrEngine : IDisposable
{
    string ActiveBackend { get; }
    List<OcrResult> ExtractTextWithBounds(SKBitmap bitmap);
    string ExtractText(SKBitmap bitmap);
}

/// <summary>
/// Abstraction for detecting PII entities from text tokens and bounding boxes.
/// </summary>
public interface IPiiDetector
{
    List<PiiMatch> DetectPii(List<OcrResult> ocrResults);
}

/// <summary>
/// Abstraction for recognising free-standing person names (no label or title) in OCR lines.
/// </summary>
public interface INameRecognizer : IDisposable
{
    string Description { get; }

    /// <summary>Returns each run of consecutive words recognised as a person's name.</summary>
    List<List<OcrResult>> FindNames(IReadOnlyList<List<OcrResult>> lines);
}

/// <summary>
/// Abstraction for non-destructive visual redaction of PII entities.
/// </summary>
public interface IPiiMasker
{
    SKBitmap ApplyMask(SKBitmap original, List<PiiMatch>? piiMatches);
}

/// <summary>
/// Abstraction for side-by-side composite rendering and audit report generation.
/// </summary>
public interface IImageComparer
{
    SKBitmap CreateSideBySideComparison(
        SKBitmap original,
        SKBitmap masked,
        List<PiiMatch> piiMatches,
        string captureId,
        string inputSource);

    string GenerateJsonReport(
        CaptureResult result,
        List<PiiMatch> piiMatches,
        string comparisonImagePath);
}

/// <summary>
/// Abstraction for persisting original, masked, comparison, and report artifacts to disk.
/// </summary>
public interface IDiskWriter
{
    string BaseOutputDir { get; }
    string SaveOriginal(SKBitmap bitmap, string timestamp);
    string SaveMasked(SKBitmap bitmap, string timestamp);
    string SaveComparison(SKBitmap bitmap, string timestamp);
    string SaveReport(string jsonContent, string timestamp);
}
