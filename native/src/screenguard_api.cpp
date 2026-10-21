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
 *   2. Classify the input source (event subtype + IOKit HID) and queue the click
 *      — the tap callback returns immediately so macOS never disables the tap
 *   3. Capture worker thread grabs the clicked display as raw BGRA
 *   4. Build a ScreenGuardCapture struct and invoke the C# callback
 *   5. C# processes (OCR, PII mask, save) then calls ScreenGuardFreeCapture
 */

#include "screenguard_api.h"
#include "mouse_monitor.h"
#include "input_classifier.h"
#include "screen_capture.h"
#include "gpu_detector.h"

#include <CoreGraphics/CoreGraphics.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <iostream>
#include <mutex>
#include <thread>
#include <vector>

/* ──────────────────────────────────────────────────────────────────────────── */
/*  Global State                                                               */
/* ──────────────────────────────────────────────────────────────────────────── */

struct PendingClick {
    MouseClickEvent click;
    InputSource     source;
};

/** Clicks beyond this backlog are dropped (and counted) instead of queuing unbounded captures. */
static constexpr size_t kMaxPendingClicks = 16;

static MouseMonitor*                g_mouseMonitor    = nullptr;
static InputClassifier*             g_inputClassifier = nullptr;
static ScreenGuardCaptureCallback   g_callback        = nullptr;
static std::mutex                   g_initMutex;

static std::vector<GpuDeviceInfo>   g_gpus;
static bool                         g_gpusDetected    = false;
static std::mutex                   g_gpuMutex;

static std::thread                  g_worker;
static std::mutex                   g_queueMutex;
static std::condition_variable      g_queueCv;
static std::deque<PendingClick>     g_queue;
static bool                         g_workerStop      = false;
static std::atomic<uint64_t>        g_droppedClicks{0};

/* ──────────────────────────────────────────────────────────────────────────── */
/*  Helpers                                                                    */
/* ──────────────────────────────────────────────────────────────────────────── */

static void EnsureGpusDetected() {
    std::lock_guard<std::mutex> lock(g_gpuMutex);
    if (!g_gpusDetected) {
        g_gpus = GpuDetector::detectGPUs();
        g_gpusDetected = true;
    }
}

/**
 * Determine the input source for a click.
 * Priority 1: Hardware event subtype from CGEvent (macOS Quartz Event Services)
 *   subtype 3 = NX_SUBTYPE_MOUSE_TOUCH (multitouch trackpad or Magic Trackpad)
 *   subtype 0 = NX_SUBTYPE_DEFAULT (standard external mouse)
 * Priority 2: IOKit HID Classifier (active hardware device)
 */
static InputSource ClassifyClick(const MouseClickEvent& click) {
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
    return source;
}

/** Wrap a CapturedImage in the C ABI struct, transferring pixel ownership. */
static ScreenGuardCapture* MakeCapture(CapturedImage* image, InputSource source,
                                       double clickX, double clickY, int64_t timestampMs) {
    ScreenGuardCapture* capture = (ScreenGuardCapture*)malloc(sizeof(ScreenGuardCapture));
    if (!capture) {
        std::cerr << "[ScreenGuard] Memory allocation failed for ScreenGuardCapture." << std::endl;
        ScreenCapture::freeCapture(image);
        return nullptr;
    }

    capture->width       = image->width;
    capture->height      = image->height;
    capture->stride      = image->stride;
    capture->pixelData   = image->data;     // Transfer ownership of pixel buffer
    capture->inputSource = static_cast<int>(source);
    capture->clickX      = clickX;
    capture->clickY      = clickY;
    capture->timestampMs = timestampMs;

    // Prevent double-free: null out the data pointer before deleting container
    image->data = nullptr;
    delete image;
    return capture;
}

/** Worker thread: captures the clicked display for each queued click and hands it to C#. */
static void CaptureWorkerLoop() {
    while (true) {
        PendingClick pending;
        {
            std::unique_lock<std::mutex> lock(g_queueMutex);
            g_queueCv.wait(lock, [] { return g_workerStop || !g_queue.empty(); });
            if (g_workerStop) return;
            pending = g_queue.front();
            g_queue.pop_front();
        }

        CapturedImage* image = ScreenCapture::captureDisplayAtPoint(pending.click.x, pending.click.y);
        if (!image) {
            std::cerr << "[ScreenGuard] Screen capture FAILED for click at ("
                      << pending.click.x << ", " << pending.click.y << ")." << std::endl;
            continue;
        }

        // Convert global point coordinates into pixel coordinates of the captured image
        double clickX = (pending.click.x - image->originX) * image->scale;
        double clickY = (pending.click.y - image->originY) * image->scale;

        ScreenGuardCapture* capture = MakeCapture(image, pending.source, clickX, clickY,
                                                  pending.click.timestampMs);
        if (!capture) continue;

        if (g_callback) {
            g_callback(capture);
        } else {
            // No callback registered — free immediately
            ScreenGuardFreeCapture(capture);
        }
    }
}

static void StopWorker() {
    {
        std::lock_guard<std::mutex> lock(g_queueMutex);
        g_workerStop = true;
        g_queue.clear();
    }
    g_queueCv.notify_all();
    if (g_worker.joinable()) {
        g_worker.join();
    }
}

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

    // ── Step 1: Verify permissions up front ──────────────────────────────
    // Without Input Monitoring an event tap can still be created but never
    // receives events, so check explicitly instead of reporting success.
    if (!CGPreflightListenEventAccess()) {
        CGRequestListenEventAccess();
        std::cerr << "[ScreenGuard] ERROR: Input Monitoring permission is not granted.\n"
                  << "             Enable your terminal (or ScreenGuard.app) in:\n"
                  << "             System Settings → Privacy & Security → Input Monitoring\n"
                  << "             then quit and reopen it." << std::endl;
        return -3;
    }
    if (!CGPreflightScreenCaptureAccess()) {
        CGRequestScreenCaptureAccess();
        std::cerr << "[ScreenGuard] ERROR: Screen Recording permission is not granted.\n"
                  << "             Enable your terminal (or ScreenGuard.app) in:\n"
                  << "             System Settings → Privacy & Security → Screen Recording\n"
                  << "             then quit and reopen it." << std::endl;
        return -4;
    }

    g_callback = callback;
    g_droppedClicks.store(0);
    EnsureGpusDetected();

    // ── Step 2: Start Input Classifier (IOKit HID) ───────────────────────
    g_inputClassifier = new InputClassifier();
    if (!g_inputClassifier->start()) {
        std::cerr << "[ScreenGuard] WARNING: Input classifier failed to start.\n"
                  << "             Device classification will rely on event subtypes only." << std::endl;
        // Non-fatal: continue without classification
    } else {
        auto devices = g_inputClassifier->getConnectedDevices();
        std::cout << "[ScreenGuard] Detected " << devices.size()
                  << " pointing device(s)." << std::endl;
    }

    // ── Step 3: Start capture worker ─────────────────────────────────────
    {
        std::lock_guard<std::mutex> queueLock(g_queueMutex);
        g_workerStop = false;
        g_queue.clear();
    }
    g_worker = std::thread(CaptureWorkerLoop);

    // ── Step 4: Start Mouse Monitor (CGEventTap) ─────────────────────────
    g_mouseMonitor = new MouseMonitor();

    bool started = g_mouseMonitor->start([](const MouseClickEvent& click) {
        // Runs on the event tap thread: classify and queue only, never block here
        PendingClick pending{click, ClassifyClick(click)};
        bool queued = false;
        {
            std::lock_guard<std::mutex> queueLock(g_queueMutex);
            if (g_queue.size() < kMaxPendingClicks) {
                g_queue.push_back(pending);
                queued = true;
            }
        }
        if (queued) {
            g_queueCv.notify_one();
        } else {
            g_droppedClicks.fetch_add(1);
            std::cerr << "[ScreenGuard] Capture backlog full; click dropped." << std::endl;
        }
    });

    if (!started) {
        std::cerr << "[ScreenGuard] FATAL: Failed to start mouse monitor." << std::endl;
        delete g_mouseMonitor;
        g_mouseMonitor = nullptr;
        StopWorker();
        if (g_inputClassifier) {
            g_inputClassifier->stop();
            delete g_inputClassifier;
            g_inputClassifier = nullptr;
        }
        g_callback = nullptr;
        return -2;
    }

    return 0;
}

void ScreenGuardShutdown() {
    std::lock_guard<std::mutex> lock(g_initMutex);

    // Stop new clicks first, then the worker (which may be inside the callback)
    if (g_mouseMonitor) {
        g_mouseMonitor->stop();
        delete g_mouseMonitor;
        g_mouseMonitor = nullptr;
    }

    StopWorker();

    if (g_inputClassifier) {
        g_inputClassifier->stop();
        delete g_inputClassifier;
        g_inputClassifier = nullptr;
    }

    g_callback = nullptr;

    uint64_t dropped = g_droppedClicks.load();
    if (dropped > 0) {
        std::cout << "[ScreenGuard] Clicks dropped due to capture backlog: " << dropped << std::endl;
    }
}

int ScreenGuardGetGpuCount() {
    EnsureGpusDetected();
    std::lock_guard<std::mutex> lock(g_gpuMutex);
    return static_cast<int>(g_gpus.size());
}

int ScreenGuardGetGpuInfo(int index, ScreenGuardGpuInfo* out) {
    EnsureGpusDetected();
    std::lock_guard<std::mutex> lock(g_gpuMutex);

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
    if (!CGPreflightScreenCaptureAccess()) {
        CGRequestScreenCaptureAccess();
        std::cerr << "[ScreenGuard] ERROR: Screen Recording permission is not granted.\n"
                  << "             System Settings → Privacy & Security → Screen Recording" << std::endl;
        return nullptr;
    }

    CapturedImage* image = ScreenCapture::captureFullScreen();
    if (!image) return nullptr;

    InputSource source = InputSource::Unknown;
    if (g_inputClassifier) {
        source = g_inputClassifier->getLastInputSource();
    }

    auto now = std::chrono::system_clock::now();
    auto epochMs = std::chrono::duration_cast<std::chrono::milliseconds>(
        now.time_since_epoch()).count();

    return MakeCapture(image, source, 0.0, 0.0, epochMs);
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
