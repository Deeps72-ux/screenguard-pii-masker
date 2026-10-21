/**
 * CaptureProcessor.cs
 * The per-frame redaction pipeline: save original → detect PII (text and pixels) → mask →
 * compare → report.
 *
 * Single responsibility: turning one captured frame into its output files. All collaborators
 * are injected as abstractions (Dependency Inversion), so platforms, OCR engines and detectors
 * can be swapped or added without changing this class. Visual detectors run concurrently with
 * OCR, since neither depends on the other.
 *
 * Fail-closed policy: if OCR or any visual detector fails, the pipeline cannot know where PII
 * is, so the masked output redacts the entire frame instead of passing it through.
 */

using SkiaSharp;
using ScreenGuard.App.Abstractions;
using ScreenGuard.App.Models;

namespace ScreenGuard.App.Services;

/// <summary>Settings that shape what the pipeline writes.</summary>
/// <param name="SaveOriginals">Write the unmasked original screenshot.</param>
/// <param name="NameRecognition">"ner" or "labels-only", recorded in each report.</param>
public sealed record CaptureProcessorOptions(bool SaveOriginals, string NameRecognition);

public sealed class CaptureProcessor
{
    private readonly IOcrEngine? _ocrEngine;
    private readonly IPiiDetector _textDetector;
    private readonly IReadOnlyList<IVisualPiiDetector> _visualDetectors;
    private readonly IPiiMasker _masker;
    private readonly IImageComparer _comparer;
    private readonly IDiskWriter _diskWriter;
    private readonly CaptureProcessorOptions _options;

    public CaptureProcessor(
        IOcrEngine? ocrEngine,
        IPiiDetector textDetector,
        IReadOnlyList<IVisualPiiDetector> visualDetectors,
        IPiiMasker masker,
        IImageComparer comparer,
        IDiskWriter diskWriter,
        CaptureProcessorOptions options)
    {
        _ocrEngine = ocrEngine;
        _textDetector = textDetector;
        _visualDetectors = visualDetectors;
        _masker = masker;
        _comparer = comparer;
        _diskWriter = diskWriter;
        _options = options;
    }

    /// <summary>Processes one frame and returns its audit record.</summary>
    public CaptureResult Process(SKBitmap bitmap, string inputSource, double clickX, double clickY,
                                 long timestampMs, CaptureLog log)
    {
        string timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs)
            .ToLocalTime().ToString("yyyyMMdd_HHmmss_fff");
        string captureId = $"capture_{timestamp}";

        // 1. Save original screenshot
        string originalPath = "";
        if (_options.SaveOriginals)
        {
            originalPath = _diskWriter.SaveOriginal(bitmap, timestamp);
            log.Color(ConsoleColor.Green);
            log.Line($"│  [✓] Original saved:   {Path.GetFileName(originalPath)}");
            log.Reset();
        }

        // 2. Detect PII: visual detectors run in parallel with OCR + text detection
        var visualTasks = _visualDetectors
            .Select(detector => (Detector: detector, Task: Task.Run(() => detector.Detect(bitmap))))
            .ToList();

        var textOutcome = DetectTextPii(bitmap, log);
        var visualOutcomes = visualTasks.Select(v => CollectVisual(v.Detector, v.Task, log)).ToList();

        bool allSucceeded = textOutcome.Succeeded && visualOutcomes.All(o => o.Succeeded);
        var piiMatches = textOutcome.Matches.Concat(visualOutcomes.SelectMany(o => o.Matches)).ToList();

        if (allSucceeded)
        {
            LogMatches(piiMatches, log);
        }
        else
        {
            // Fail closed: a failed detector means PII may be anywhere in the frame
            piiMatches = [FullFrame(bitmap)];
            log.Color(ConsoleColor.Yellow);
            log.Line("│  [!] Detection incomplete — entire frame redacted (fail closed).");
            log.Reset();
        }

        // 3. Apply Visual Masking
        using var maskedBitmap = _masker.ApplyMask(bitmap, piiMatches);
        string maskedPath = _diskWriter.SaveMasked(maskedBitmap, timestamp);
        log.Color(ConsoleColor.Green);
        log.Line($"│  [✓] Masked saved:     {Path.GetFileName(maskedPath)}");
        log.Reset();

        // 4. Create Side-by-Side Comparison Composite
        using var comparisonBitmap = _comparer.CreateSideBySideComparison(
            bitmap, maskedBitmap, piiMatches, captureId, inputSource);
        string comparisonPath = _diskWriter.SaveComparison(comparisonBitmap, timestamp);
        log.Color(ConsoleColor.Cyan);
        log.Line($"│  [✓] Comparison saved: {Path.GetFileName(comparisonPath)}");
        log.Reset();

        // 5. Generate Audit Report
        var result = new CaptureResult
        {
            CaptureId = captureId,
            Width = bitmap.Width,
            Height = bitmap.Height,
            InputSource = inputSource,
            ClickX = clickX,
            ClickY = clickY,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs),
            OriginalImagePath = originalPath,
            MaskedImagePath = maskedPath,
            PiiCount = allSucceeded ? piiMatches.Count : 0,
            RedactionMode = allSucceeded ? "ocr" : "full-frame",
            NameRecognition = _options.NameRecognition,
            VisualDetectors = _visualDetectors.Select(d => d.Name).ToList()
        };

        string jsonReport = _comparer.GenerateJsonReport(result, piiMatches, comparisonPath);
        string reportPath = _diskWriter.SaveReport(jsonReport, timestamp);
        log.Color(ConsoleColor.DarkCyan);
        log.Line($"│  [✓] Report saved:     {Path.GetFileName(reportPath)}");
        log.Reset();

        log.Color(ConsoleColor.DarkGray);
        log.Line("└───────────────────────────────────────────────────────");
        log.Reset();

        return result;
    }

    private readonly record struct DetectionOutcome(bool Succeeded, List<PiiMatch> Matches);

    private DetectionOutcome DetectTextPii(SKBitmap bitmap, CaptureLog log)
    {
        if (_ocrEngine == null)
        {
            log.Color(ConsoleColor.Yellow);
            log.Line("│  [!] OCR unavailable.");
            log.Reset();
            return new DetectionOutcome(false, []);
        }

        try
        {
            var ocrResults = _ocrEngine.ExtractTextWithBounds(bitmap);
            log.Color(ConsoleColor.DarkGray);
            log.Line($"│  [~] OCR extracted {ocrResults.Count} word tokens. Scanning for PII...");
            log.Reset();
            return new DetectionOutcome(true, _textDetector.DetectPii(ocrResults));
        }
        catch (Exception ex)
        {
            log.Color(ConsoleColor.Yellow);
            log.Line($"│  [!] OCR / PII detection failed: {ex.Message}");
            log.Reset();
            return new DetectionOutcome(false, []);
        }
    }

    private static DetectionOutcome CollectVisual(IVisualPiiDetector detector, Task<List<PiiMatch>> task, CaptureLog log)
    {
        try
        {
            return new DetectionOutcome(true, task.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            log.Color(ConsoleColor.Yellow);
            log.Line($"│  [!] {detector.Name} detection failed: {ex.Message}");
            log.Reset();
            return new DetectionOutcome(false, []);
        }
    }

    private static void LogMatches(List<PiiMatch> matches, CaptureLog log)
    {
        if (matches.Count == 0)
        {
            log.Color(ConsoleColor.DarkGray);
            log.Line("│  [i] Detection completed: 0 PII entities detected.");
            log.Reset();
            return;
        }

        log.Color(ConsoleColor.Magenta);
        log.Line($"│  [!] Detected {matches.Count} PII entity(s) for redaction:");
        foreach (var match in matches)
            log.Line($"│      • {match}");
        log.Reset();
    }

    private static PiiMatch FullFrame(SKBitmap bitmap) => new()
    {
        Type = "FullFrame",
        OriginalText = "",
        X = 0,
        Y = 0,
        Width = bitmap.Width,
        Height = bitmap.Height
    };
}
