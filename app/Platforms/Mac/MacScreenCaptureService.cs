using System.Runtime.InteropServices;
using SkiaSharp;
using ScreenGuard.App.Abstractions;
using ScreenGuard.App.Interop;
using ScreenGuard.App.Models;
using ScreenGuard.App.Services;

namespace ScreenGuard.App.Platforms.Mac;

/// <summary>
/// macOS screen capture service using native ScreenCaptureKit via libscreencapture.dylib.
/// </summary>
public class MacScreenCaptureService : IScreenCaptureService
{
    private readonly ImageConverter _imageConverter = new();

    public CapturedFrame? CaptureScreen()
    {
        IntPtr capturePtr = NativeInterop.ScreenGuardCaptureNow();
        if (capturePtr == IntPtr.Zero)
            return null;

        try
        {
            var capture = Marshal.PtrToStructure<NativeInterop.ScreenGuardCaptureStruct>(capturePtr);
            var bitmap = _imageConverter.FromRawBgra(capture.PixelData, capture.Width, capture.Height, capture.Stride);
            if (bitmap == null)
                return null;

            string inputSource = capture.InputSource switch
            {
                1 => "External Mouse",
                2 => "Trackpad",
                _ => "Unknown"
            };

            return new CapturedFrame(bitmap, inputSource, capture.ClickX, capture.ClickY, capture.TimestampMs);
        }
        finally
        {
            NativeInterop.ScreenGuardFreeCapture(capturePtr);
        }
    }
}
