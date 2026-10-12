/**
 * ImageConverter.cs
 * Converts raw BGRA pixel buffers from the native layer into SkiaSharp bitmaps.
 *
 * Handles stride differences between the native capture buffer and
 * SkiaSharp's internal row alignment. Copies pixel data row-by-row
 * when strides don't match.
 */

using SkiaSharp;

namespace SkanCapture.App.Services;

/// <summary>
/// Converts raw pixel buffers from the native C++ layer into SkiaSharp bitmaps.
/// </summary>
public class ImageConverter
{
    /// <summary>
    /// Convert a raw BGRA pixel buffer to an SKBitmap.
    /// </summary>
    /// <param name="data">Pointer to the raw BGRA pixel data.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="stride">Bytes per row in the source buffer.</param>
    /// <returns>A new SKBitmap, or null if the input is invalid.</returns>
    public SKBitmap? FromRawBgra(IntPtr data, int width, int height, int stride)
    {
        if (data == IntPtr.Zero || width <= 0 || height <= 0 || stride <= 0)
        {
            Console.Error.WriteLine("[ImageConverter] Invalid input parameters.");
            return null;
        }

        // Create a bitmap with the same dimensions
        // macOS Core Graphics outputs BGRA with premultiplied alpha
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);

        IntPtr destPtr = bitmap.GetPixels();
        int destStride = bitmap.RowBytes;

        if (stride == destStride)
        {
            // Fast path: strides match, single memcpy
            long totalBytes = (long)stride * height;
            unsafe
            {
                Buffer.MemoryCopy(
                    (void*)data, (void*)destPtr,
                    totalBytes, totalBytes);
            }
        }
        else
        {
            // Slow path: copy row by row to handle stride differences
            int minStride = Math.Min(stride, destStride);
            for (int y = 0; y < height; y++)
            {
                IntPtr srcRow = IntPtr.Add(data, y * stride);
                IntPtr dstRow = IntPtr.Add(destPtr, y * destStride);
                unsafe
                {
                    Buffer.MemoryCopy(
                        (void*)srcRow, (void*)dstRow,
                        minStride, minStride);
                }
            }
        }

        bitmap.NotifyPixelsChanged();
        return bitmap;
    }
}
