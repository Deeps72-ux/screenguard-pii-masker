/**
 * DiskWriter.cs
 * Production-grade disk storage manager for captured screenshots, masked variants,
 * side-by-side comparisons, and compliance reports.
 *
 * Directory structure:
 *   {outputDir}/original/     → Original unmodified screenshots
 *   {outputDir}/masked/       → PII-redacted screenshots
 *   {outputDir}/comparison/   → Side-by-side comparison images
 *   {outputDir}/reports/      → Structured JSON audit reports
 */

using SkiaSharp;
using ScreenGuard.App.Abstractions;

namespace ScreenGuard.App.Services;

/// <summary>
/// Handles writing captured images and comparison artifacts to disk.
/// </summary>
public class DiskWriter : IDiskWriter
{
    public string BaseOutputDir { get; }
    private readonly string _originalDir;
    private readonly string _maskedDir;
    private readonly string _comparisonDir;
    private readonly string _reportsDir;

    /// <summary>
    /// Initialize the disk writer with the base output directory.
    /// Creates the directory structure if it doesn't exist.
    /// </summary>
    public DiskWriter(string outputBaseDir)
    {
        BaseOutputDir = outputBaseDir;
        _originalDir = Path.Combine(outputBaseDir, "original");
        _maskedDir = Path.Combine(outputBaseDir, "masked");
        _comparisonDir = Path.Combine(outputBaseDir, "comparison");
        _reportsDir = Path.Combine(outputBaseDir, "reports");

        Directory.CreateDirectory(_originalDir);
        Directory.CreateDirectory(_maskedDir);
        Directory.CreateDirectory(_comparisonDir);
        Directory.CreateDirectory(_reportsDir);

        Console.WriteLine($"[DiskWriter] Output directory: {outputBaseDir}");
        Console.WriteLine($"[DiskWriter]   Original:   {_originalDir}");
        Console.WriteLine($"[DiskWriter]   Masked:     {_maskedDir}");
        Console.WriteLine($"[DiskWriter]   Comparison: {_comparisonDir}");
        Console.WriteLine($"[DiskWriter]   Reports:    {_reportsDir}");
    }

    /// <summary>
    /// Save the original (unmasked) screenshot.
    /// </summary>
    public string SaveOriginal(SKBitmap bitmap, string timestamp)
    {
        string filename = $"capture_{timestamp}.png";
        string path = Path.Combine(_originalDir, filename);
        SaveBitmapAsPng(bitmap, path);
        return path;
    }

    /// <summary>
    /// Save the PII-masked screenshot.
    /// </summary>
    public string SaveMasked(SKBitmap bitmap, string timestamp)
    {
        string filename = $"capture_{timestamp}_masked.png";
        string path = Path.Combine(_maskedDir, filename);
        SaveBitmapAsPng(bitmap, path);
        return path;
    }

    /// <summary>
    /// Save the side-by-side comparison composite image.
    /// </summary>
    public string SaveComparison(SKBitmap bitmap, string timestamp)
    {
        string filename = $"capture_{timestamp}_comparison.png";
        string path = Path.Combine(_comparisonDir, filename);
        SaveBitmapAsPng(bitmap, path);
        return path;
    }

    /// <summary>
    /// Save the JSON metadata audit report.
    /// </summary>
    public string SaveReport(string jsonContent, string timestamp)
    {
        string filename = $"capture_{timestamp}_report.json";
        string path = Path.Combine(_reportsDir, filename);
        File.WriteAllText(path, jsonContent);
        return path;
    }

    /// <summary>
    /// Safely encode and save an SKBitmap as a PNG file.
    /// </summary>
    private static void SaveBitmapAsPng(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
    }
}
