/**
 * PiiMasker.cs
 * Production-grade visual PII masking and redaction.
 *
 * Applies non-destructive visual redaction over detected PII regions:
 *   - Solid opaque black redaction block
 *   - High-contrast red perimeter border for clear visual indication
 *   - Redaction type badge overlay (e.g. "[REDACTED: SSN]")
 *   - Boundary clamping against image dimensions to prevent clipping issues
 */

using SkiaSharp;
using ScreenGuard.App.Abstractions;
using ScreenGuard.App.Models;

namespace ScreenGuard.App.Services;

/// <summary>
/// Applies visual redaction masks over detected PII regions in screenshots.
/// </summary>
public class PiiMasker : IPiiMasker
{
    private readonly SKColor _maskColor = SKColors.Black;
    private readonly SKColor _accentColor = new(229, 57, 53); // Crimson Red
    private readonly int _padding = 4;

    /// <summary>
    /// Create a masked copy of the bitmap with black rectangles and badges over PII regions.
    /// </summary>
    /// <param name="original">The original screenshot bitmap.</param>
    /// <param name="piiMatches">List of detected PII regions to mask.</param>
    /// <returns>A new bitmap with PII regions redacted.</returns>
    public SKBitmap ApplyMask(SKBitmap original, List<PiiMatch>? piiMatches)
    {
        var masked = original.Copy();

        if (piiMatches == null || piiMatches.Count == 0)
            return masked;

        using var canvas = new SKCanvas(masked);

        using var maskPaint = new SKPaint
        {
            Color = _maskColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = false
        };

        using var borderPaint = new SKPaint
        {
            Color = _accentColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true
        };

        using var badgeBgPaint = new SKPaint
        {
            Color = _accentColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        using var badgeTextPaint = new SKPaint
        {
            Color = SKColors.White,
            TextSize = 11f,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
        };

        foreach (var match in piiMatches)
        {
            // Clamp coordinates to image boundaries
            float left = Math.Clamp(match.X - _padding, 0, original.Width - 1);
            float top = Math.Clamp(match.Y - _padding, 0, original.Height - 1);
            float right = Math.Clamp(match.X + match.Width + _padding, 0, original.Width);
            float bottom = Math.Clamp(match.Y + match.Height + _padding, 0, original.Height);

            var rect = new SKRect(left, top, right, bottom);

            // 1. Draw solid black redaction block
            canvas.DrawRect(rect, maskPaint);

            // 2. Draw crisp red boundary outline
            canvas.DrawRect(rect, borderPaint);

            // 3. Draw badge label
            string badgeText = $"REDACTED: {match.Type.ToUpperInvariant()}";
            float textWidth = badgeTextPaint.MeasureText(badgeText);
            float badgeHeight = 16f;
            float badgeWidth = textWidth + 8f;

            float badgeY = (top - badgeHeight - 2 >= 0) ? top - badgeHeight - 2 : top + 2;
            float badgeX = Math.Clamp(left, 0, original.Width - badgeWidth);

            var badgeRect = new SKRoundRect(new SKRect(badgeX, badgeY, badgeX + badgeWidth, badgeY + badgeHeight), 3f);
            canvas.DrawRoundRect(badgeRect, badgeBgPaint);
            canvas.DrawText(badgeText, badgeX + 4f, badgeY + badgeHeight - 4f, badgeTextPaint);
        }

        canvas.Flush();
        return masked;
    }
}
