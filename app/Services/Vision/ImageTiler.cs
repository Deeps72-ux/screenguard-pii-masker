/**
 * ImageTiler.cs
 * Splits a large frame into overlapping, model-sized tiles at one or more scales.
 *
 * Detection models take a small fixed input (e.g. 640×480). Shrinking a 2880×1800 screenshot
 * to that size turns a 100 px avatar into a 20 px blob the model cannot see, so the frame is
 * also processed as overlapping tiles at a higher scale. Overlap guarantees that any object
 * smaller than the overlap lies wholly inside at least one tile.
 */

using SkiaSharp;

namespace ScreenGuard.App.Services.Vision;

/// <summary>A tile: the source rectangle in frame pixels that maps onto the model input.</summary>
public readonly record struct Tile(SKRectI Source);

public static class ImageTiler
{
    /// <summary>
    /// Tiles covering the frame at the given scale (1.0 = native pixels, 0.5 = half size).
    /// Each tile spans <c>inputWidth / scale</c> × <c>inputHeight / scale</c> frame pixels and
    /// neighbours overlap by <paramref name="overlap"/> (0–0.9) of a tile.
    /// </summary>
    public static List<Tile> Cover(int frameWidth, int frameHeight, int inputWidth, int inputHeight,
                                   double scale, double overlap)
    {
        if (frameWidth <= 0 || frameHeight <= 0) return [];
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        overlap = Math.Clamp(overlap, 0, 0.9);

        int tileW = Math.Min(frameWidth, (int)Math.Round(inputWidth / scale));
        int tileH = Math.Min(frameHeight, (int)Math.Round(inputHeight / scale));

        var tiles = new List<Tile>();
        foreach (int y in Starts(frameHeight, tileH, overlap))
            foreach (int x in Starts(frameWidth, tileW, overlap))
                tiles.Add(new Tile(new SKRectI(x, y, x + tileW, y + tileH)));
        return tiles;
    }

    /// <summary>One tile covering the whole frame, padded to the model's aspect ratio.</summary>
    public static Tile Whole(int frameWidth, int frameHeight, int inputWidth, int inputHeight)
    {
        double modelAspect = inputWidth / (double)inputHeight;
        int w = frameWidth, h = frameHeight;
        if (w / (double)h > modelAspect) h = (int)Math.Ceiling(w / modelAspect);
        else w = (int)Math.Ceiling(h * modelAspect);
        return new Tile(new SKRectI(0, 0, w, h)); // area beyond the frame is padded black
    }

    /// <summary>Evenly spaced start offsets whose last tile ends exactly at the edge.</summary>
    private static IEnumerable<int> Starts(int length, int tile, double overlap)
    {
        if (tile >= length)
        {
            yield return 0;
            yield break;
        }

        int step = Math.Max(1, (int)(tile * (1 - overlap)));
        int count = (int)Math.Ceiling((length - tile) / (double)step) + 1;
        double exactStep = (length - tile) / (double)(count - 1);
        for (int i = 0; i < count; i++)
            yield return (int)Math.Round(i * exactStep);
    }
}
