using ScreenGuard.App.Models;

namespace ScreenGuard.App.Abstractions;

/// <summary>
/// Abstraction for detecting graphics hardware (Metal API on macOS, DirectX/WMI on Windows).
/// </summary>
public interface IGpuDetector
{
    /// <summary>Enumerates active graphics cards and classifies them (iGPU, dGPU, eGPU).</summary>
    IReadOnlyList<GpuInfoModel> DetectGpus();
}
