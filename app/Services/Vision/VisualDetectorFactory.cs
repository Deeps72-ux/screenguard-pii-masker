/**
 * VisualDetectorFactory.cs
 * Builds the set of pixel-based PII detectors available on this machine.
 *
 * The one place that knows which visual detectors exist: adding a new one (e.g. signatures or
 * QR codes) means a new IVisualPiiDetector class and one entry here, with no change to the
 * capture pipeline. A detector whose model is missing is skipped with a warning.
 */

using ScreenGuard.App.Abstractions;

namespace ScreenGuard.App.Services.Vision;

/// <summary>Which visual detectors the user enabled.</summary>
public sealed record VisualDetectionSettings(bool Faces);

public static class VisualDetectorFactory
{
    public const string FaceModelFile = "version-RFB-640.onnx";

    /// <param name="info">Receives one line per detector that loaded.</param>
    /// <param name="warning">Receives one line per detector that is enabled but unavailable.</param>
    public static IReadOnlyList<IVisualPiiDetector> CreateAll(
        VisualDetectionSettings settings, Action<string> info, Action<string> warning)
    {
        var detectors = new List<IVisualPiiDetector>();

        if (settings.Faces)
            TryAdd(detectors, CreateFaceDetector, "Face masking", info, warning);

        return detectors;
    }

    private static IVisualPiiDetector? CreateFaceDetector()
    {
        string? dir = ModelLocator.Find("face", FaceModelFile);
        return dir == null ? null : new OnnxFaceDetector(Path.Combine(dir, FaceModelFile));
    }

    private static void TryAdd(List<IVisualPiiDetector> detectors, Func<IVisualPiiDetector?> create,
                               string feature, Action<string> info, Action<string> warning)
    {
        try
        {
            var detector = create();
            if (detector == null)
            {
                warning($"{feature} model not found (run scripts/download_models.sh); {feature.ToLowerInvariant()} is off.");
                return;
            }
            detectors.Add(detector);
            info($"{feature} initialized: {detector.Description}");
        }
        catch (Exception ex)
        {
            warning($"{feature} unavailable: {ex.Message}");
        }
    }
}
