/**
 * OcrEngine.cs
 * Production-grade OCR engine for extracting text and bounding boxes from screenshots.
 *
 * Implements a dual-engine architecture:
 *   1. Tesseract CLI Engine: Pipes PNG image bytes directly via stdin and parses TSV from stdout.
 *      Zero-temp-files on disk, highly optimized with Apple Silicon NEON acceleration, robust,
 *      and immune to filesystem symlink / permissions issues.
 *   2. Tesseract Native Engine: In-process TesseractEngine via P/Invoke.
 *
 * Provides accurate word-level bounding boxes (X, Y, Width, Height) and confidence
 * scores, feeding directly into the PII detector and visual masker.
 */

using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Tesseract;

namespace ScreenGuard.App.Services;

/// <summary>
/// Result from OCR text extraction, including text content, bounding box, and confidence.
/// </summary>
public class OcrResult
{
    /// <summary>The recognized text content.</summary>
    public string Text { get; set; } = "";

    /// <summary>X coordinate of the bounding box (pixels from left).</summary>
    public int X { get; set; }

    /// <summary>Y coordinate of the bounding box (pixels from top).</summary>
    public int Y { get; set; }

    /// <summary>Width of the bounding box in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Height of the bounding box in pixels.</summary>
    public int Height { get; set; }

    /// <summary>Recognition confidence score (0 to 100).</summary>
    public float Confidence { get; set; }

    /// <summary>
    /// Identifier of the text line this word belongs to, as reported by the OCR engine.
    /// -1 when unknown (the PII detector then groups lines geometrically).
    /// </summary>
    public int LineId { get; set; } = -1;

    public override string ToString() => $"\"{Text}\" at ({X}, {Y}, {Width}x{Height}) [{Confidence:F1}%]";
}

/// <summary>
/// Production-grade OCR engine wrapper.
/// Extraction failures throw, so callers can fail closed instead of treating
/// an OCR error as "no PII on screen".
/// </summary>
public class OcrEngine : ScreenGuard.App.Abstractions.IOcrEngine
{
    private const int CliTimeoutMs = 60_000;

    private readonly string? _tessDataPath;
    private readonly string? _tesseractBinaryPath;
    private readonly TesseractEngine? _nativeEngine;
    private readonly bool _useCli;
    private readonly object _nativeLock = new(); // TesseractEngine is not thread-safe
    private bool _disposed;

    /// <summary>Name of the active OCR backend.</summary>
    public string ActiveBackend { get; }

    /// <summary>
    /// Initialize the OCR engine.
    /// Attempts native Tesseract first; falls back gracefully to Tesseract CLI.
    /// </summary>
    /// <param name="tessDataPath">Optional path to tessdata folder containing eng.traineddata.</param>
    public OcrEngine(string? tessDataPath = null)
    {
        _tessDataPath = tessDataPath;

        // Find tesseract binary: bundled copy first, then common macOS and Windows install paths
        string baseDir = AppContext.BaseDirectory;
        string[] candidateBinaries =
        [
            Path.Combine(baseDir, "..", "Frameworks", "tesseract", "tesseract"),
            Path.Combine(baseDir, "tesseract", "tesseract"),
            Path.Combine(baseDir, "tesseract", "tesseract.exe"),
            "/opt/homebrew/bin/tesseract",
            "/usr/local/bin/tesseract",
            @"C:\Program Files\Tesseract-OCR\tesseract.exe",
            @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe",
            "tesseract.exe",
            "tesseract"
        ];

        foreach (var candidate in candidateBinaries)
        {
            bool isBareName = candidate.IndexOfAny(['/', '\\']) < 0;
            if (File.Exists(candidate) || isBareName)
            {
                try
                {
                    using var proc = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = candidate,
                            Arguments = "--version",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    proc.Start();
                    proc.WaitForExit(2000);
                    if (proc.ExitCode == 0)
                    {
                        _tesseractBinaryPath = candidate;
                        break;
                    }
                }
                catch
                {
                    // Continue searching
                }
            }
        }

        // Try initializing native in-process engine if tessdata path is provided
        if (!string.IsNullOrEmpty(tessDataPath) && Directory.Exists(tessDataPath))
        {
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(TesseractEngine).Assembly, ResolveNativeLibrary);
                _nativeEngine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
            }
            catch
            {
                _nativeEngine = null;
            }
        }

        if (_nativeEngine != null)
        {
            _useCli = false;
            ActiveBackend = "Tesseract Native (.NET P/Invoke)";
        }
        else if (!string.IsNullOrEmpty(_tesseractBinaryPath))
        {
            _useCli = true;
            ActiveBackend = $"Tesseract CLI ({_tesseractBinaryPath})";
        }
        else
        {
            throw new InvalidOperationException("No working Tesseract OCR backend found (neither native nor CLI).");
        }
    }

    /// <summary>
    /// Extract text with bounding boxes from a SkiaSharp bitmap.
    /// </summary>
    public List<OcrResult> ExtractTextWithBounds(SKBitmap bitmap)
    {
        if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
            return [];

        // Two passes: the screenshot as-is, and a high-contrast black-on-white copy that
        // recovers text OCR drops under selection highlights or on tinted backgrounds.
        using var highContrast = CreateHighContrastCopy(bitmap);

        List<OcrResult> primary, secondary;
        if (_useCli)
        {
            // One tesseract process per pass: run both concurrently
            var primaryTask = Task.Run(() => ExtractUsingCli(bitmap));
            var secondaryTask = Task.Run(() => ExtractUsingCli(highContrast));
            primary = primaryTask.GetAwaiter().GetResult();
            secondary = secondaryTask.GetAwaiter().GetResult();
        }
        else
        {
            lock (_nativeLock)
            {
                primary = ExtractUsingNative(bitmap);
                secondary = ExtractUsingNative(highContrast);
            }
        }

        return MergePasses(primary, secondary);
    }

    /// <summary>
    /// Adds words from the secondary pass that the primary pass did not find. Line ids from
    /// the two passes are not comparable, so they are cleared and lines are regrouped by
    /// geometry in the PII detector.
    /// </summary>
    internal static List<OcrResult> MergePasses(List<OcrResult> primary, List<OcrResult> secondary)
    {
        var merged = new List<OcrResult>(primary);
        foreach (var word in secondary)
        {
            long area = Math.Max(1L, (long)word.Width * word.Height);
            bool covered = primary.Any(p =>
            {
                long w = Math.Min(p.X + p.Width, word.X + word.Width) - Math.Max(p.X, word.X);
                long h = Math.Min(p.Y + p.Height, word.Y + word.Height) - Math.Max(p.Y, word.Y);
                return w > 0 && h > 0 && w * h > area * 0.3;
            });
            if (!covered)
                merged.Add(word);
        }

        foreach (var word in merged)
            word.LineId = -1;
        return merged;
    }

    /// <summary>
    /// Binarizes by luminance with per-tile polarity: in each tile the majority class is
    /// treated as background, so light-on-dark text, dark-on-light text and text inside a
    /// selection highlight all become black text on white.
    /// </summary>
    internal static unsafe SKBitmap CreateHighContrastCopy(SKBitmap source)
    {
        const int Tile = 48;
        const int Threshold = 128;

        using var bgra = source.ColorType == SKColorType.Bgra8888 ? null : source.Copy(SKColorType.Bgra8888);
        var src = bgra ?? source;
        int width = src.Width, height = src.Height;
        byte* srcPixels = (byte*)src.GetPixels();
        int srcStride = src.RowBytes;

        var result = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        byte* dstPixels = (byte*)result.GetPixels();
        int dstStride = result.RowBytes;

        // Pass 1: luminance mask (1 = light)
        for (int y = 0; y < height; y++)
        {
            byte* s = srcPixels + (long)y * srcStride;
            byte* d = dstPixels + (long)y * dstStride;
            for (int x = 0; x < width; x++, s += 4)
                d[x] = (byte)((s[2] * 77 + s[1] * 150 + s[0] * 29) >> 8 >= Threshold ? 1 : 0);
        }

        // Pass 2: per tile, the minority class is ink (black), the majority background (white)
        for (int ty = 0; ty < height; ty += Tile)
        {
            for (int tx = 0; tx < width; tx += Tile)
            {
                int yEnd = Math.Min(ty + Tile, height), xEnd = Math.Min(tx + Tile, width);
                int light = 0, total = (yEnd - ty) * (xEnd - tx);
                for (int y = ty; y < yEnd; y++)
                {
                    byte* d = dstPixels + (long)y * dstStride;
                    for (int x = tx; x < xEnd; x++) light += d[x];
                }
                byte inkValue = light * 2 >= total ? (byte)0 : (byte)1; // light background → dark pixels are ink
                for (int y = ty; y < yEnd; y++)
                {
                    byte* d = dstPixels + (long)y * dstStride;
                    for (int x = tx; x < xEnd; x++) d[x] = d[x] == inkValue ? (byte)0 : (byte)255;
                }
            }
        }

        result.NotifyPixelsChanged();
        return result;
    }

    /// <summary>
    /// Extract full text without bounding boxes.
    /// </summary>
    public string ExtractText(SKBitmap bitmap)
    {
        var results = ExtractTextWithBounds(bitmap);
        return string.Join(" ", results.Select(r => r.Text));
    }

    /// <summary>
    /// CLI-based extraction with streaming stdin and TSV output parsing.
    /// Eliminates temp files and disk latency.
    /// </summary>
    private List<OcrResult> ExtractUsingCli(SKBitmap bitmap)
    {
        var results = new List<OcrResult>();

        var psi = new ProcessStartInfo
        {
            FileName = _tesseractBinaryPath ?? "tesseract",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("stdin");
        psi.ArgumentList.Add("stdout");

        // Point tesseract at our bundled language data when available
        if (!string.IsNullOrEmpty(_tessDataPath) && Directory.Exists(_tessDataPath))
        {
            psi.ArgumentList.Add("--tessdata-dir");
            psi.ArgumentList.Add(_tessDataPath);
        }

        // Request TSV via parameters rather than the "tsv" config file, which only exists
        // in a full tessdata/configs install and silently degrades to plain text otherwise
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("tessedit_create_tsv=1");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("tessedit_create_txt=0");

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Drain both pipes concurrently so a chatty stderr can never deadlock stdout
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // Stream PNG encoded bytes directly to tesseract stdin in memory
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        {
            data.SaveTo(process.StandardInput.BaseStream);
            process.StandardInput.BaseStream.Flush();
            process.StandardInput.BaseStream.Close();
        }

        if (!process.WaitForExit(CliTimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw new TimeoutException($"Tesseract did not finish within {CliTimeoutMs / 1000}s.");
        }

        string tsvOutput = stdoutTask.GetAwaiter().GetResult();
        string errOutput = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Tesseract exited with code {process.ExitCode}: {errOutput.Trim()}");

        if (!tsvOutput.StartsWith("level\t", StringComparison.Ordinal))
            throw new InvalidOperationException("Tesseract did not return TSV output.");

        using var reader = new StringReader(tsvOutput);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            string[] parts = line.Split('\t');
            // TSV format: level, page_num, block_num, par_num, line_num, word_num, left, top, width, height, conf, text
            if (parts.Length >= 12 && parts[0].Trim() == "5") // Level 5 = Word
            {
                string wordText = parts[11].Trim();
                if (!string.IsNullOrWhiteSpace(wordText) &&
                    int.TryParse(parts[2].Trim(), out int block) &&
                    int.TryParse(parts[3].Trim(), out int par) &&
                    int.TryParse(parts[4].Trim(), out int lineNum) &&
                    int.TryParse(parts[6].Trim(), out int x) &&
                    int.TryParse(parts[7].Trim(), out int y) &&
                    int.TryParse(parts[8].Trim(), out int w) &&
                    int.TryParse(parts[9].Trim(), out int h))
                {
                    float.TryParse(parts[10].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float conf);
                    results.Add(new OcrResult
                    {
                        Text = wordText,
                        X = x,
                        Y = y,
                        Width = w,
                        Height = h,
                        Confidence = conf,
                        LineId = (block * 1000 + par) * 1000 + lineNum
                    });
                }
            }
        }

        return results;
    }

    /// <summary>
    /// In-process native TesseractEngine extraction.
    /// </summary>
    private List<OcrResult> ExtractUsingNative(SKBitmap bitmap)
    {
        var results = new List<OcrResult>();
        if (_nativeEngine == null) return results;

        string tempPng = Path.Combine(Path.GetTempPath(), $"screenguard_ocr_{Guid.NewGuid():N}.png");

        try
        {
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = File.Create(tempPng))
            {
                data.SaveTo(stream);
                stream.Flush(true);
            }

            using var pix = Pix.LoadFromFile(tempPng);
            using var page = _nativeEngine.Process(pix);
            using var iter = page.GetIterator();

            if (iter != null)
            {
                int lineId = -1;
                iter.Begin();
                do
                {
                    if (iter.IsAtBeginningOf(PageIteratorLevel.TextLine))
                        lineId++;

                    if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out var bounds))
                    {
                        string? word = iter.GetText(PageIteratorLevel.Word);
                        float conf = iter.GetConfidence(PageIteratorLevel.Word);
                        if (!string.IsNullOrWhiteSpace(word))
                        {
                            results.Add(new OcrResult
                            {
                                Text = word.Trim(),
                                X = bounds.X1,
                                Y = bounds.Y1,
                                Width = bounds.Width,
                                Height = bounds.Height,
                                Confidence = conf,
                                LineId = Math.Max(lineId, 0)
                            });
                        }
                    }
                } while (iter.Next(PageIteratorLevel.Word));
            }
        }
        finally
        {
            try { if (File.Exists(tempPng)) File.Delete(tempPng); } catch { /* ignore */ }
        }

        return results;
    }

    /// <summary>
    /// Custom DllImport resolver mapping Charlesw native DLL names to macOS dylibs.
    /// </summary>
    private static IntPtr ResolveNativeLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string[] possiblePaths = libraryName switch
            {
                "tesseract50" or "tesseract" =>
                [
                    "/opt/homebrew/lib/libtesseract.dylib",
                    "/opt/homebrew/lib/libtesseract.5.dylib",
                    "/usr/local/lib/libtesseract.dylib"
                ],
                "leptonica-1.82.0" or "leptonica" or "liblept" =>
                [
                    "/opt/homebrew/lib/libleptonica.dylib",
                    "/opt/homebrew/lib/libleptonica.6.dylib",
                    "/usr/local/lib/libleptonica.dylib"
                ],
                _ => []
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                {
                    return handle;
                }
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _nativeEngine?.Dispose();
            _disposed = true;
        }
    }
}
