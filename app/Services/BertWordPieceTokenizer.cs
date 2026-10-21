/**
 * BertWordPieceTokenizer.cs
 * Minimal BERT (cased) WordPiece tokenizer for the on-device NER model.
 *
 * Text arrives already split into OCR words, so tokenization works per word:
 *   1. Basic split: separate punctuation from letters/digits (BERT "BasicTokenizer", cased)
 *   2. WordPiece: greedy longest-match against vocab.txt, continuation pieces prefixed "##"
 * Keeping the per-word structure lets every sub-token be mapped back to its OCR word box.
 */

using System.Globalization;
using System.Text;

namespace ScreenGuard.App.Services;

public sealed class BertWordPieceTokenizer
{
    private const int MaxCharsPerWord = 100;

    private readonly Dictionary<string, int> _vocab;

    public int ClsId { get; }
    public int SepId { get; }
    public int PadId { get; }
    public int UnkId { get; }

    public BertWordPieceTokenizer(string vocabPath)
    {
        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        int index = 0;
        foreach (var line in File.ReadLines(vocabPath))
        {
            _vocab.TryAdd(line, index);
            index++;
        }

        ClsId = Require("[CLS]");
        SepId = Require("[SEP]");
        PadId = Require("[PAD]");
        UnkId = Require("[UNK]");
    }

    private int Require(string token) =>
        _vocab.TryGetValue(token, out int id) ? id : throw new InvalidDataException($"vocab.txt is missing {token}");

    /// <summary>Token ids for one OCR word (may be several pieces).</summary>
    public List<int> TokenizeWord(string word)
    {
        var ids = new List<int>();
        foreach (var piece in BasicSplit(word))
            WordPiece(piece, ids);
        return ids;
    }

    /// <summary>Splits on punctuation and drops control characters (cased: no lowercasing).</summary>
    private static IEnumerable<string> BasicSplit(string text)
    {
        var current = new StringBuilder();
        foreach (char c in text.Normalize(NormalizationForm.FormC))
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
            }
            else if (IsPunctuation(c))
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                yield return c.ToString();
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static bool IsPunctuation(char c)
    {
        // BERT treats all non-letter/number ASCII symbols as punctuation
        if (c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~')
            return true;
        var category = char.GetUnicodeCategory(c);
        return category is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }

    private void WordPiece(string token, List<int> output)
    {
        if (token.Length > MaxCharsPerWord)
        {
            output.Add(UnkId);
            return;
        }

        var pieces = new List<int>();
        int start = 0;
        while (start < token.Length)
        {
            int end = token.Length;
            int found = -1;
            while (start < end)
            {
                string sub = token[start..end];
                if (start > 0) sub = "##" + sub;
                if (_vocab.TryGetValue(sub, out int id))
                {
                    found = id;
                    break;
                }
                end--;
            }

            if (found < 0)
            {
                output.Add(UnkId);
                return;
            }

            pieces.Add(found);
            start = end;
        }

        output.AddRange(pieces);
    }
}
