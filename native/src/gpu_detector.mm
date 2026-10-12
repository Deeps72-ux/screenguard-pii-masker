/**
 * @file gpu_detector.mm
 * @brief GPU detection and classification using the Metal API.
 *
 * Objective-C++ file (.mm) because the Metal API is Objective-C based.
 * Enumerates all Metal-compatible GPU devices and classifies them using:
 *   - isLowPower   → Integrated GPU (iGPU)
 *   - isRemovable  → External GPU (eGPU)
 *   - neither      → Discrete GPU (dGPU)
 *
 * On Apple Silicon (M1/M2/M3/M4): typically one integrated GPU.
 * On Intel Macs: may have both Intel Iris (integrated) and AMD Radeon (discrete).
 */

#import "gpu_detector.h"
#import <Metal/Metal.h>
#import <Foundation/Foundation.h>
#import <iostream>

std::vector<GpuDeviceInfo> GpuDetector::detectGPUs() {
    std::vector<GpuDeviceInfo> gpus;

    // MTLCopyAllDevices() returns all Metal-compatible GPUs on the system
    // Available on macOS 10.11+. On Apple Silicon, returns a single integrated GPU.
    NSArray<id<MTLDevice>>* devices = MTLCopyAllDevices();

    if (!devices || devices.count == 0) {
        // Fallback: try the system default device (always available if Metal is supported)
        id<MTLDevice> defaultDevice = MTLCreateSystemDefaultDevice();
        if (defaultDevice) {
            GpuDeviceInfo info;
            info.name = std::string([defaultDevice.name UTF8String]);
            info.isLowPower = defaultDevice.isLowPower;
            info.isRemovable = defaultDevice.isRemovable;

            if (info.isRemovable) {
                info.type = GpuType::External;
            } else if (info.isLowPower) {
                info.type = GpuType::Integrated;
            } else {
                // On Apple Silicon, the unified GPU reports isLowPower=false
                // but it's still integrated into the SoC
                info.type = GpuType::Integrated;
            }

            gpus.push_back(info);
        }
        return gpus;
    }

    for (id<MTLDevice> device in devices) {
        GpuDeviceInfo info;
        info.name = std::string([device.name UTF8String]);
        info.isLowPower = device.isLowPower;
        info.isRemovable = device.isRemovable;

        if (info.isRemovable) {
            info.type = GpuType::External;
        } else if (info.isLowPower) {
            info.type = GpuType::Integrated;
        } else {
            // Could be discrete GPU on Intel Mac, or unified GPU on Apple Silicon
            // If there's only one GPU (Apple Silicon), treat it as Integrated
            if (devices.count == 1) {
                info.type = GpuType::Integrated;
            } else {
                info.type = GpuType::Discrete;
            }
        }

        gpus.push_back(info);
    }

    return gpus;
}

void GpuDetector::printGPUInfo() {
    auto gpus = detectGPUs();

    std::cout << "[ScreenGuard] ╔══════════════════════════════════════════╗" << std::endl;
    std::cout << "[ScreenGuard] ║          GPU Information                 ║" << std::endl;
    std::cout << "[ScreenGuard] ╠══════════════════════════════════════════╣" << std::endl;

    if (gpus.empty()) {
        std::cout << "[ScreenGuard] ║  No Metal-compatible GPUs found.        ║" << std::endl;
    } else {
        for (size_t i = 0; i < gpus.size(); i++) {
            const auto& gpu = gpus[i];
            const char* typeStr = "Unknown";
            switch (gpu.type) {
                case GpuType::Integrated: typeStr = "Integrated (iGPU)"; break;
                case GpuType::Discrete:   typeStr = "Discrete (dGPU)";   break;
                case GpuType::External:   typeStr = "External (eGPU)";   break;
            }
            std::cout << "[ScreenGuard] ║  GPU " << i << ": " << gpu.name << std::endl;
            std::cout << "[ScreenGuard] ║    Type: " << typeStr
                      << " | LowPower: " << (gpu.isLowPower ? "Yes" : "No")
                      << " | Removable: " << (gpu.isRemovable ? "Yes" : "No")
                      << std::endl;
        }
    }

    std::cout << "[ScreenGuard] ╚══════════════════════════════════════════╝" << std::endl;
}
