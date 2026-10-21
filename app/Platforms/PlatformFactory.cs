using System.Runtime.InteropServices;
using ScreenGuard.App.Abstractions;
using ScreenGuard.App.Platforms.Mac;
using ScreenGuard.App.Platforms.Windows;

namespace ScreenGuard.App.Platforms;

/// <summary>
/// Abstract Factory for resolving platform-specific capture, input, and GPU services.
/// Adheres to Open/Closed (OCP) and Dependency Inversion (DIP) principles.
/// </summary>
public static class PlatformFactory
{
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static string PlatformName =>
        IsMacOS ? "macOS (Apple Silicon / Intel)" :
        IsWindows ? "Microsoft Windows" :
        RuntimeInformation.OSDescription;

    public static IScreenCaptureService CreateCaptureService()
    {
        if (IsMacOS)
            return new MacScreenCaptureService();

        if (IsWindows)
            return new WindowsScreenCaptureService();

        throw new PlatformNotSupportedException($"Screen capture is not supported on {RuntimeInformation.OSDescription}.");
    }

    public static IGlobalInputMonitor CreateInputMonitor(IScreenCaptureService captureService)
    {
        if (IsMacOS)
            return new MacGlobalInputMonitor();

        if (IsWindows)
            return new WindowsGlobalInputMonitor(captureService);

        throw new PlatformNotSupportedException($"Global input monitoring is not supported on {RuntimeInformation.OSDescription}.");
    }

    public static IGpuDetector CreateGpuDetector()
    {
        if (IsMacOS)
            return new MacGpuDetector();

        if (IsWindows)
            return new WindowsGpuDetector();

        return new FallbackGpuDetector();
    }

    private class FallbackGpuDetector : IGpuDetector
    {
        public IReadOnlyList<Models.GpuInfoModel> DetectGpus() =>
        [
            new Models.GpuInfoModel
            {
                Name = "Generic Display Controller",
                Type = "Integrated",
                TypeCode = 0
            }
        ];
    }
}
