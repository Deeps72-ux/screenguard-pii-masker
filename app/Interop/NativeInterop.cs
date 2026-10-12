/**
 * NativeInterop.cs
 * P/Invoke bindings for the C++ native screen capture library (libscreencapture.dylib).
 *
 * Struct layouts must exactly match the C structs in skan_api.h, including
 * padding/alignment for pointer and double fields on 64-bit platforms.
 */

using System.Runtime.InteropServices;

namespace SkanCapture.App.Interop;

/// <summary>
/// P/Invoke declarations for the native C++ screen capture library.
/// </summary>
public static class NativeInterop
{
    /// <summary>
    /// Library name for DllImport. On macOS, .NET automatically maps
    /// "screencapture" → "libscreencapture.dylib".
    /// </summary>
    private const string LibName = "screencapture";

    /* ─────────────────────────────────────────────────────────────────── */
    /*  Callback Delegate                                                  */
    /* ─────────────────────────────────────────────────────────────────── */

    /// <summary>
    /// Callback invoked by native code on each mouse click capture.
    /// The IntPtr points to a SkanCapture struct allocated on the native heap.
    /// The managed side MUST call SkanFreeCapture() when done.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SkanCaptureCallback(IntPtr capturePtr);

    /* ─────────────────────────────────────────────────────────────────── */
    /*  Structures                                                         */
    /* ─────────────────────────────────────────────────────────────────── */

    /// <summary>
    /// Matches the C struct SkanCapture in skan_api.h.
    /// Layout on 64-bit:
    ///   Offset  0: Width       (4 bytes)
    ///   Offset  4: Height      (4 bytes)
    ///   Offset  8: Stride      (4 bytes)
    ///   Offset 12: [padding]   (4 bytes for pointer alignment)
    ///   Offset 16: PixelData   (8 bytes pointer)
    ///   Offset 24: InputSource (4 bytes)
    ///   Offset 28: [padding]   (4 bytes for double alignment)
    ///   Offset 32: ClickX      (8 bytes)
    ///   Offset 40: ClickY      (8 bytes)
    ///   Offset 48: TimestampMs (8 bytes)
    ///   Total: 56 bytes
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SkanCaptureStruct
    {
        public int    Width;
        public int    Height;
        public int    Stride;
        public IntPtr PixelData;
        public int    InputSource;
        public double ClickX;
        public double ClickY;
        public long   TimestampMs;
    }

    /// <summary>
    /// Matches the C struct SkanGpuInfo in skan_api.h.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SkanGpuInfoStruct
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Name;

        public int Type;
    }

    /* ─────────────────────────────────────────────────────────────────── */
    /*  API Functions                                                       */
    /* ─────────────────────────────────────────────────────────────────── */

    /// <summary>
    /// Initialize the native capture system.
    /// Returns 0 on success, negative on failure.
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SkanInit(SkanCaptureCallback callback);

    /// <summary>
    /// Shut down the capture system and release resources.
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SkanShutdown();

    /// <summary>
    /// Get the number of detected GPU devices.
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SkanGetGpuCount();

    /// <summary>
    /// Get info about a specific GPU device.
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SkanGetGpuInfo(int index, out SkanGpuInfoStruct info);

    /// <summary>
    /// Perform an immediate screen capture (for testing).
    /// Caller must free with SkanFreeCapture().
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SkanCaptureNow();

    /// <summary>
    /// Free a SkanCapture struct and its pixel buffer.
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SkanFreeCapture(IntPtr capturePtr);
}
