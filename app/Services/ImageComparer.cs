/**
 * ImageComparer.cs
 * Side-by-side comparison engine and report generator.
 *
 * Implements the assignment requirement: "saved to disk – original and masked — compare"
 *
 * Capabilities:
 *   1. Generates a composite side-by-side comparison image:
 *      - Left side: Original unmodified screenshot with green header banner
 *      - Right side: Redacted screenshot with crimson header banner
 *      - Central vertical divider line
 *      - Information dashboard footer detailing input device, click coordinates,
 *        PII counts, and resolution.
 *   2. Generates comprehensive JSON comparison audit reports for compliance.
 */

using System.Text.Json;
using SkiaSharp;
using ScreenGuard.App.Abstractions;
using ScreenGuard.App.Models;

namespace ScreenGuard.App.Services;

/// <summary>
/// Compares original and masked captures, generating visual diffs and audit reports.
/// </summary>
public class ImageComparer : IImageComparer
{
    private const int HeaderHeight = 50;
    private const int FooterHeight = 44;
    private const int DividerWidth = 4;

    /// <summary>
    /// Creates a side-by-side composite comparison image of original vs masked.
    /// </summary>
    /// <param name="original">The original screenshot.</param>
    /// <param name="masked">The redacted screenshot.</param>
    /// <param name="piiMatches">List of detected and redacted PII items.</param>
    /// <param name="captureId">Unique capture ID/timestamp.</param>
    /// <param name="inputSource">Detected input source (External Mouse vs Trackpad).</param>
    /// <returns>A new composite SKBitmap containing the side-by-side comparison.</returns>
    public SKBitmap CreateSideBySideComparison(
        SKBitmap original,
        SKBitmap masked,
        List<PiiMatch> piiMatches,
        string captureId,
        string inputSource)
    {
        int targetWidth = original.Width * 2 + DividerWidth;
        int targetHeight = original.Height + HeaderHeight + FooterHeight;

        var info = new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        var composite = new SKBitmap(info);

        using var canvas = new SKCanvas(composite);
        canvas.Clear(new SKColor(24, 24, 37)); // Modern Dark theme background (#181825)

        // ── 1. Top Header ──────────────────────────────────────────────
        using var headerBgPaint = new SKPaint
        {
            Color = new SKColor(30, 30, 46),
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRect(new SKRect(0, 0, targetWidth, HeaderHeight), headerBgPaint);

        using var titleFont = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            Typeface = titleFont,
            TextSize = 18f
        };

        // Left Header: Original
        textPaint.Color = new SKColor(46, 204, 113); // Emerald Green
        canvas.DrawCircle(20, HeaderHeight / 2f, 6, textPaint);
        canvas.DrawText("ORIGINAL CAPTURE (Unmasked)", 36, (HeaderHeight / 2f) + 6, textPaint);

        // Right Header: Redacted
        int rightHalfX = original.Width + DividerWidth;
        textPaint.Color = new SKColor(231, 76, 60); // Crimson Red
        canvas.DrawCircle(rightHalfX + 20, HeaderHeight / 2f, 6, textPaint);
        canvas.DrawText("REDACTED VIEW (PII Masked)", rightHalfX + 36, (HeaderHeight / 2f) + 6, textPaint);

        // ── 2. Images ──────────────────────────────────────────────────
        // Draw Original on the left
        canvas.DrawBitmap(original, 0, HeaderHeight);

        // Draw Divider in center
        using var dividerPaint = new SKPaint
        {
            Color = new SKColor(88, 91, 112),
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRect(new SKRect(original.Width, HeaderHeight, original.Width + DividerWidth, HeaderHeight + original.Height), dividerPaint);

        // Draw Masked on the right
        canvas.DrawBitmap(masked, rightHalfX, HeaderHeight);

        // ── 3. Bottom Footer Dashboard ─────────────────────────────────
        int footerY = HeaderHeight + original.Height;
        using var footerBgPaint = new SKPaint
        {
            Color = new SKColor(30, 30, 46),
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRect(new SKRect(0, footerY, targetWidth, targetHeight), footerBgPaint);

        using var footerFont = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        using var footerTextPaint = new SKPaint
        {
            Color = new SKColor(205, 214, 244),
            TextSize = 14f,
            IsAntialias = true,
            Typeface = footerFont
        };

        string piiBreakdown = (piiMatches.Count > 0)
            ? string.Join(", ", piiMatches.GroupBy(m => m.Type).Select(g => $"{g.Key}: {g.Count()}"))
            : "None";

        string footerText = $"Capture: {captureId}   |   Input: {inputSource}   |   Resolution: {original.Width}x{original.Height}   |   PII Detected: {piiMatches.Count} ({piiBreakdown})";
        canvas.DrawText(footerText, 20, footerY + (FooterHeight / 2f) + 5, footerTextPaint);

        canvas.Flush();
        return composite;
    }

    /// <summary>
    /// Generates a structured JSON comparison metadata report.
    /// </summary>
    public string GenerateJsonReport(
        CaptureResult result,
        List<PiiMatch> piiMatches,
        string comparisonImagePath)
    {
        var report = new
        {
            captureId = Path.GetFileNameWithoutExtension(result.OriginalImagePath),
            timestamp = result.Timestamp.ToString("o"),
            inputSource = result.InputSource,
            clickCoordinates = new { x = result.ClickX, y = result.ClickY },
            dimensions = new { width = result.Width, height = result.Height },
            piiSummary = new
            {
                totalDetected = piiMatches.Count,
                types = piiMatches.GroupBy(m => m.Type).ToDictionary(g => g.Key, g => g.Count())
            },
            piiRegions = piiMatches.Select(m => new
            {
                type = m.Type,
                maskedPreview = m.MaskedPreview,
                boundingBox = new { x = m.X, y = m.Y, width = m.Width, height = m.Height }
            }),
            files = new
            {
                original = result.OriginalImagePath,
                masked = result.MaskedImagePath,
                comparison = comparisonImagePath
            }
        };

        return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    }
}
