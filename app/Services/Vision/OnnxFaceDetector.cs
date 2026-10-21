/**
 * OnnxFaceDetector.cs
 * On-device face detection with UltraFace (version-RFB-640, MIT) via ONNX Runtime.
 *
 * Pipeline per frame:
 *   1. Tiles: the whole frame (large faces) plus overlapping tiles at each configured scale
 *      (small avatars in feeds, chat lists and video calls) — see ImageTiler
 *   2. Each tile is resized to the model input (640×480), converted to RGB and normalised
 *      ((v - 127) / 128), then run through the model
 *   3. Boxes above the score threshold are mapped back to frame pixels, slightly enlarged so
 *      hair and chin are covered, and merged with non-max suppression
 */

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using ScreenGuard.App.Abstractions;
using ScreenGuard.App.Models;

namespace ScreenGuard.App.Services.Vision;

/// <summary>Tuning for <see cref="OnnxFaceDetector"/>; defaults were measured on real screenshots.</summary>
public sealed record FaceDetectorOptions
{
    /// <summary>
    /// Minimum face probability (0–1). 0.9 kept every real face in the test screenshots while
    /// rejecting statues and fractal art that scored 0.8–0.9.
    /// </summary>
    public float ScoreThreshold { get; init; } = 0.9f;

    /// <summary>IoU above which two boxes are treated as the same face.</summary>
    public float IouThreshold { get; init; } = 0.3f;

    /// <summary>Tile scales in addition to the whole-frame pass (1.0 = native pixels).</summary>
    public IReadOnlyList<double> TileScales { get; init; } = [0.5, 1.0];

    /// <summary>Fraction of a tile shared with its neighbour.</summary>
    public double TileOverlap { get; init; } = 0.25;

    /// <summary>Boxes smaller than this (frame pixels) are ignored as noise.</summary>
    public int MinFaceSize { get; init; } = 16;

    /// <summary>Fractional enlargement of each box so hair, ears and chin are covered.</summary>
    public float BoxPadding { get; init; } = 0.25f;
}

public sealed class OnnxFaceDetector : IVisualPiiDetector
{
    private const int InputWidth = 640;
    private const int InputHeight = 480;

    private readonly InferenceSession _session;
    private readonly FaceDetectorOptions _options;
    private readonly string _inputName;
    private readonly string _scoresName;
    private readonly string _boxesName;

    public string Name => "face";
    public string Description { get; }

    public OnnxFaceDetector(string modelPath, FaceDetectorOptions? options = null)
    {
        _options = options ?? new FaceDetectorOptions();

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = 2, // several capture workers run in parallel
            // The UltraFace export lists its weights as graph inputs, which makes ONNX Runtime
            // print a warning per weight; they are harmless
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        _session = new InferenceSession(modelPath, sessionOptions);

        _inputName = _session.InputMetadata.Keys.First(k => _session.InputMetadata[k].Dimensions.Length == 4);
        var dims = _session.InputMetadata[_inputName].Dimensions;
        if (dims.Length != 4 || dims[2] != InputHeight || dims[3] != InputWidth)
            throw new InvalidDataException($"Unexpected face model input shape [{string.Join(",", dims)}]; expected [1,3,{InputHeight},{InputWidth}].");

        // UltraFace outputs "scores" [1, N, 2] and "boxes" [1, N, 4]
        _scoresName = _session.OutputMetadata.Keys.First(k => _session.OutputMetadata[k].Dimensions[^1] == 2);
        _boxesName = _session.OutputMetadata.Keys.First(k => _session.OutputMetadata[k].Dimensions[^1] == 4);

        Description = $"UltraFace RFB-640 (ONNX Runtime, {Path.GetFileName(modelPath)})";
    }

    public List<PiiMatch> Detect(SKBitmap frame)
    {
        var tiles = new List<Tile> { ImageTiler.Whole(frame.Width, frame.Height, InputWidth, InputHeight) };
        foreach (double scale in _options.TileScales)
            tiles.AddRange(ImageTiler.Cover(frame.Width, frame.Height, InputWidth, InputHeight, scale, _options.TileOverlap));

        var candidates = new List<ScoredBox>();
        using var input = new SKBitmap(InputWidth, InputHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        foreach (var tile in tiles)
            candidates.AddRange(DetectInTile(frame, tile, input));

        return NonMaxSuppression.Apply(candidates, _options.IouThreshold)
            .Select(b => ToMatch(b, frame.Width, frame.Height))
            .Where(m => m.Width >= _options.MinFaceSize && m.Height >= _options.MinFaceSize)
            .ToList();
    }

    private IEnumerable<ScoredBox> DetectInTile(SKBitmap frame, Tile tile, SKBitmap input)
    {
        // Resize the tile into the model input; area outside the frame stays black
        using (var canvas = new SKCanvas(input))
        {
            canvas.Clear(SKColors.Black);
            var src = tile.Source;
            var visible = SKRectI.Intersect(src, new SKRectI(0, 0, frame.Width, frame.Height));
            float sx = InputWidth / (float)src.Width, sy = InputHeight / (float)src.Height;
            var dest = new SKRect((visible.Left - src.Left) * sx, (visible.Top - src.Top) * sy,
                                  (visible.Right - src.Left) * sx, (visible.Bottom - src.Top) * sy);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
            canvas.DrawBitmap(frame, visible, dest, paint);
        }

        var tensor = ToNormalizedRgbTensor(input);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        var scores = results.First(r => r.Name == _scoresName).AsTensor<float>();
        var boxes = results.First(r => r.Name == _boxesName).AsTensor<float>();

        var found = new List<ScoredBox>();
        int count = scores.Dimensions[1];
        for (int i = 0; i < count; i++)
        {
            float score = scores[0, i, 1];
            if (score < _options.ScoreThreshold) continue;

            // Boxes are normalised to the model input; map back to frame pixels
            var s = tile.Source;
            found.Add(new ScoredBox(new SKRect(
                s.Left + boxes[0, i, 0] * s.Width,
                s.Top + boxes[0, i, 1] * s.Height,
                s.Left + boxes[0, i, 2] * s.Width,
                s.Top + boxes[0, i, 3] * s.Height), score));
        }
        return found;
    }

    private static unsafe DenseTensor<float> ToNormalizedRgbTensor(SKBitmap bgra)
    {
        var tensor = new DenseTensor<float>([1, 3, InputHeight, InputWidth]);
        var span = tensor.Buffer.Span;
        int plane = InputWidth * InputHeight;
        byte* pixels = (byte*)bgra.GetPixels();
        int stride = bgra.RowBytes;

        for (int y = 0; y < InputHeight; y++)
        {
            byte* row = pixels + (long)y * stride;
            for (int x = 0; x < InputWidth; x++)
            {
                int i = y * InputWidth + x;
                span[i] = (row[x * 4 + 2] - 127f) / 128f;             // R
                span[plane + i] = (row[x * 4 + 1] - 127f) / 128f;     // G
                span[2 * plane + i] = (row[x * 4] - 127f) / 128f;     // B
            }
        }
        return tensor;
    }

    private PiiMatch ToMatch(ScoredBox box, int frameWidth, int frameHeight)
    {
        var r = box.Box;
        float padX = r.Width * _options.BoxPadding, padY = r.Height * _options.BoxPadding;
        int left = (int)Math.Clamp(r.Left - padX, 0, frameWidth);
        int top = (int)Math.Clamp(r.Top - padY, 0, frameHeight);
        int right = (int)Math.Clamp(r.Right + padX, 0, frameWidth);
        int bottom = (int)Math.Clamp(r.Bottom + padY, 0, frameHeight);

        return new PiiMatch
        {
            Type = "Face",
            OriginalText = $"face {box.Score:P0}",
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top
        };
    }

    public void Dispose() => _session.Dispose();
}
