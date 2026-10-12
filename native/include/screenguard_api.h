/**
 * @file screenguard_api.h
 * @brief ScreenGuard Screen Capture Native API
 *
 * Public C API for the screen capture library. This header defines all
 * exported functions, structures, and callback types that the C# application
 * layer uses via P/Invoke.
 *
 * Architecture:
 *   C++ (this library) → captures screen, monitors mouse, classifies input, detects GPUs
 *   C# (application)   → receives captures via callback, performs OCR/PII masking, saves to disk
 */

#ifndef SCREENGUARD_API_H
#define SCREENGUARD_API_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ──────────────────────────────────────────────────────────────────────────── */
/*  Constants                                                                  */
/* ──────────────────────────────────────────────────────────────────────────── */

/** Input source classification */
#define SG_INPUT_UNKNOWN          0
#define SG_INPUT_EXTERNAL_MOUSE   1
#define SG_INPUT_TRACKPAD         2

/** GPU type classification */
#define SG_GPU_INTEGRATED         0
#define SG_GPU_DISCRETE           1
#define SG_GPU_EXTERNAL           2

/* ──────────────────────────────────────────────────────────────────────────── */
/*  Structures                                                                 */
/* ──────────────────────────────────────────────────────────────────────────── */

/**
 * Screen capture result passed from C++ to C# via callback.
 *
 * Memory layout must match the C# StructLayout exactly:
 *   width       (4 bytes)
 *   height      (4 bytes)
 *   stride      (4 bytes)
 *   [padding]   (4 bytes — for pointer alignment)
 *   pixelData   (8 bytes — pointer)
 *   inputSource (4 bytes)
 *   [padding]   (4 bytes — for double alignment)
 *   clickX      (8 bytes)
 *   clickY      (8 bytes)
 *   timestampMs (8 bytes)
 */
typedef struct {
    int         width;
    int         height;
    int         stride;         /**< Bytes per row (may include padding) */
    uint8_t*    pixelData;      /**< Raw BGRA pixel buffer (malloc'd) */
    int         inputSource;    /**< SG_INPUT_* constant */
    double      clickX;         /**< Screen X coordinate of click */
    double      clickY;         /**< Screen Y coordinate of click */
    int64_t     timestampMs;    /**< Unix epoch milliseconds */
} ScreenGuardCapture;

/**
 * GPU device information.
 */
typedef struct {
    char    name[256];          /**< GPU device name (null-terminated) */
    int     type;               /**< SG_GPU_* constant */
} ScreenGuardGpuInfo;

/* ──────────────────────────────────────────────────────────────────────────── */
/*  Callback Type                                                              */
/* ──────────────────────────────────────────────────────────────────────────── */

/**
 * Callback invoked by the native layer on each mouse click capture.
 * The C# application registers this via ScreenGuardInit().
 *
 * @param capture  Pointer to the capture data. The callee MUST call
 *                 ScreenGuardFreeCapture() when finished processing.
 */
typedef void (*ScreenGuardCaptureCallback)(ScreenGuardCapture* capture);

/* ──────────────────────────────────────────────────────────────────────────── */
/*  API Functions                                                              */
/* ──────────────────────────────────────────────────────────────────────────── */

/**
 * Initialize the capture system: starts mouse monitoring, input classification,
 * and GPU detection.
 *
 * @param callback  Function pointer to invoke on each mouse click capture.
 * @return  0 on success, negative error code on failure.
 *          -1 = already initialized
 *          -2 = failed to start mouse monitor (check Input Monitoring permissions)
 */
__attribute__((visibility("default")))
int ScreenGuardInit(ScreenGuardCaptureCallback callback);

/**
 * Shut down the capture system and release all resources.
 */
__attribute__((visibility("default")))
void ScreenGuardShutdown(void);

/**
 * Get the number of detected GPU devices.
 * Must be called after ScreenGuardInit().
 */
__attribute__((visibility("default")))
int ScreenGuardGetGpuCount(void);

/**
 * Get information about a specific GPU device.
 *
 * @param index  Zero-based GPU index (0 to ScreenGuardGetGpuCount()-1).
 * @param out    Pointer to a ScreenGuardGpuInfo struct to fill.
 * @return  0 on success, -1 on invalid index or null pointer.
 */
__attribute__((visibility("default")))
int ScreenGuardGetGpuInfo(int index, ScreenGuardGpuInfo* out);

/**
 * Perform an immediate screen capture (for testing).
 * The caller must free the result with ScreenGuardFreeCapture().
 *
 * @return  Pointer to capture data, or NULL on failure.
 */
__attribute__((visibility("default")))
ScreenGuardCapture* ScreenGuardCaptureNow(void);

/**
 * Free a ScreenGuardCapture struct and its pixel buffer.
 *
 * @param capture  Pointer to capture data to free. Safe to pass NULL.
 */
__attribute__((visibility("default")))
void ScreenGuardFreeCapture(ScreenGuardCapture* capture);

#ifdef __cplusplus
}
#endif

#endif /* SCREENGUARD_API_H */
