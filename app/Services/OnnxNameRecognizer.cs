/**
 * OnnxNameRecognizer.cs
 * On-device person-name detection with a BERT NER model (dslim/bert-base-NER, int8 ONNX).
 *
 * Pattern rules can only recognise names next to a label ("Customer Name:") or title
 * ("Dr."). Free-standing names — a LinkedIn feed, a chat list, an email header — need a
 * model that judges from context whether "Naveen Venkat" is a person or "Optum India" a
 * company. Each OCR line is classified as one sequence; an OCR word is a name when the
 * model's B-PER + I-PER probability for its first sub-token passes the threshold.
 *
 * Runs fully offline via ONNX Runtime on macOS and Windows. InferenceSession.Run is
 * thread-safe, so concurrent capture workers can share one instance.
 */

using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ScreenGuard.App.Abstractions;

namespace ScreenGuard.App.Services;

public sealed class OnnxNameRecognizer : INameRecognizer
{
    private const int MaxSequenceLength = 128;
    private const int BatchSize = 16;
    private const float PersonThreshold = 0.7f;

    private readonly InferenceSession _session;
    private readonly BertWordPieceTokenizer _tokenizer;
    private readonly int[] _personLabelIds;
    private readonly bool _hasTokenTypeIds;

    public string Description { get; }

    /// <param name="modelDir">Directory containing model.onnx, vocab.txt and config.json.</param>
    public OnnxNameRecognizer(string modelDir)
    {
        _tokenizer = new BertWordPieceTokenizer(Path.Combine(modelDir, "vocab.txt"));

        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDir, "config.json")));
        _personLabelIds = config.RootElement.GetProperty("id2label").EnumerateObject()
            .Where(p => p.Value.GetString() is "B-PER" or "I-PER")
            .Select(p => int.Parse(p.Name))
            .ToArray();
        if (_personLabelIds.Length == 0)
            throw new InvalidDataException("NER model config has no B-PER / I-PER labels.");

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = 2 // several capture workers run in parallel
        };
        _session = new InferenceSession(Path.Combine(modelDir, "model.onnx"), options);
        _hasTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");

        Description = $"BERT NER (ONNX Runtime, {Path.GetFileName(modelDir)})";
    }

    public List<List<OcrResult>> FindNames(IReadOnlyList<List<OcrResult>> lines)
    {
        // Build one token sequence per line (long lines are chunked by word)
        var sequences = new List<(int[] Ids, int[] FirstTokenOfWord, List<OcrResult> Words)>();
        foreach (var line in lines)
        {
            var words = line.Where(w => w.Text.Any(char.IsLetter)).ToList();
            if (words.Count == 0) continue;

            var ids = new List<int> { _tokenizer.ClsId };
            var firstToken = new List<int>();
            var chunkWords = new List<OcrResult>();
            foreach (var word in words)
            {
                var pieces = _tokenizer.TokenizeWord(word.Text);
                if (pieces.Count == 0) continue;

                if (ids.Count + pieces.Count + 1 > MaxSequenceLength && chunkWords.Count > 0)
                {
                    ids.Add(_tokenizer.SepId);
                    sequences.Add((ids.ToArray(), firstToken.ToArray(), chunkWords));
                    ids = new List<int> { _tokenizer.ClsId };
                    firstToken = new List<int>();
                    chunkWords = new List<OcrResult>();
                }

                firstToken.Add(ids.Count);
                chunkWords.Add(word);
                ids.AddRange(pieces.Take(MaxSequenceLength - 2));
            }

            if (chunkWords.Count > 0)
            {
                ids.Add(_tokenizer.SepId);
                sequences.Add((ids.ToArray(), firstToken.ToArray(), chunkWords));
            }
        }

        var names = new List<List<OcrResult>>();
        for (int b = 0; b < sequences.Count; b += BatchSize)
        {
            var batch = sequences.Skip(b).Take(BatchSize).ToList();
            var personProbs = Run(batch.Select(s => s.Ids).ToList());

            for (int i = 0; i < batch.Count; i++)
            {
                var (_, firstTokens, words) = batch[i];
                List<OcrResult>? span = null;
                for (int w = 0; w < words.Count; w++)
                {
                    bool isPerson = personProbs[i][firstTokens[w]] >= PersonThreshold && LooksLikeNameWord(words[w].Text);
                    if (isPerson)
                    {
                        span ??= new List<OcrResult>();
                        span.Add(words[w]);
                    }
                    else if (span != null)
                    {
                        names.Add(span);
                        span = null;
                    }
                }
                if (span != null) names.Add(span);
            }
        }

        return names;
    }

    /// <summary>Filters model output to plausible name tokens (a capital letter, not a lone symbol).</summary>
    private static bool LooksLikeNameWord(string text)
    {
        string core = text.Trim('.', ',', ':', ';', '!', '?', '(', ')', '"', '\'', '|');
        return core.Length >= 2 && char.IsUpper(core.First(char.IsLetterOrDigit));
    }

    /// <summary>Runs one padded batch; returns P(B-PER)+P(I-PER) for every token.</summary>
    private float[][] Run(List<int[]> batch)
    {
        int maxLen = batch.Max(s => s.Length);
        var shape = new[] { batch.Count, maxLen };
        var inputIds = new DenseTensor<long>(shape);
        var attention = new DenseTensor<long>(shape);
        var tokenTypes = new DenseTensor<long>(shape);

        for (int i = 0; i < batch.Count; i++)
        {
            for (int t = 0; t < maxLen; t++)
            {
                bool real = t < batch[i].Length;
                inputIds[i, t] = real ? batch[i][t] : _tokenizer.PadId;
                attention[i, t] = real ? 1 : 0;
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attention)
        };
        if (_hasTokenTypeIds)
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypes));

        using var results = _session.Run(inputs);
        var logits = results.First().AsTensor<float>(); // [batch, seq, labels]
        int labels = logits.Dimensions[2];

        var output = new float[batch.Count][];
        var probs = new float[labels];
        for (int i = 0; i < batch.Count; i++)
        {
            output[i] = new float[maxLen];
            for (int t = 0; t < batch[i].Length; t++)
            {
                float max = float.MinValue;
                for (int l = 0; l < labels; l++) max = Math.Max(max, logits[i, t, l]);
                float sum = 0;
                for (int l = 0; l < labels; l++) { probs[l] = MathF.Exp(logits[i, t, l] - max); sum += probs[l]; }
                float person = 0;
                foreach (int id in _personLabelIds) person += probs[id];
                output[i][t] = person / sum;
            }
        }
        return output;
    }

    public void Dispose() => _session.Dispose();
}
