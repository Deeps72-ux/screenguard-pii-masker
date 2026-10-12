/**
 * @file screen_capture.h
 * @brief Full-screen capture using Core Graphics (CGWindowListCreateImage).
 *
 * Captures the entire screen as a raw BGRA pixel buffer.
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
};

/**
 * Static screen capture utility class.
 */
class ScreenCapture {
public:
    /**
     * Capture the full screen and return a CapturedImage.
     *
     * @return  Pointer to a CapturedImage on success, nullptr on failure.
     *          The caller is responsible for freeing with freeCapture().
     */
    static CapturedImage* captureFullScreen();

    /**
     * Free a CapturedImage and its pixel buffer.
     * Safe to pass nullptr.
     */
    static void freeCapture(CapturedImage* image);
};

#endif /* SCREEN_CAPTURE_H */
