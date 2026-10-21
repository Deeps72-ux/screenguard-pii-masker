/**
 * @file screen_capture.h
 * @brief Per-display screen capture using ScreenCaptureKit (macOS 14+),
 *        with a CGDisplayCreateImage fallback on macOS 12.3–13.
 *
 * Captures one display at its native pixel resolution (e.g. 2560x1600 on a
 * Retina panel rather than the 1440x900 point size) as a raw BGRA buffer.
 * Requires Screen Recording permission in System Settings → Privacy & Security.
 */

#ifndef SCREEN_CAPTURE_H
#define SCREEN_CAPTURE_H

#include <cstdint>
#include <cstddef>

/**
 * Raw captured image data.
 */
struct CapturedImage {
    int       width;      /**< Image width in pixels */
    int       height;     /**< Image height in pixels */
    int       stride;     /**< Bytes per row (may exceed width * 4 due to alignment) */
    uint8_t*  data;       /**< Raw BGRA pixel buffer (malloc'd — caller must free) */
    size_t    dataSize;   /**< Total size of data buffer in bytes */
    double    originX;    /**< Display origin in global points (CG coordinate space) */
    double    originY;    /**< Display origin in global points (CG coordinate space) */
    double    scale;      /**< Pixels per point for the captured display (2.0 on Retina) */
};

/**
 * Static screen capture utility class.
 */
class ScreenCapture {
public:
    /**
     * Capture the main display.
     *
     * @return  Pointer to a CapturedImage on success, nullptr on failure.
     *          The caller is responsible for freeing with freeCapture().
     */
    static CapturedImage* captureFullScreen();

    /**
     * Capture the display that contains the given global point (in points,
     * as reported by CGEventGetLocation). Falls back to the main display.
     */
    static CapturedImage* captureDisplayAtPoint(double x, double y);

    /**
     * Free a CapturedImage and its pixel buffer.
     * Safe to pass nullptr.
     */
    static void freeCapture(CapturedImage* image);
};

#endif /* SCREEN_CAPTURE_H */
