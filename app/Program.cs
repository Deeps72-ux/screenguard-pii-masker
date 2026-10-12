/**
 * Program.cs
 * Production-grade entry point for the Skan-AI Screen Capture & PII Masker application.
 *
 * Architecture:
 *   Native C++ Layer (libscreencapture.dylib):
 *     - CGEventTap for global mouse click event monitoring
 *     - Event Subtype + IOKit HID for External Mouse vs Trackpad classification
 *     - ScreenCaptureKit for high-performance screen capture
 *     - Metal API for GPU detection (Integrated iGPU vs Discrete dGPU)
 *     - Dispatches captured pixel buffer to C# via P/Invoke callback
 *
 *   C# Managed Layer (.NET 8):
 *     - P/Invoke interop with 64-bit alignment matched structures
 *     - ImageConverter: Raw BGRA buffer → SkiaSharp SKBitmap
 *     - OcrEngine: Dual-backend OCR (Tesseract CLI with Apple Silicon NEON & native)
 *     - PiiDetector: Multi-pattern detection (SSN, Email, Phone, CreditCard with Luhn, IP, DOB)
 *     - PiiMasker: Non-destructive visual masking with red outline and badges
 *     - ImageComparer: Side-by-side visual comparison composite and JSON audit reports
 *     - DiskWriter: Organizes original, masked, comparison, and report artifacts
 */

using System.Runtime.InteropServices;
using SkiaSharp;
using SkanCapture.App.Interop;
using SkanCapture.App.Models;
using SkanCapture.App.Services;

namespace SkanCapture.App;

class Program
{
    // ── Services ────────────────────────────────────────────────────────
    private static readonly ImageConverter _imageConverter = new();
    private static readonly PiiDetector _piiDetector = new();
    private static readonly PiiMasker _piiMasker = new();
    private static readonly ImageComparer _imageComparer = new();
    private static OcrEngine? _ocrEngine;
    private static DiskWriter _diskWriter = null!;

    // ── State ───────────────────────────────────────────────────────────
    private static int _captureCount = 0;
    private static readonly List<CaptureResult> _captureHistory = new();
    private static readonly ManualResetEventSlim _exitEvent = new(false);

    static void Main(string[] args)
    {
        PrintBanner();

        // ── Parse Arguments ─────────────────────────────────────────────
        string outputDir = Path.Combine(Directory.GetCurrentDirectory(), "output");
        bool testMode = false;
        bool benchmarkMode = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].ToLowerInvariant();
            if (arg is "--help" or "-h")
            {
                PrintHelp();
                return;
            }
            if (arg is "--test" or "--once")
            {
                testMode = true;
            }
            else if (arg is "--benchmark" or "--sample")
            {
                benchmarkMode = true;
            }
            else if (arg is "--output" or "-o" && i + 1 < args.Length)
            {
                outputDir = args[++i];
            }
            else if (!arg.StartsWith('-'))
            {
                outputDir = args[i];
            }
        }

        _diskWriter = new DiskWriter(outputDir);
        Console.WriteLine();

        // ── Initialize OCR ──────────────────────────────────────────────
        InitializeOcr();
        Console.WriteLine();

        // ── Mode Execution ──────────────────────────────────────────────
        if (benchmarkMode)
        {
            RunSyntheticBenchmark();
            return;
        }

        // ── Initialize Native C++ Layer ─────────────────────────────────
        NativeInterop.SkanCaptureCallback callback = OnCapture;
        GCHandle callbackHandle = GCHandle.Alloc(callback);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("[*] Initializing native C++ layer (libscreencapture.dylib)...");
        Console.ResetColor();

        int result = NativeInterop.SkanInit(callback);
        if (result != 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[✗] Failed to initialize native layer (error code: {result}).");
            Console.WriteLine("    Possible causes:");
            Console.WriteLine("    • Input Monitoring permission missing (System Settings → Privacy & Security → Input Monitoring)");
            Console.WriteLine("    • Screen Recording permission missing (System Settings → Privacy & Security → Screen Recording)");
            Console.ResetColor();
            callbackHandle.Free();
            return;
        }

        // ── Print GPU Information ───────────────────────────────────────
        PrintGpuInfo();
        Console.WriteLine();

        if (testMode)
        {
            RunSingleCaptureTest();
            NativeInterop.SkanShutdown();
            callbackHandle.Free();
            _ocrEngine?.Dispose();
            PrintSessionSummary();
            return;
        }

        // ── Setup Console Cancellation (Ctrl+C) ─────────────────────────
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            _exitEvent.Set();
        };

        // ── Interactive Monitoring Loop ─────────────────────────────────
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  ✓ ScreenGuard Live Monitor Active                            ║");
        Console.WriteLine("║    Click anywhere on screen to capture, analyze & mask PII.   ║");
        Console.WriteLine("║    Supports both External Mouse & Built-in Trackpad clicks.   ║");
        Console.WriteLine("║                                                               ║");
        Console.WriteLine("║    Press ENTER or Ctrl+C to stop and display summary.         ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();

        // Wait for ENTER or Ctrl+C in a non-blocking thread wait
        var inputThread = new Thread(() =>
        {
            try { Console.ReadLine(); } catch { }
            _exitEvent.Set();
        })
        { IsBackground = true };
        inputThread.Start();

        _exitEvent.Wait();

        // ── Shutdown ────────────────────────────────────────────────────
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n[*] Shutting down native monitoring...");
        Console.ResetColor();

        NativeInterop.SkanShutdown();
        callbackHandle.Free();
        _ocrEngine?.Dispose();

        PrintSessionSummary();
    }

    /// <summary>
    /// Callback invoked by the native C++ layer on each mouse click.
    /// </summary>
    private static void OnCapture(IntPtr capturePtr)
    {
        try
        {
            var capture = Marshal.PtrToStructure<NativeInterop.SkanCaptureStruct>(capturePtr);
            int count = Interlocked.Increment(ref _captureCount);

            string inputSource = capture.InputSource switch
            {
                1 => "External Mouse",
                2 => "Trackpad",
                _ => "Unknown"
            };

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine();
            Console.WriteLine($"┌─ Capture #{count} ──────────────────────────────────────────");
            Console.WriteLine($"│  Click Position:  ({capture.ClickX:F0}, {capture.ClickY:F0})");
            Console.WriteLine($"│  Input Device:    {inputSource}");
            Console.WriteLine($"│  Resolution:      {capture.Width}x{capture.Height} (Stride: {capture.Stride})");
            Console.ResetColor();

            // Convert raw buffer to SKBitmap
            using var bitmap = _imageConverter.FromRawBgra(
                capture.PixelData, capture.Width, capture.Height, capture.Stride);

            if (bitmap == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("│  [✗] Failed to convert raw pixel buffer to bitmap.");
                Console.WriteLine("└───────────────────────────────────────────────────────");
                Console.ResetColor();
                return;
            }

            ProcessCapture(bitmap, inputSource, capture.ClickX, capture.ClickY, capture.TimestampMs);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[✗] Error processing capture: {ex.Message}");
            Console.ResetColor();
        }
        finally
        {
            // CRITICAL: Always free native memory
            NativeInterop.SkanFreeCapture(capturePtr);
        }
    }

    /// <summary>
    /// Processes a captured bitmap through OCR, PII detection, masking, comparison, and disk saving.
    /// </summary>
    private static void ProcessCapture(
        SKBitmap bitmap,
        string inputSource,
        double clickX,
        double clickY,
        long timestampMs)
    {
        string timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs)
            .ToLocalTime().ToString("yyyyMMdd_HHmmss_fff");
        string captureId = $"capture_{timestamp}";

        // 1. Save original screenshot
        string originalPath = _diskWriter.SaveOriginal(bitmap, timestamp);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"│  [✓] Original saved:   {Path.GetFileName(originalPath)}");
        Console.ResetColor();

        // 2. OCR and PII Detection
        var piiMatches = new List<PiiMatch>();
        if (_ocrEngine != null)
        {
            try
            {
                var ocrResults = _ocrEngine.ExtractTextWithBounds(bitmap);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"│  [~] OCR extracted {ocrResults.Count} word tokens. Scanning for PII...");
                Console.ResetColor();

                piiMatches = _piiDetector.DetectPii(ocrResults);

                if (piiMatches.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine($"│  [!] Detected {piiMatches.Count} PII entity(s) for redaction:");
                    foreach (var match in piiMatches)
                    {
                        Console.WriteLine($"│      • {match}");
                    }
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("│  [i] OCR completed: 0 PII entities detected.");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"│  [!] OCR / PII detection notice: {ex.Message}");
                Console.ResetColor();
            }
        }

        // 3. Apply Visual Masking
        using var maskedBitmap = _piiMasker.ApplyMask(bitmap, piiMatches);
        string maskedPath = _diskWriter.SaveMasked(maskedBitmap, timestamp);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"│  [✓] Masked saved:     {Path.GetFileName(maskedPath)}");
        Console.ResetColor();

        // 4. Create Side-by-Side Comparison Composite
        using var comparisonBitmap = _imageComparer.CreateSideBySideComparison(
            bitmap, maskedBitmap, piiMatches, captureId, inputSource);
        string comparisonPath = _diskWriter.SaveComparison(comparisonBitmap, timestamp);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"│  [✓] Comparison saved: {Path.GetFileName(comparisonPath)}");
        Console.ResetColor();

        // 5. Generate Audit Report
        var resultModel = new CaptureResult
        {
            Width = bitmap.Width,
            Height = bitmap.Height,
            InputSource = inputSource,
            ClickX = clickX,
            ClickY = clickY,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs),
            OriginalImagePath = originalPath,
            MaskedImagePath = maskedPath,
            PiiCount = piiMatches.Count
        };

        string jsonReport = _imageComparer.GenerateJsonReport(resultModel, piiMatches, comparisonPath);
        string reportPath = _diskWriter.SaveReport(jsonReport, timestamp);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"│  [✓] Report saved:     {Path.GetFileName(reportPath)}");
        Console.ResetColor();

        lock (_captureHistory)
        {
            _captureHistory.Add(resultModel);
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("└───────────────────────────────────────────────────────");
        Console.ResetColor();
    }

    /// <summary>
    /// Runs a single screen capture test using SkanCaptureNow().
    /// </summary>
    private static void RunSingleCaptureTest()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[*] Executing single test screen capture via SkanCaptureNow()...");
        Console.ResetColor();

        IntPtr capturePtr = NativeInterop.SkanCaptureNow();
        if (capturePtr == IntPtr.Zero)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[✗] SkanCaptureNow() returned null.");
            Console.ResetColor();
            return;
        }

        try
        {
            var capture = Marshal.PtrToStructure<NativeInterop.SkanCaptureStruct>(capturePtr);
            string inputSource = capture.InputSource switch
            {
                1 => "External Mouse",
                2 => "Trackpad",
                _ => "Manual/Test"
            };

            using var bitmap = _imageConverter.FromRawBgra(
                capture.PixelData, capture.Width, capture.Height, capture.Stride);

            if (bitmap != null)
            {
                ProcessCapture(bitmap, inputSource, 0, 0, capture.TimestampMs);
            }
        }
        finally
        {
            NativeInterop.SkanFreeCapture(capturePtr);
        }
    }

    /// <summary>
    /// Generates a synthetic test bitmap with known PII items to validate the full pipeline.
    /// </summary>
    private static void RunSyntheticBenchmark()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n[*] Generating high-resolution synthetic test capture with PII tokens...");
        Console.ResetColor();

        int width = 1200;
        int height = 700;
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);

            using var titlePaint = new SKPaint
            {
                Color = new SKColor(20, 20, 20),
                TextSize = 28f,
                IsAntialias = true,
                Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
            };
            canvas.DrawText("CONFIDENTIAL CUSTOMER RECORD - INTERNAL USE ONLY", 50, 60, titlePaint);

            using var textPaint = new SKPaint
            {
                Color = new SKColor(40, 40, 40),
                TextSize = 20f,
                IsAntialias = true,
                Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
            };

            canvas.DrawText("Customer Name:    Alexander Hamilton", 50, 130, textPaint);
            canvas.DrawText("Social Security:  123-45-6789", 50, 180, textPaint);
            canvas.DrawText("Email Address:    alexander.hamilton@enterprise.io", 50, 230, textPaint);
            canvas.DrawText("Contact Phone:    (555) 234-5678", 50, 280, textPaint);
            canvas.DrawText("Credit Card:      4111 2222 3333 4444", 50, 330, textPaint);
            canvas.DrawText("Server IP:        192.168.1.105", 50, 380, textPaint);
            canvas.DrawText("Date of Birth:    01/11/1985", 50, 430, textPaint);
            canvas.DrawText("Passport ID:      A12345678", 50, 480, textPaint);

            canvas.Flush();
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[✓] Synthetic test image created. Executing detection, masking & comparison pipeline...");
        Console.ResetColor();

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ProcessCapture(bitmap, "Synthetic Benchmark", 600, 350, nowMs);
        PrintSessionSummary();
    }

    /// <summary>
    /// Initialize the OCR engine.
    /// </summary>
    private static void InitializeOcr()
    {
        string[] candidateDirs =
        [
            Path.Combine(AppContext.BaseDirectory, "tessdata"),
            Path.Combine(Directory.GetCurrentDirectory(), "tessdata"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "tessdata"),
        ];

        string? foundTessData = null;
        foreach (var dir in candidateDirs)
        {
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "eng.traineddata")))
            {
                foundTessData = dir;
                break;
            }
        }

        try
        {
            _ocrEngine = new OcrEngine(foundTessData);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[✓] OCR engine initialized: {_ocrEngine.ActiveBackend}");
            if (foundTessData != null)
            {
                Console.WriteLine($"    Tessdata location:   {foundTessData}");
            }
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[!] OCR initialization warning: {ex.Message}");
            Console.WriteLine("    PII detection will proceed without OCR.");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Displays GPU information retrieved from the native Metal API.
    /// </summary>
    private static void PrintGpuInfo()
    {
        int count = NativeInterop.SkanGetGpuCount();
        if (count <= 0) return;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  GPU Information (Metal API)                                  ║");
        Console.WriteLine("╠═══════════════════════════════════════════════════════════════╣");

        for (int i = 0; i < count; i++)
        {
            if (NativeInterop.SkanGetGpuInfo(i, out var info) == 0)
            {
                string typeStr = info.Type switch
                {
                    0 => "Integrated (iGPU)",
                    1 => "Discrete (dGPU)",
                    2 => "External (eGPU)",
                    _ => "Unknown"
                };

                Console.WriteLine($"║  GPU {i}: {info.Name,-36} [{typeStr,-16}] ║");
            }
        }

        Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }

    /// <summary>
    /// Prints session summary statistics.
    /// </summary>
    private static void PrintSessionSummary()
    {
        List<CaptureResult> history;
        lock (_captureHistory)
        {
            history = new List<CaptureResult>(_captureHistory);
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                     SESSION SUMMARY                           ║");
        Console.WriteLine("╠═══════════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║  Total Captures Processed:  {history.Count,6}                            ║");

        int mouseCount = history.Count(c => c.InputSource == "External Mouse");
        int trackpadCount = history.Count(c => c.InputSource == "Trackpad");
        int totalPii = history.Sum(c => c.PiiCount);

        Console.WriteLine($"║  • External Mouse Clicks:   {mouseCount,6}                            ║");
        Console.WriteLine($"║  • Trackpad Clicks:         {trackpadCount,6}                            ║");
        Console.WriteLine($"║  • Total PII Entities:      {totalPii,6}                            ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine();
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                                                               ║");
        Console.WriteLine("║        🖥️  ScreenGuard: Screen Capture & PII Masker  🔒        ║");
        Console.WriteLine("║                                                               ║");
        Console.WriteLine("║       Native C++ Layer        ←→      C# Application Layer    ║");
        Console.WriteLine("║   (CGEventTap + Metal GPU)         (OCR + PII Mask + Compare) ║");
        Console.WriteLine("║                                                               ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: dotnet run [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --test, --once         Run a single live screen capture, process and exit.");
        Console.WriteLine("  --benchmark, --sample  Run synthetic PII benchmark (validates OCR, detection & masking).");
        Console.WriteLine("  --output <dir>, -o     Specify custom directory for output images and reports.");
        Console.WriteLine("  --help, -h             Show this help message.");
        Console.WriteLine();
    }
}
