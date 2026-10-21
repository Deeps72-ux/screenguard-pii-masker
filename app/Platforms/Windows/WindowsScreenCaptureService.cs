using System.Runtime.InteropServices;
using SkiaSharp;
using ScreenGuard.App.Abstractions;
using ScreenGuard.App.Models;

namespace ScreenGuard.App.Platforms.Windows;

/// <summary>
/// Production-grade Windows screen capture service using Win32 GDI P/Invoke.
/// Captures the monitor containing a given point at physical pixel resolution
/// (the process is made per-monitor DPI aware at startup by <see cref="WindowsDpiAwareness"/>).
/// Operates in pure C# with zero external native C++ DLL dependencies on Windows.
/// </summary>
public class WindowsScreenCaptureService : IScreenCaptureService
{
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000; // include layered (transparent/translucent) windows
    private const int DIB_RGB_COLORS = 0;
    private const int BI_RGB = 0;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, IntPtr lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    /// <summary>Captures the monitor under the mouse cursor.</summary>
    public CapturedFrame? CaptureScreen()
    {
        GetCursorPos(out var cursorPos);
        return CaptureScreenAt(cursorPos.X, cursorPos.Y);
    }

    /// <summary>
    /// Captures the monitor containing the given virtual-screen point. The frame's
    /// click coordinates are relative to that monitor's captured image.
    /// </summary>
    public CapturedFrame? CaptureScreenAt(int x, int y)
    {
        var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return null;

        var bounds = info.rcMonitor;
        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) return null;

        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
        IntPtr hOldBitmap = SelectObject(hdcMem, hBitmap);

        try
        {
            if (!BitBlt(hdcMem, 0, 0, width, height, hdcScreen, bounds.Left, bounds.Top, SRCCOPY | CAPTUREBLT))
                return null;

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // Negative height for top-down DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB
                }
            };

            // GDI leaves the 4th byte undefined (usually 0). Declare the bitmap opaque and
            // force alpha to 255, otherwise the PNG is fully transparent and OCR sees nothing.
            var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            IntPtr pixelsPtr = bitmap.GetPixels();

            int scanLines = GetDIBits(hdcMem, hBitmap, 0, (uint)height, pixelsPtr, ref bmi, DIB_RGB_COLORS);
            if (scanLines <= 0)
            {
                bitmap.Dispose();
                return null;
            }

            ForceOpaque(pixelsPtr, bitmap.RowBytes, width, height);
            bitmap.NotifyPixelsChanged();

            long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return new CapturedFrame(bitmap, "Unknown", x - bounds.Left, y - bounds.Top, timestampMs);
        }
        finally
        {
            SelectObject(hdcMem, hOldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    internal static unsafe void ForceOpaque(IntPtr pixels, int rowBytes, int width, int height)
    {
        for (int row = 0; row < height; row++)
        {
            byte* p = (byte*)pixels + (long)row * rowBytes + 3;
            for (int col = 0; col < width; col++, p += 4)
                *p = 0xFF;
        }
    }
}
