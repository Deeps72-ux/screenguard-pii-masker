using System.Runtime.InteropServices;
using ScreenGuard.App.Abstractions;
using ScreenGuard.App.Interop;
using ScreenGuard.App.Models;
using ScreenGuard.App.Services;

namespace ScreenGuard.App.Platforms.Mac;

/// <summary>
/// macOS global input monitor using Quartz EventTap (CGEventTap) and IOKit via libscreencapture.dylib.
/// </summary>
public class MacGlobalInputMonitor : IGlobalInputMonitor
{
    private readonly ImageConverter _imageConverter = new();
    private NativeInterop.ScreenGuardCaptureCallback? _nativeCallback;
    private GCHandle _callbackHandle;
    private bool _isStarted;

    public event Action<CapturedFrame>? OnFrameCaptured;

    public bool Start()
    {
        if (_isStarted) return true;

        _nativeCallback = HandleNativeCapture;
        _callbackHandle = GCHandle.Alloc(_nativeCallback);

        int result = NativeInterop.ScreenGuardInit(_nativeCallback);
        if (result != 0)
        {
            _callbackHandle.Free();
            _nativeCallback = null;
            return false;
        }

        _isStarted = true;
        return true;
    }

    public void Stop()
    {
        if (!_isStarted) return;

        NativeInterop.ScreenGuardShutdown();
        if (_callbackHandle.IsAllocated)
        {
            _callbackHandle.Free();
        }
        _nativeCallback = null;
        _isStarted = false;
    }

    private void HandleNativeCapture(IntPtr capturePtr)
    {
        if (capturePtr == IntPtr.Zero) return;

        try
        {
            var capture = Marshal.PtrToStructure<NativeInterop.ScreenGuardCaptureStruct>(capturePtr);
            var bitmap = _imageConverter.FromRawBgra(capture.PixelData, capture.Width, capture.Height, capture.Stride);
            if (bitmap == null) return;

            string inputSource = capture.InputSource switch
            {
                1 => "External Mouse",
                2 => "Trackpad",
                _ => "Unknown"
            };

            var frame = new CapturedFrame(bitmap, inputSource, capture.ClickX, capture.ClickY, capture.TimestampMs);
            OnFrameCaptured?.Invoke(frame);
        }
        finally
        {
            NativeInterop.ScreenGuardFreeCapture(capturePtr);
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
