/**
 * NonMaxSuppression.cs
 * Keeps the highest-scoring box among overlapping detections.
 *
 * Detectors emit several boxes per object, and overlapping tiles see the same object twice.
 * Standard greedy NMS by intersection-over-union, plus a containment check: a box mostly
 * inside a higher-scoring box (a partial face cut at a tile edge) is dropped as well.
 */

using SkiaSharp;

namespace ScreenGuard.App.Services.Vision;

public readonly record struct ScoredBox(SKRect Box, float Score);

public static class NonMaxSuppression
{
    public static List<ScoredBox> Apply(IEnumerable<ScoredBox> boxes, float iouThreshold, float containmentThreshold = 0.7f)
    {
        var kept = new List<ScoredBox>();
        foreach (var candidate in boxes.OrderByDescending(b => b.Score))
        {
            bool suppressed = kept.Any(k =>
                IoU(k.Box, candidate.Box) > iouThreshold ||
                IntersectionOverSmaller(k.Box, candidate.Box) > containmentThreshold);
            if (!suppressed)
                kept.Add(candidate);
        }
        return kept;
    }

    public static float IoU(SKRect a, SKRect b)
    {
        float inter = IntersectionArea(a, b);
        float union = Area(a) + Area(b) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    private static float IntersectionOverSmaller(SKRect a, SKRect b)
    {
        float smaller = Math.Min(Area(a), Area(b));
        return smaller <= 0 ? 0 : IntersectionArea(a, b) / smaller;
    }

    private static float IntersectionArea(SKRect a, SKRect b)
    {
        float w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        float h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return w > 0 && h > 0 ? w * h : 0;
    }

    private static float Area(SKRect r) => Math.Max(0, r.Width) * Math.Max(0, r.Height);
}
