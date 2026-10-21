using System.Runtime.InteropServices;
using ScreenGuard.App.Abstractions;
using ScreenGuard.App.Models;

namespace ScreenGuard.App.Platforms.Windows;

/// <summary>
/// Production-grade Windows global input monitor using SetWindowsHookEx (WH_MOUSE_LL).
/// Runs on a dedicated Win32 message loop thread without blocking the application.
/// </summary>
public class WindowsGlobalInputMonitor : IGlobalInputMonitor
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const uint MOUSEEVENTF_FROMTOUCH = 0xFF515700;

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

    private readonly IScreenCaptureService _captureService;
    private LowLevelMouseProc? _hookProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private Thread? _messageLoopThread;
    private uint _messageLoopThreadId;
    private bool _isStarted;

    public event Action<CapturedFrame>? OnFrameCaptured;

    public WindowsGlobalInputMonitor(IScreenCaptureService captureService)
    {
        _captureService = captureService;
    }

    public bool Start()
    {
        if (_isStarted) return true;

        var initSignal = new ManualResetEventSlim(false);

        _messageLoopThread = new Thread(() =>
        {
            _messageLoopThreadId = GetCurrentThreadId();
            _hookProc = HookCallback;
            _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, IntPtr.Zero, 0);

            initSignal.Set();

            if (_hookHandle != IntPtr.Zero)
            {
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
        })
        {
            IsBackground = true,
            Name = "WindowsMouseHookMessagePump"
        };

        _messageLoopThread.Start();
        initSignal.Wait(2000);

        _isStarted = _hookHandle != IntPtr.Zero;
        return _isStarted;
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
            const uint WM_QUIT = 0x0012;
            PostThreadMessage(_messageLoopThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        _isStarted = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_LBUTTONDOWN || wParam == (IntPtr)WM_RBUTTONDOWN))
        {
            try
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                // Distinguish Touchpad vs Physical Mouse on Windows
                long extraInfo = hookStruct.dwExtraInfo.ToInt64();
                bool isTouchOrPen = (extraInfo & 0xFFFFFF00) == MOUSEEVENTF_FROMTOUCH;
                string inputSource = isTouchOrPen ? "Trackpad" : "External Mouse";

                // Capture screen immediately
                var frame = _captureService.CaptureScreen();
                if (frame != null)
                {
                    frame.InputSource = inputSource;
                    frame.ClickX = hookStruct.pt.x;
                    frame.ClickY = hookStruct.pt.y;
                    OnFrameCaptured?.Invoke(frame);
                }
            }
            catch
            {
                // Never crash the low-level hook
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
