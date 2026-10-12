namespace SkanCapture.App.Models;

/// <summary>
/// Represents a detected PII match with its type, original text, and screen coordinates.
/// The coordinates correspond to the bounding box of the matched text in the screenshot.
/// </summary>
public class PiiMatch
{
    /// <summary>PII type identifier (e.g., "SSN", "Email", "Phone").</summary>
    public string Type { get; set; } = "";

    /// <summary>The original text that matched the PII pattern.</summary>
    public string OriginalText { get; set; } = "";

    /// <summary>X coordinate of the bounding box (pixels from left edge).</summary>
    public int X { get; set; }

    /// <summary>Y coordinate of the bounding box (pixels from top edge).</summary>
    public int Y { get; set; }

    /// <summary>Width of the bounding box in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Height of the bounding box in pixels.</summary>
    public int Height { get; set; }

    /// <summary>
    /// A partially masked preview of the original text for logging.
    /// Shows first 2 and last 2 characters, with the middle replaced by asterisks.
    /// Example: "john.doe@example.com" → "jo**************om"
    /// </summary>
    public string MaskedPreview
    {
        get
        {
            if (string.IsNullOrEmpty(OriginalText))
                return "";
            if (OriginalText.Length <= 4)
                return new string('*', OriginalText.Length);
            return OriginalText[..2] +
                   new string('*', OriginalText.Length - 4) +
                   OriginalText[^2..];
        }
    }

    public override string ToString() =>
        $"[{Type}] \"{MaskedPreview}\" at ({X},{Y}) {Width}x{Height}";
}
