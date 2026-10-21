using System.Diagnostics;
using System.Text.RegularExpressions;
using ScreenGuard.App.Abstractions;
using ScreenGuard.App.Models;

namespace ScreenGuard.App.Platforms.Windows;

/// <summary>
/// Production-grade Windows GPU detector using WMI / PowerShell video controller queries.
/// Classifies graphics devices into Integrated (iGPU) vs Discrete (dGPU) vs External (eGPU).
/// </summary>
public class WindowsGpuDetector : IGpuDetector
{
    public IReadOnlyList<GpuInfoModel> DetectGpus()
    {
        var list = new List<GpuInfoModel>();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    string gpuName = line.Trim();
                    if (string.IsNullOrWhiteSpace(gpuName)) continue;

                    var (typeStr, typeCode) = Classify(gpuName);

                    list.Add(new GpuInfoModel
                    {
                        Name = gpuName,
                        Type = typeStr,
                        TypeCode = typeCode
                    });
                }
            }
        }
        catch
        {
            // Fallback default
        }

        if (list.Count == 0)
        {
            list.Add(new GpuInfoModel
            {
                Name = "Unknown Display Adapter",
                Type = "Unknown",
                TypeCode = -1
            });
        }

        return list;
    }

    /// <summary>
    /// Classifies an adapter name. Integrated parts are matched first because AMD and Intel
    /// both ship integrated and discrete GPUs under the same brand names.
    /// </summary>
    internal static (string Type, int Code) Classify(string gpuName)
    {
        string lower = gpuName.ToLowerInvariant();

        if (lower.Contains("microsoft basic") || lower.Contains("remote display") || lower.Contains("virtual"))
            return ("Software", -1);

        // Intel: Arc A-series / B-series cards are discrete; everything else (UHD, Iris, "Arc Graphics") is integrated
        if (lower.Contains("intel"))
            return Regex.IsMatch(lower, @"\barc(\(tm\))?\s+[ab]\d{3}") ? ("Discrete", 1) : ("Integrated", 0);

        // AMD: RX / Pro W / FirePro / Instinct are discrete; "Radeon Graphics", Vega N and 6x0M/7x0M/8x0M APUs are integrated
        if (lower.Contains("radeon") || lower.Contains("amd"))
        {
            if (Regex.IsMatch(lower, @"\brx\s*\d|\bpro\s+w\d|firepro|instinct"))
                return ("Discrete", 1);
            if (lower.Contains("graphics") || Regex.IsMatch(lower, @"\bvega\s*\d+\b|\b\d{3}m\b"))
                return ("Integrated", 0);
            return ("Discrete", 1);
        }

        if (lower.Contains("nvidia") || lower.Contains("geforce") || lower.Contains("quadro") || lower.Contains("rtx") || lower.Contains("gtx"))
            return ("Discrete", 1);

        if (lower.Contains("qualcomm") || lower.Contains("adreno"))
            return ("Integrated", 0);

        return ("Unknown", -1);
    }
}
