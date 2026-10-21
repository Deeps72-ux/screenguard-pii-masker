/**
 * Program.cs
 * Production-grade entry point for the ScreenGuard Screen Capture & PII Masker application.
 *
 * Architecture:
 *   Native C++ Layer (libscreencapture.dylib):
 *     - CGEventTap for global mouse click event monitoring
 *     - Event Subtype + IOKit HID for External Mouse vs Trackpad classification
 *     - ScreenCaptureKit for high-performance screen capture
 *     - Metal API for GPU detection (Integrated iGPU vs Discrete dGPU)
 *     - Dispatches captured pixel buffer to C# via P/Invoke callback (from a capture
 *       worker thread, so the event tap never blocks)
 *
 *   C# Managed Layer (.NET 8):
 *     - P/Invoke interop with 64-bit alignment matched structures
 *     - ImageConverter: Raw BGRA buffer → SkiaSharp SKBitmap
 *     - OcrEngine: Dual-backend OCR (Tesseract CLI with Apple Silicon NEON & native)
 *     - PiiDetector: Multi-pattern detection (SSN, Email, Phone, CreditCard with Luhn, IP, DOB)
 *     - PiiMasker: Non-destructive visual masking with red outline and badges
 *     - ImageComparer: Side-by-side visual comparison composite and JSON audit reports
 *     - DiskWriter: Organizes original, masked, comparison, and report artifacts
 *
 *   Captured frames are queued and processed on a dedicated thread. If OCR is unavailable
 *   or fails for a frame, the whole frame is redacted (fail closed) rather than saved as-is.
 */

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SkiaSharp;
using ScreenGuard.App.Abstractions;
using ScreenGuard.App.Models;
using ScreenGuard.App.Platforms;
using ScreenGuard.App.Services;
using ScreenGuard.App.Services.Vision;

namespace ScreenGuard.App;

class Program
{
    // ── Services (Abstracted for SOLID & Cross-Platform Support) ─────────
    private static readonly ImageConverter _imageConverter = new();
    private static IPiiDetector _piiDetector = new PiiDetector();
    private static INameRecognizer? _nameRecognizer;
    private static IReadOnlyList<IVisualPiiDetector> _visualDetectors = [];
    private static CaptureProcessor _processor = null!;
    private static readonly IPiiMasker _piiMasker = new PiiMasker();
    private static readonly IImageComparer _imageComparer = new ImageComparer();
    private static IOcrEngine? _ocrEngine;
    private static IDiskWriter _diskWriter = null!;
    private static IScreenCaptureService _captureService = null!;
    private static IGlobalInputMonitor _inputMonitor = null!;
    private static IGpuDetector _gpuDetector = null!;

    // ── State ───────────────────────────────────────────────────────────
    private const int MaxQueuedFrames = 8;

    // OCR is the bottleneck (~2s per Retina frame); process a few frames concurrently
    private static readonly int ProcessingWorkers = Math.Clamp(Environment.ProcessorCount / 2, 1, 3);

    private static int _captureCount = 0;
    private static int _droppedFrames = 0;
    private static bool _saveOriginals = true;
    private static bool _maskFaces = true;
    private static readonly List<CaptureResult> _captureHistory = new();
    private static readonly ManualResetEventSlim _exitEvent = new(false);
    private static readonly BlockingCollection<CapturedFrame> _frameQueue = new(MaxQueuedFrames);

    static int Main(string[] args)
    {
        PlatformFactory.InitializeProcess();
        PrintBanner();

        // ── Parse Arguments ─────────────────────────────────────────────
        string outputDir = DefaultOutputDirectory();
        bool testMode = false;
        bool benchmarkMode = false;
        string? scanFile = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].ToLowerInvariant();
            if (arg is "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }
            if (arg is "--test" or "--once")
            {
                testMode = true;
            }
            else if (arg is "--benchmark" or "--sample")
            {
                benchmarkMode = true;
            }
            else if (arg is "--no-original")
            {
                _saveOriginals = false;
            }
            else if (arg is "--no-faces")
            {
                _maskFaces = false;
            }
            else if (arg is "--scan" or "-s")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                    return ArgumentError($"{args[i]} requires an image path.");
                scanFile = args[++i];
            }
            else if (arg is "--output" or "-o")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                    return ArgumentError($"{args[i]} requires a directory path.");
                outputDir = args[++i];
            }
            else if (!arg.StartsWith('-'))
            {
                outputDir = args[i];
            }
            else
            {
                return ArgumentError($"Unknown option: {args[i]}");
            }
        }

        try
        {
            _diskWriter = new DiskWriter(outputDir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[✗] Cannot write to output directory '{outputDir}': {ex.Message}");
            Console.WriteLine("    Choose a writable location with --output <dir>.");
            Console.ResetColor();
            return 1;
        }
        Console.WriteLine();

        // ── Initialize OCR and name recognition ─────────────────────────
        InitializeOcr();
        InitializeNameRecognizer();
        InitializeVisualDetectors();
        _processor = new CaptureProcessor(
            _ocrEngine, _piiDetector, _visualDetectors, _piiMasker, _imageComparer, _diskWriter,
            new CaptureProcessorOptions(_saveOriginals, _nameRecognizer != null ? "ner" : "labels-only"));
        Console.WriteLine();

        // ── Mode Execution ──────────────────────────────────────────────
        if (!string.IsNullOrEmpty(scanFile))
        {
            return RunScanFile(scanFile) ? 0 : 1;
        }

        if (benchmarkMode)
        {
            RunSyntheticBenchmark();
            return 0;
        }

        // ── Initialize Cross-Platform Services ──────────────────────────
        _captureService = PlatformFactory.CreateCaptureService();
        _inputMonitor = PlatformFactory.CreateInputMonitor(_captureService);
        _gpuDetector = PlatformFactory.CreateGpuDetector();

        // Capture threads only enqueue; OCR and disk I/O run on the processing thread
        _inputMonitor.OnFrameCaptured += EnqueueFrame;

        // ── Print GPU Information ───────────────────────────────────────
        PrintGpuInfo();
        Console.WriteLine();

        if (testMode)
        {
            bool captured = RunSingleCaptureTest();
            DisposeServices();
            PrintSessionSummary();
            return captured ? 0 : 1;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[*] Initializing {PlatformFactory.PlatformName} capture layer...");
        Console.ResetColor();

        if (!_inputMonitor.Start())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[✗] Failed to initialize input monitoring on {PlatformFactory.PlatformName}.");
            if (PlatformFactory.IsMacOS)
            {
                Console.WriteLine("    Grant both permissions to your terminal (or ScreenGuard.app), then quit and reopen it:");
                Console.WriteLine("    • Input Monitoring: System Settings → Privacy & Security → Input Monitoring");
                Console.WriteLine("    • Screen Recording: System Settings → Privacy & Security → Screen Recording");
            }
            Console.ResetColor();
            return 1;
        }

        var processingThreads = Enumerable.Range(0, ProcessingWorkers)
            .Select(i => new Thread(ProcessQueuedFrames) { IsBackground = true, Name = $"CaptureProcessing{i}" })
            .ToList();
        processingThreads.ForEach(t => t.Start());

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

        // Stop on Ctrl+C (above), on a termination signal (e.g. the Terminal window closing),
        // or on ENTER when a keyboard is attached. Without a keyboard (stdin redirected or
        // closed) end-of-input must not count as ENTER, or the monitor would stop at once.
        using var stopSignals = RegisterStopSignals();
        if (!Console.IsInputRedirected)
        {
            var inputThread = new Thread(() =>
            {
                try
                {
                    if (Console.ReadLine() != null)
                        _exitEvent.Set();
                }
                catch { }
            })
            { IsBackground = true };
            inputThread.Start();
        }

        _exitEvent.Wait();

        // ── Shutdown ────────────────────────────────────────────────────
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n[*] Shutting down input monitoring...");
        Console.ResetColor();

        _inputMonitor.Stop();
        _inputMonitor.Dispose();

        // Finish frames that were already captured before exiting
        _frameQueue.CompleteAdding();
        if (_frameQueue.Count > 0)
        {
            Console.WriteLine($"[*] Finishing {_frameQueue.Count} queued capture(s)...");
        }
        processingThreads.ForEach(t => t.Join());
        DisposeServices();

        PrintSessionSummary();
        return 0;
    }

    /// <summary>
    /// Default output location. From an installed macOS app (or when the working directory is
    /// not writable, e.g. "/" when launched from Finder) results go to ~/ScreenGuard/output;
    /// otherwise to ./output next to where the command was run.
    /// </summary>
    private static string DefaultOutputDirectory()
    {
        string cwd = Directory.GetCurrentDirectory();
        bool runningFromAppBundle = AppContext.BaseDirectory.Contains(".app/Contents/MacOS", StringComparison.Ordinal);

        if (!runningFromAppBundle && IsWritableDirectory(cwd))
            return Path.Combine(cwd, "output");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "ScreenGuard", "output");
    }

    private static bool IsWritableDirectory(string path)
    {
        try
        {
            string probe = Path.Combine(path, $".screenguard_write_test_{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Treats SIGTERM / SIGHUP (closing the Terminal window, kill) like Ctrl+C so queued
    /// captures are finished and the summary is printed.
    /// </summary>
    private static IDisposable RegisterStopSignals()
    {
        var registrations = new List<IDisposable>();
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP })
        {
            try
            {
                registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    _exitEvent.Set();
                }));
            }
            catch (PlatformNotSupportedException)
            {
                // SIGHUP does not exist on Windows
            }
        }
        return new CompositeDisposable(registrations);
    }

    private sealed class CompositeDisposable(List<IDisposable> items) : IDisposable
    {
        public void Dispose() => items.ForEach(i => i.Dispose());
    }

    private static int ArgumentError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[✗] {message}");
        Console.ResetColor();
        Console.WriteLine();
        PrintHelp();
        return 2;
    }

    /// <summary>
    /// Called on the platform capture thread. Never blocks: when the processing queue is
    /// full the frame is dropped and counted.
    /// </summary>
    private static void EnqueueFrame(CapturedFrame frame)
    {
        bool queued;
        try
        {
            queued = _frameQueue.TryAdd(frame);
        }
        catch (InvalidOperationException)
        {
            queued = false; // shutting down
        }

        if (!queued)
        {
            frame.Dispose();
            Interlocked.Increment(ref _droppedFrames);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[!] Processing queue full; capture dropped.");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Processing thread: runs OCR, masking and disk writes for each queued frame.
    /// </summary>
    private static void ProcessQueuedFrames()
    {
        foreach (var frame in _frameQueue.GetConsumingEnumerable())
        {
            using (frame)
            {
                var log = new CaptureLog();
                try
                {
                    int count = Interlocked.Increment(ref _captureCount);
                    log.Color(ConsoleColor.Yellow);
                    log.Line();
                    log.Line($"┌─ Capture #{count} ──────────────────────────────────────────");
                    log.Line($"│  Platform:        {PlatformFactory.PlatformName}");
                    log.Line($"│  Click Position:  ({frame.ClickX:F0}, {frame.ClickY:F0}) px");
                    log.Line($"│  Input Device:    {frame.InputSource}");
                    log.Line($"│  Resolution:      {frame.Width}x{frame.Height}");
                    log.Reset();

                    ProcessCapture(frame.Bitmap, frame.InputSource, frame.ClickX, frame.ClickY, frame.TimestampMs, log);
                }
                catch (Exception ex)
                {
                    log.Color(ConsoleColor.Red);
                    log.Line($"│  [✗] Failed to process capture: {ex.Message}");
                    log.Line("└───────────────────────────────────────────────────────");
                    log.Reset();
                }
                finally
                {
                    log.Flush();
                }
            }
        }
    }

    /// <summary>
    /// Runs one frame through the capture pipeline and records it in the session history.
    /// </summary>
    private static void ProcessCapture(
        SKBitmap bitmap,
        string inputSource,
        double clickX,
        double clickY,
        long timestampMs,
        CaptureLog log)
    {
        var result = _processor.Process(bitmap, inputSource, clickX, clickY, timestampMs, log);
        lock (_captureHistory)
        {
            _captureHistory.Add(result);
        }
    }

    /// <summary>
    /// Processes a static image file directly through the OCR and PII redaction pipeline.
    /// </summary>
    private static bool RunScanFile(string path)
    {
        if (!File.Exists(path))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[✗] Image file not found: {path}");
            Console.ResetColor();
            return false;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[*] Processing image through OCR and PII redaction engine: {Path.GetFileName(path)}");
        Console.ResetColor();

        using var bitmap = SKBitmap.Decode(path);
        if (bitmap == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[✗] Failed to decode image: {path}");
            Console.ResetColor();
            return false;
        }

        var log = new CaptureLog();
        ProcessCapture(bitmap, "ManualScan", 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), log);
        log.Flush();
        return true;
    }

    /// <summary>
    /// Runs a single screen capture test using the cross-platform capture service.
    /// </summary>
    private static bool RunSingleCaptureTest()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n[*] Executing single test screen capture via {_captureService.GetType().Name}...");
        Console.ResetColor();

        using var frame = _captureService.CaptureScreen();
        if (frame == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[✗] Screen capture returned null.");
            Console.ResetColor();
            return false;
        }

        var log = new CaptureLog();
        log.Line($"│  Resolution:      {frame.Width}x{frame.Height}");
        ProcessCapture(frame.Bitmap, frame.InputSource, frame.ClickX, frame.ClickY, frame.TimestampMs, log);
        log.Flush();
        return true;
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
            canvas.DrawText("Reviewed by Sarah Johnson on Monday.", 50, 530, textPaint);

            canvas.Flush();
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[✓] Synthetic test image created. Executing detection, masking & comparison pipeline...");
        Console.ResetColor();

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var log = new CaptureLog();
        ProcessCapture(bitmap, "Synthetic Benchmark", 600, 350, nowMs, log);
        log.Flush();
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
            Path.Combine(AppContext.BaseDirectory, "..", "Resources", "tessdata"),
            Path.Combine(Directory.GetCurrentDirectory(), "tessdata"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "tessdata"),
            "/opt/homebrew/share/tessdata",
            "/usr/local/share/tessdata"
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
    /// Loads the on-device NER model used to mask person names that have no label or title.
    /// Without it, only labelled ("Customer Name:") and titled ("Dr.") names are masked.
    /// </summary>
    private static void InitializeNameRecognizer()
    {
        string? modelDir = ModelLocator.Find("ner", "model.onnx", "vocab.txt");

        if (modelDir == null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[!] Name recognition model not found (run scripts/download_models.sh).");
            Console.WriteLine("    Only labelled or titled names will be masked.");
            Console.ResetColor();
            return;
        }

        try
        {
            _nameRecognizer = new OnnxNameRecognizer(modelDir);
            _piiDetector = new PiiDetector(_nameRecognizer);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[✓] Name recognition initialized: {_nameRecognizer.Description}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[!] Name recognition unavailable: {ex.Message}");
            Console.WriteLine("    Only labelled or titled names will be masked.");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Loads the pixel-based detectors (faces) that are enabled and whose models are present.
    /// </summary>
    private static void InitializeVisualDetectors()
    {
        _visualDetectors = VisualDetectorFactory.CreateAll(
            new VisualDetectionSettings(Faces: _maskFaces),
            info: message =>
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[✓] {message}");
                Console.ResetColor();
            },
            warning: message =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[!] {message}");
                Console.ResetColor();
            });
    }

    private static void DisposeServices()
    {
        _ocrEngine?.Dispose();
        _nameRecognizer?.Dispose();
        foreach (var detector in _visualDetectors)
            detector.Dispose();
    }

    /// <summary>
    /// Displays GPU information retrieved from the cross-platform GPU detector.
    /// </summary>
    private static void PrintGpuInfo()
    {
        var gpus = _gpuDetector.DetectGpus();
        if (gpus.Count == 0) return;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
        Console.WriteLine($"║  GPU Information ({PlatformFactory.PlatformName,-43}) ║");
        Console.WriteLine("╠═══════════════════════════════════════════════════════════════╣");

        for (int i = 0; i < gpus.Count; i++)
        {
            var gpu = gpus[i];
            Console.WriteLine($"║  GPU {i}: {gpu.Name,-36} [{gpu.Type,-16}] ║");
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
        int otherCount = history.Count(c => c.InputSource is not ("External Mouse" or "Trackpad"));
        int totalPii = history.Sum(c => c.PiiCount);

        Console.WriteLine($"║  • External Mouse Clicks:   {mouseCount,6}                            ║");
        Console.WriteLine($"║  • Trackpad Clicks:         {trackpadCount,6}                            ║");
        Console.WriteLine($"║  • Other / Unknown:         {otherCount,6}                            ║");
        Console.WriteLine($"║  • Total PII Entities:      {totalPii,6}                            ║");
        if (_droppedFrames > 0)
            Console.WriteLine($"║  • Dropped (queue full):    {_droppedFrames,6}                            ║");
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
        Console.WriteLine("Usage: ScreenGuard [options] [output_dir]");
        Console.WriteLine();
        Console.WriteLine("With no mode option, monitors mouse/trackpad clicks and captures the screen on each click.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --test, --once         Run a single live screen capture, process and exit.");
        Console.WriteLine("  --benchmark, --sample  Run synthetic PII benchmark (validates OCR, detection & masking).");
        Console.WriteLine("  --scan <image>, -s     Run OCR + PII masking on an existing image file.");
        Console.WriteLine("  --output <dir>, -o     Directory for output images and reports");
        Console.WriteLine("                         (default: ./output, or ~/ScreenGuard/output for the installed app).");
        Console.WriteLine("  --no-original          Do not save the unmasked original screenshot.");
        Console.WriteLine("  --no-faces             Do not mask faces (photos, avatars, video calls).");
        Console.WriteLine("  --help, -h             Show this help message.");
        Console.WriteLine();
    }
}
