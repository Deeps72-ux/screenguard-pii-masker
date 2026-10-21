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

    public override string ToString() => $"\"{Text}\" at ({X}, {Y}, {Width}x{Height}) [{Confidence:F1}%]";
}

/// <summary>
/// Production-grade OCR engine wrapper.
/// <summary>
/// Production-grade OCR engine wrapper.
/// </summary>
public class OcrEngine : ScreenGuard.App.Abstractions.IOcrEngine
{
    private readonly string? _tessDataPath;
    private readonly string? _tesseractBinaryPath;
    private readonly TesseractEngine? _nativeEngine;
    private readonly bool _useCli;
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

        // Find tesseract binary on system (check common macOS and Windows paths)
        string[] candidateBinaries =
        [
            "/opt/homebrew/bin/tesseract",
            "/usr/local/bin/tesseract",
            @"C:\Program Files\Tesseract-OCR\tesseract.exe",
            @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe",
            "tesseract.exe",
            "tesseract"
        ];

        foreach (var candidate in candidateBinaries)
        {
            if (File.Exists(candidate) || candidate == "tesseract")
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

        return _useCli ? ExtractUsingCli(bitmap) : ExtractUsingNative(bitmap);
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

        try
        {
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
            psi.ArgumentList.Add("tsv");

            // Only override TESSDATA_PREFIX if full configs directory exists
            if (!string.IsNullOrEmpty(_tessDataPath) && Directory.Exists(_tessDataPath) &&
                (Directory.Exists(Path.Combine(_tessDataPath, "tessconfigs")) || Directory.Exists(Path.Combine(_tessDataPath, "configs"))))
            {
                psi.EnvironmentVariables["TESSDATA_PREFIX"] = _tessDataPath;
            }

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Stream PNG encoded bytes directly to tesseract stdin in memory
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            {
                data.SaveTo(process.StandardInput.BaseStream);
                process.StandardInput.BaseStream.Flush();
                process.StandardInput.BaseStream.Close();
            }

            string tsvOutput = process.StandardOutput.ReadToEnd();
            string errOutput = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrEmpty(tsvOutput))
            {
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
                            int.TryParse(parts[6].Trim(), out int x) &&
                            int.TryParse(parts[7].Trim(), out int y) &&
                            int.TryParse(parts[8].Trim(), out int w) &&
                            int.TryParse(parts[9].Trim(), out int h))
                        {
                            float.TryParse(parts[10].Trim(), System.Globalization.CultureInfo.InvariantCulture, out float conf);
                            results.Add(new OcrResult
                            {
                                Text = wordText,
                                X = x,
                                Y = y,
                                Width = w,
                                Height = h,
                                Confidence = conf
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OcrEngine] CLI execution error: {ex.Message}");
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
                iter.Begin();
                do
                {
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
                                Confidence = conf
                            });
                        }
                    }
                } while (iter.Next(PageIteratorLevel.Word));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OcrEngine] Native OCR error: {ex.Message}");
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
