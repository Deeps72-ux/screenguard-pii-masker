/**
 * @file screen_capture.mm
 * @brief Per-display screen capture using ScreenCaptureKit (macOS 14+).
 *
 * Uses SCScreenshotManager to capture a single display at its native pixel
 * resolution. The async API is bridged to synchronous via dispatch_semaphore
 * so it can be called from the native capture worker thread.
 *
 * Falls back to CGDisplayCreateImage on macOS 12.3–13, where
 * SCScreenshotManager is unavailable.
 *
 * Requires Screen Recording permission in System Settings → Privacy & Security.
 */

#import "screen_capture.h"
#import <Foundation/Foundation.h>
#import <CoreGraphics/CoreGraphics.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#import <iostream>
#import <cstring>

/**
 * Pixel dimensions of a display's current mode (e.g. 2560x1600 on a Retina panel
 * whose bounds are 1440x900 points).
 */
static void GetDisplayPixelSize(CGDirectDisplayID displayID, size_t* outWidth, size_t* outHeight) {
    CGDisplayModeRef mode = CGDisplayCopyDisplayMode(displayID);
    if (mode) {
        *outWidth = CGDisplayModeGetPixelWidth(mode);
        *outHeight = CGDisplayModeGetPixelHeight(mode);
        CGDisplayModeRelease(mode);
    } else {
        *outWidth = CGDisplayPixelsWide(displayID);
        *outHeight = CGDisplayPixelsHigh(displayID);
    }
}

/**
 * Synchronously capture one display using ScreenCaptureKit.
 * Uses a dispatch semaphore to bridge the async API.
 *
 * @return CGImageRef (retained — caller must release), or nullptr on failure.
 */
API_AVAILABLE(macos(14.0))
static CGImageRef CreateScreenCaptureKitImage(CGDirectDisplayID displayID, size_t pixelWidth, size_t pixelHeight) {
    __block CGImageRef capturedImage = nullptr;
    dispatch_semaphore_t semaphore = dispatch_semaphore_create(0);

    // Step 1: Get shareable content (displays, windows, apps)
    [SCShareableContent getShareableContentWithCompletionHandler:^(
            SCShareableContent * _Nullable shareableContent,
            NSError * _Nullable error) {

        if (error || !shareableContent || shareableContent.displays.count == 0) {
            NSLog(@"[ScreenGuard] Error getting shareable content: %@",
                  error ? error.localizedDescription : @"No displays found");
            dispatch_semaphore_signal(semaphore);
            return;
        }

        // Pick the requested display, falling back to the first one
        SCDisplay *display = shareableContent.displays.firstObject;
        for (SCDisplay *candidate in shareableContent.displays) {
            if (candidate.displayID == displayID) {
                display = candidate;
                break;
            }
        }

        // Step 2: Create a content filter for the display (exclude nothing)
        SCContentFilter *filter = [[SCContentFilter alloc]
            initWithDisplay:display excludingWindows:@[]];

        // Step 3: Configure capture at native pixel resolution
        SCStreamConfiguration *config = [[SCStreamConfiguration alloc] init];
        config.width = pixelWidth;
        config.height = pixelHeight;
        // The cursor is left out: drawn over text it makes OCR misread (and so leak)
        // the value beneath it. The click position is reported separately.
        config.showsCursor = NO;
        config.pixelFormat = kCVPixelFormatType_32BGRA;

        // Step 4: Capture the screenshot
        [SCScreenshotManager captureImageWithFilter:filter
                                      configuration:config
                                  completionHandler:^(CGImageRef _Nullable cgImage,
                                                      NSError * _Nullable captureError) {
            if (captureError) {
                NSLog(@"[ScreenGuard] Capture error: %@", captureError.localizedDescription);
            } else if (cgImage) {
                CGImageRetain(cgImage);
                capturedImage = cgImage;
            }
            dispatch_semaphore_signal(semaphore);
        }];
    }];

    // Wait for the async capture to complete (timeout: 5 seconds)
    dispatch_time_t timeout = dispatch_time(DISPATCH_TIME_NOW, 5 * NSEC_PER_SEC);
    if (dispatch_semaphore_wait(semaphore, timeout) != 0) {
        NSLog(@"[ScreenGuard] Screenshot capture timed out.");
        return nullptr;
    }

    return capturedImage;
}

/**
 * Capture a display on macOS versions without SCScreenshotManager.
 */
static CGImageRef CreateLegacyDisplayImage(CGDirectDisplayID displayID) {
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
    return CGDisplayCreateImage(displayID);
#pragma clang diagnostic pop
}

/**
 * Copy a CGImage's pixels into a malloc'd BGRA buffer.
 */
static CapturedImage* CopyImagePixels(CGImageRef cgImage, CGDirectDisplayID displayID) {
    // Extract image dimensions
    size_t width = CGImageGetWidth(cgImage);
    size_t height = CGImageGetHeight(cgImage);
    size_t bytesPerRow = CGImageGetBytesPerRow(cgImage);
    size_t dataSize = bytesPerRow * height;

    // Get the raw pixel data
    CGDataProviderRef provider = CGImageGetDataProvider(cgImage);
    if (!provider) {
        std::cerr << "[ScreenGuard] ERROR: Failed to get data provider from CGImage." << std::endl;
        return nullptr;
    }

    CFDataRef dataRef = CGDataProviderCopyData(provider);
    if (!dataRef) {
        std::cerr << "[ScreenGuard] ERROR: Failed to copy pixel data." << std::endl;
        return nullptr;
    }

    const uint8_t* srcBytes = CFDataGetBytePtr(dataRef);
    size_t srcLength = (size_t)CFDataGetLength(dataRef);

    // Allocate buffer and copy pixel data
    uint8_t* rawData = (uint8_t*)malloc(dataSize);
    if (!rawData) {
        CFRelease(dataRef);
        std::cerr << "[ScreenGuard] ERROR: Failed to allocate " << dataSize
                  << " bytes for pixel buffer." << std::endl;
        return nullptr;
    }

    size_t copySize = (srcLength < dataSize) ? srcLength : dataSize;
    std::memcpy(rawData, srcBytes, copySize);
    if (copySize < dataSize) {
        std::memset(rawData + copySize, 0, dataSize - copySize);
    }
    CFRelease(dataRef);

    CGRect bounds = CGDisplayBounds(displayID);

    // Build result struct
    CapturedImage* result = new CapturedImage();
    result->width = static_cast<int>(width);
    result->height = static_cast<int>(height);
    result->stride = static_cast<int>(bytesPerRow);
    result->data = rawData;
    result->dataSize = dataSize;
    result->originX = bounds.origin.x;
    result->originY = bounds.origin.y;
    result->scale = bounds.size.width > 0 ? (double)width / bounds.size.width : 1.0;
    return result;
}

static CapturedImage* CaptureDisplay(CGDirectDisplayID displayID) {
    @autoreleasepool {
        size_t pixelWidth = 0, pixelHeight = 0;
        GetDisplayPixelSize(displayID, &pixelWidth, &pixelHeight);

        CGImageRef cgImage = nullptr;
        if (@available(macOS 14.0, *)) {
            cgImage = CreateScreenCaptureKitImage(displayID, pixelWidth, pixelHeight);
        } else {
            cgImage = CreateLegacyDisplayImage(displayID);
        }

        if (!cgImage) {
            std::cerr << "[ScreenGuard] ERROR: Screen capture failed.\n"
                      << "             Please grant Screen Recording permission:\n"
                      << "             System Settings → Privacy & Security → Screen Recording"
                      << std::endl;
            return nullptr;
        }

        CapturedImage* result = CopyImagePixels(cgImage, displayID);
        CGImageRelease(cgImage);
        return result;
    }
}

CapturedImage* ScreenCapture::captureFullScreen() {
    return CaptureDisplay(CGMainDisplayID());
}

CapturedImage* ScreenCapture::captureDisplayAtPoint(double x, double y) {
    CGDirectDisplayID displayID = CGMainDisplayID();
    uint32_t count = 0;
    if (CGGetDisplaysWithPoint(CGPointMake(x, y), 1, &displayID, &count) != kCGErrorSuccess || count == 0) {
        displayID = CGMainDisplayID();
    }
    return CaptureDisplay(displayID);
}

void ScreenCapture::freeCapture(CapturedImage* image) {
    if (!image) return;
    if (image->data) {
        free(image->data);
        image->data = nullptr;
    }
    delete image;
}
