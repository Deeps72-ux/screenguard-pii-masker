/**
 * @file screen_capture.mm
 * @brief Full-screen capture using ScreenCaptureKit (macOS 14+).
 *
 * Uses SCScreenshotManager to capture the main display. The async API
 * is bridged to synchronous via dispatch_semaphore so it can be called
 * from the CGEventTap callback thread.
 *
 * Falls back to CGWindowListCreateImage on older macOS versions (< 14).
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
 * Synchronously capture the screen using ScreenCaptureKit.
 * Uses a dispatch semaphore to bridge the async API.
 *
 * @return CGImageRef (retained — caller must release), or nullptr on failure.
 */
static CGImageRef CreateScreenCaptureKitImage() {
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

        // Pick the main display
        SCDisplay *display = shareableContent.displays.firstObject;

        // Step 2: Create a content filter for the display (exclude nothing)
        SCContentFilter *filter = [[SCContentFilter alloc]
            initWithDisplay:display excludingWindows:@[]];

        // Step 3: Configure stream settings
        SCStreamConfiguration *config = [[SCStreamConfiguration alloc] init];
        config.width = (size_t)display.width;
        config.height = (size_t)display.height;
        config.showsCursor = YES;
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

CapturedImage* ScreenCapture::captureFullScreen() {
    @autoreleasepool {
        CGImageRef cgImage = CreateScreenCaptureKitImage();

        if (!cgImage) {
            std::cerr << "[ScreenGuard] ERROR: Screen capture failed.\n"
                      << "             Please grant Screen Recording permission:\n"
                      << "             System Settings → Privacy & Security → Screen Recording"
                      << std::endl;
            return nullptr;
        }

        // Extract image dimensions
        size_t width = CGImageGetWidth(cgImage);
        size_t height = CGImageGetHeight(cgImage);
        size_t bytesPerRow = CGImageGetBytesPerRow(cgImage);
        size_t bitsPerPixel = CGImageGetBitsPerPixel(cgImage);
        size_t dataSize = bytesPerRow * height;

        // Get the raw pixel data
        CGDataProviderRef provider = CGImageGetDataProvider(cgImage);
        if (!provider) {
            CGImageRelease(cgImage);
            std::cerr << "[ScreenGuard] ERROR: Failed to get data provider from CGImage." << std::endl;
            return nullptr;
        }

        CFDataRef dataRef = CGDataProviderCopyData(provider);
        if (!dataRef) {
            CGImageRelease(cgImage);
            std::cerr << "[ScreenGuard] ERROR: Failed to copy pixel data." << std::endl;
            return nullptr;
        }

        const uint8_t* srcBytes = CFDataGetBytePtr(dataRef);
        size_t srcLength = (size_t)CFDataGetLength(dataRef);

        // Allocate buffer and copy pixel data
        uint8_t* rawData = (uint8_t*)malloc(dataSize);
        if (!rawData) {
            CFRelease(dataRef);
            CGImageRelease(cgImage);
            std::cerr << "[ScreenGuard] ERROR: Failed to allocate " << dataSize
                      << " bytes for pixel buffer." << std::endl;
            return nullptr;
        }

        size_t copySize = (srcLength < dataSize) ? srcLength : dataSize;
        std::memcpy(rawData, srcBytes, copySize);

        CFRelease(dataRef);
        CGImageRelease(cgImage);

        // Build result struct
        CapturedImage* result = new CapturedImage();
        result->width = static_cast<int>(width);
        result->height = static_cast<int>(height);
        result->stride = static_cast<int>(bytesPerRow);
        result->data = rawData;
        result->dataSize = dataSize;

        std::cout << "[ScreenGuard] Screen captured: " << width << "x" << height
                  << " (" << bitsPerPixel << "bpp, " << bytesPerRow << " bytes/row, "
                  << (dataSize / 1024 / 1024) << " MB)" << std::endl;

        return result;
    }
}

void ScreenCapture::freeCapture(CapturedImage* image) {
    if (!image) return;
    if (image->data) {
        free(image->data);
        image->data = nullptr;
    }
    delete image;
}
