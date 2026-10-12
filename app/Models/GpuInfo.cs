namespace SkanCapture.App.Models;

/// <summary>
/// Managed representation of GPU device information from the native layer.
/// </summary>
public class GpuInfoModel
{
    /// <summary>GPU device name (e.g., "Apple M2", "AMD Radeon Pro 5500M").</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// GPU type classification:
    ///   "Integrated" — low-power GPU (Intel Iris, Apple M-series)
    ///   "Discrete"   — dedicated GPU (AMD Radeon, NVIDIA)
    ///   "External"   — eGPU via Thunderbolt
    /// </summary>
    public string Type { get; set; } = "";

    /// <summary>Numeric type code from native layer (0=Integrated, 1=Discrete, 2=External).</summary>
    public int TypeCode { get; set; }

    public override string ToString() => $"{Name} ({Type})";
}
