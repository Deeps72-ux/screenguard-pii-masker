/**
 * @file screenguard_api.cpp
 * @brief Main ScreenGuard native API implementation.
 *
 * Wires together all native modules (MouseMonitor, InputClassifier,
 * ScreenCapture, GpuDetector) and exposes the unified C API for the
 * C# application layer via P/Invoke.
 *
 * Flow on each mouse click:
 *   1. CGEventTap fires → MouseMonitor callback
 *   2. Query InputClassifier for the most recent device type
 *   3. ScreenCapture grabs the full screen as raw BGRA
 *   4. Build a ScreenGuardCapture struct and invoke the C# callback
 *   5. C# processes (OCR, PII mask, save) then calls ScreenGuardFreeCapture
 */

#include "screenguard_api.h"
#include "mouse_monitor.h"
#include "input_classifier.h"
#include "screen_capture.h"
#include "gpu_detector.h"

#include <iostream>
#include <chrono>
#include <mutex>
#include <vector>
#include <cstring>

/* ──────────────────────────────────────────────────────────────────────────── */
/*  Global State                                                               */
/* ──────────────────────────────────────────────────────────────────────────── */

static MouseMonitor*                g_mouseMonitor    = nullptr;
static InputClassifier*             g_inputClassifier = nullptr;
static ScreenGuardCaptureCallback   g_callback        = nullptr;
static std::vector<GpuDeviceInfo>   g_gpus;
static std::mutex                   g_initMutex;

/* ──────────────────────────────────────────────────────────────────────────── */
/*  API Implementation                                                         */
/* ──────────────────────────────────────────────────────────────────────────── */

extern "C" {

int ScreenGuardInit(ScreenGuardCaptureCallback callback) {
    std::lock_guard<std::mutex> lock(g_initMutex);

    if (g_mouseMonitor) {
        std::cerr << "[ScreenGuard] Already initialized. Call ScreenGuardShutdown() first." << std::endl;
        return -1;
    }

    g_callback = callback;

    std::cout << "\n[ScreenGuard] ══════════════════════════════════════════" << std::endl;
    std::cout << "[ScreenGuard]   Initializing ScreenGuard Engine" << std::endl;
    std::cout << "[ScreenGuard] ══════════════════════════════════════════\n" << std::endl;

    // ── Step 1: Detect GPUs ──────────────────────────────────────────────
    g_gpus = GpuDetector::detectGPUs();
    GpuDetector::printGPUInfo();

    // ── Step 2: Start Input Classifier (IOKit HID) ───────────────────────
    g_inputClassifier = new InputClassifier();
    if (!g_inputClassifier->start()) {
        std::cerr << "[ScreenGuard] WARNING: Input classifier failed to start.\n"
                  << "             Device classification will default to 'Unknown'." << std::endl;
        // Non-fatal: continue without classification
    } else {
        // Log connected devices
        auto devices = g_inputClassifier->getConnectedDevices();
        std::cout << "[ScreenGuard] Detected " << devices.size()
                  << " pointing device(s)." << std::endl;
    }

    // ── Step 3: Start Mouse Monitor (CGEventTap) ─────────────────────────
    g_mouseMonitor = new MouseMonitor();

    bool started = g_mouseMonitor->start([](const MouseClickEvent& click) {
        // Determine input source:
        // Priority 1: Hardware event subtype from CGEvent (macOS Quartz Event Services)
        //   subtype 3 = NX_SUBTYPE_MOUSE_TOUCH (multitouch trackpad or Magic Trackpad)
        //   subtype 0 = NX_SUBTYPE_DEFAULT (standard external mouse)
        // Priority 2: IOKit HID Classifier (active hardware device)
        InputSource source = InputSource::Unknown;
        if (click.subtype == 3) {
            source = InputSource::Trackpad;
        } else if (click.subtype == 0) {
            if (g_inputClassifier) {
                source = g_inputClassifier->getLastInputSource();
            }
            if (source == InputSource::Unknown) {
                source = InputSource::ExternalMouse;
            }
        } else if (g_inputClassifier) {
            source = g_inputClassifier->getLastInputSource();
        }

        const char* sourceStr = "Unknown";
        switch (source) {
            case InputSource::ExternalMouse: sourceStr = "External Mouse"; break;
            case InputSource::Trackpad:      sourceStr = "Trackpad";       break;
            default: break;
        }

        std::cout << "\n[ScreenGuard] ── Click Detected ──────────────────────" << std::endl;
        std::cout << "[ScreenGuard]   Position: (" << click.x << ", " << click.y << ")" << std::endl;
        std::cout << "[ScreenGuard]   Button: " << (click.isLeftClick ? "Left" : "Right") << std::endl;
        std::cout << "[ScreenGuard]   Source: " << sourceStr << std::endl;
        std::cout << "[ScreenGuard]   Capturing screen..." << std::endl;

        // Capture the screen
        CapturedImage* image = ScreenCapture::captureFullScreen();
        if (!image) {
            std::cerr << "[ScreenGuard]   Screen capture FAILED." << std::endl;
            return;
        }

        // Build the ScreenGuardCapture struct to pass to C#
        ScreenGuardCapture* capture = (ScreenGuardCapture*)malloc(sizeof(ScreenGuardCapture));
        if (!capture) {
            std::cerr << "[ScreenGuard]   Memory allocation failed for ScreenGuardCapture." << std::endl;
            ScreenCapture::freeCapture(image);
            return;
        }

        capture->width       = image->width;
        capture->height      = image->height;
        capture->stride      = image->stride;
        capture->pixelData   = image->data;     // Transfer ownership of pixel buffer
        capture->inputSource = static_cast<int>(source);
        capture->clickX      = click.x;
        capture->clickY      = click.y;
        capture->timestampMs = click.timestampMs;

        // Prevent double-free: null out the data pointer before deleting container
        image->data = nullptr;
        delete image;

        std::cout << "[ScreenGuard]   Passing capture to C# layer..." << std::endl;

        // Invoke the C# callback
        if (g_callback) {
            g_callback(capture);
        } else {
            // No callback registered — free immediately
            ScreenGuardFreeCapture(capture);
        }
    });

    if (!started) {
        std::cerr << "[ScreenGuard] FATAL: Failed to start mouse monitor." << std::endl;
        delete g_mouseMonitor;
        g_mouseMonitor = nullptr;
        if (g_inputClassifier) {
            g_inputClassifier->stop();
            delete g_inputClassifier;
            g_inputClassifier = nullptr;
        }
        return -2;
    }

    std::cout << "\n[ScreenGuard] ══════════════════════════════════════════" << std::endl;
    std::cout << "[ScreenGuard]   Ready! Click anywhere to capture." << std::endl;
    std::cout << "[ScreenGuard] ══════════════════════════════════════════\n" << std::endl;

    return 0;
}

void ScreenGuardShutdown() {
    std::lock_guard<std::mutex> lock(g_initMutex);

    if (g_mouseMonitor) {
        g_mouseMonitor->stop();
        delete g_mouseMonitor;
        g_mouseMonitor = nullptr;
    }

    if (g_inputClassifier) {
        g_inputClassifier->stop();
        delete g_inputClassifier;
        g_inputClassifier = nullptr;
    }

    g_callback = nullptr;
    g_gpus.clear();

    std::cout << "[ScreenGuard] Shutdown complete." << std::endl;
}

int ScreenGuardGetGpuCount() {
    return static_cast<int>(g_gpus.size());
}

int ScreenGuardGetGpuInfo(int index, ScreenGuardGpuInfo* out) {
    if (index < 0 || index >= static_cast<int>(g_gpus.size()) || !out) {
        return -1;
    }

    const auto& gpu = g_gpus[static_cast<size_t>(index)];
    std::strncpy(out->name, gpu.name.c_str(), sizeof(out->name) - 1);
    out->name[sizeof(out->name) - 1] = '\0';
    out->type = static_cast<int>(gpu.type);

    return 0;
}

ScreenGuardCapture* ScreenGuardCaptureNow() {
    CapturedImage* image = ScreenCapture::captureFullScreen();
    if (!image) return nullptr;

    InputSource source = InputSource::Unknown;
    if (g_inputClassifier) {
        source = g_inputClassifier->getLastInputSource();
    }

    auto now = std::chrono::system_clock::now();
    auto epochMs = std::chrono::duration_cast<std::chrono::milliseconds>(
        now.time_since_epoch()).count();

    ScreenGuardCapture* capture = (ScreenGuardCapture*)malloc(sizeof(ScreenGuardCapture));
    if (!capture) {
        ScreenCapture::freeCapture(image);
        return nullptr;
    }

    capture->width       = image->width;
    capture->height      = image->height;
    capture->stride      = image->stride;
    capture->pixelData   = image->data;
    capture->inputSource = static_cast<int>(source);
    capture->clickX      = 0.0;
    capture->clickY      = 0.0;
    capture->timestampMs = epochMs;

    image->data = nullptr;
    delete image;

    return capture;
}

void ScreenGuardFreeCapture(ScreenGuardCapture* capture) {
    if (!capture) return;

    if (capture->pixelData) {
        free(capture->pixelData);
        capture->pixelData = nullptr;
    }

    free(capture);
}

} // extern "C"
