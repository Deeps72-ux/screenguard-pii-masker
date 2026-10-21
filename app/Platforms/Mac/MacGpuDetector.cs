using ScreenGuard.App.Abstractions;
using ScreenGuard.App.Interop;
using ScreenGuard.App.Models;

namespace ScreenGuard.App.Platforms.Mac;

/// <summary>
/// macOS GPU detector using Apple Metal API via libscreencapture.dylib.
/// </summary>
public class MacGpuDetector : IGpuDetector
{
    public IReadOnlyList<GpuInfoModel> DetectGpus()
    {
        var list = new List<GpuInfoModel>();
        try
        {
            int count = NativeInterop.ScreenGuardGetGpuCount();
            for (int i = 0; i < count; i++)
            {
                if (NativeInterop.ScreenGuardGetGpuInfo(i, out var info) == 0)
                {
                    string typeStr = info.Type switch
                    {
                        0 => "Integrated",
                        1 => "Discrete",
                        2 => "External",
                        _ => "Unknown"
                    };

                    list.Add(new GpuInfoModel
                    {
                        Name = info.Name,
                        Type = typeStr,
                        TypeCode = info.Type
                    });
                }
            }
        }
        catch
        {
            // Fallback if native library is unavailable
        }

        return list;
    }
}
