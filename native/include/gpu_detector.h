/**
 * @file gpu_detector.h
 * @brief GPU detection and classification using the Metal API.
 *
 * Enumerates all Metal-compatible GPUs and classifies them as:
 *   - Integrated (iGPU) — isLowPower = true
 *   - Discrete (dGPU)   — isLowPower = false, isRemovable = false
 *   - External (eGPU)   — isRemovable = true
 *
 * On Apple Silicon, there is typically one integrated GPU.
 * On Intel Macs, there may be both an integrated Intel GPU and
 * a discrete AMD GPU.
 */

#ifndef GPU_DETECTOR_H
#define GPU_DETECTOR_H

#include <string>
#include <vector>

/**
 * GPU type classification.
 */
enum class GpuType {
    Integrated = 0,   /**< Low-power integrated GPU (e.g., Intel Iris, Apple M-series) */
    Discrete   = 1,   /**< Dedicated discrete GPU (e.g., AMD Radeon) */
    External   = 2    /**< External GPU via Thunderbolt (eGPU) */
};

/**
 * Information about a detected GPU device.
 */
struct GpuDeviceInfo {
    std::string name;         /**< GPU device name from Metal */
    GpuType     type;         /**< Classification (integrated/discrete/external) */
    bool        isLowPower;   /**< Metal isLowPower property */
    bool        isRemovable;  /**< Metal isRemovable property */
};

/**
 * Static GPU detection utility class.
 */
class GpuDetector {
public:
    /**
     * Detect all Metal-compatible GPUs on the system.
     * @return  Vector of GpuDeviceInfo structs.
     */
    static std::vector<GpuDeviceInfo> detectGPUs();

    /**
     * Print GPU information to stdout.
     */
    static void printGPUInfo();
};

#endif /* GPU_DETECTOR_H */
