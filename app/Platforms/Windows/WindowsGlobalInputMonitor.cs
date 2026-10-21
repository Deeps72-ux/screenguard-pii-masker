using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ScreenGuard.App.Abstractions;
using ScreenGuard.App.Models;

namespace ScreenGuard.App.Platforms.Windows;

/// <summary>
/// Production-grade Windows global input monitor using SetWindowsHookEx (WH_MOUSE_LL).
///
/// The hook callback only records the click and returns: Windows silently removes a
/// low-level hook whose callback exceeds LowLevelHooksTimeout (≤ 1s), so capture and
/// classification run on a separate worker thread. Trackpad vs mouse classification uses
/// <see cref="WindowsRawInputClassifier"/> on the same message-loop thread as the hook.
/// </summary>
public class WindowsGlobalInputMonitor : IGlobalInputMonitor
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const uint WM_QUIT = 0x0012;
    private const long MOUSEEVENTF_FROMTOUCH = 0xFF515700;
    private const long TOUCH_SIGNATURE_MASK = 0xFFFFFF00;
    private const int MaxPendingClicks = 16;
    private const int ClassificationDelayMs = 40;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    private record PendingClick(int X, int Y, long Tick, bool IsTouchOrPen);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly IScreenCaptureService _captureService;
    private readonly WindowsRawInputClassifier _classifier = new();
    private BlockingCollection<PendingClick>? _pending;
    private LowLevelMouseProc? _hookProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private Thread? _messageLoopThread;
    private Thread? _captureThread;
    private uint _messageLoopThreadId;
    private bool _isStarted;
    private long _droppedClicks;

    public event Action<CapturedFrame>? OnFrameCaptured;

    public WindowsGlobalInputMonitor(IScreenCaptureService captureService)
    {
        _captureService = captureService;
    }

    public bool Start()
    {
        if (_isStarted) return true;

        _pending = new BlockingCollection<PendingClick>(MaxPendingClicks);
        var initSignal = new ManualResetEventSlim(false);

        _messageLoopThread = new Thread(() =>
        {
            _messageLoopThreadId = GetCurrentThreadId();

            if (!_classifier.Start())
                Console.Error.WriteLine("[ScreenGuard] Raw Input unavailable; input device will be reported as Unknown.");

            _hookProc = HookCallback;
            _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);

            initSignal.Set();

            if (_hookHandle != IntPtr.Zero)
            {
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }

            _classifier.Dispose();
        })
        {
            IsBackground = true,
            Name = "WindowsMouseHookMessagePump"
        };

        _messageLoopThread.Start();
        initSignal.Wait(2000);

        if (_hookHandle == IntPtr.Zero)
            return false;

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "WindowsClickCaptureWorker"
        };
        _captureThread.Start();

        _isStarted = true;
        return true;
    }

    public void Stop()
    {
        if (!_isStarted) return;

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        if (_messageLoopThreadId != 0)
        {
            PostThreadMessage(_messageLoopThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        _pending?.CompleteAdding();
        _captureThread?.Join(5000);
        _messageLoopThread?.Join(2000);

        long dropped = Interlocked.Read(ref _droppedClicks);
        if (dropped > 0)
            Console.WriteLine($"[ScreenGuard] Clicks dropped due to capture backlog: {dropped}");

        _isStarted = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Keep this path minimal: record the click and return immediately
        if (nCode >= 0 && (wParam == (IntPtr)WM_LBUTTONDOWN || wParam == (IntPtr)WM_RBUTTONDOWN))
        {
            try
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                long extraInfo = hookStruct.dwExtraInfo.ToInt64();
                bool isTouchOrPen = (extraInfo & TOUCH_SIGNATURE_MASK) == MOUSEEVENTF_FROMTOUCH;

                var click = new PendingClick(hookStruct.pt.x, hookStruct.pt.y, Environment.TickCount64, isTouchOrPen);
                if (_pending == null || !_pending.TryAdd(click))
                    Interlocked.Increment(ref _droppedClicks);
            }
            catch
            {
                // Never crash the low-level hook
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void CaptureLoop()
    {
        if (_pending == null) return;

        foreach (var click in _pending.GetConsumingEnumerable())
        {
            try
            {
                // Give the matching WM_INPUT a moment to arrive before classifying
                long wait = click.Tick + ClassificationDelayMs - Environment.TickCount64;
                if (wait > 0) Thread.Sleep((int)wait);

                string inputSource = click.IsTouchOrPen ? "Touchscreen" : _classifier.Classify(click.Tick);

                var frame = _captureService.CaptureScreenAt(click.X, click.Y);
                if (frame == null)
                {
                    Console.Error.WriteLine($"[ScreenGuard] Screen capture failed for click at ({click.X}, {click.Y}).");
                    continue;
                }

                frame.InputSource = inputSource;
                OnFrameCaptured?.Invoke(frame);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ScreenGuard] Capture worker error: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _pending?.Dispose();
        GC.SuppressFinalize(this);
    }
}
