/**
 * PiiDetector.cs
 * Production-grade Personal Identifiable Information (PII) detection engine.
 *
 * Scans OCR results for sensitive data patterns:
 *   - Social Security Numbers (SSN) with SSA validation rules
 *   - Email addresses (RFC 5322 compliant pattern)
 *   - Phone numbers (US formats, parenthesis, dots, dashes, international E.164)
 *   - Credit card numbers (Visa, Mastercard, Amex, Discover with Luhn checksum validation)
 *   - IPv4 addresses
 *   - Dates of birth / sensitive dates (MM/DD/YYYY, YYYY-MM-DD, etc.)
 *   - US Passport numbers
 *   - Tax Identification / EIN numbers
 *
 * Features:
 *   - Line-scoped matching: OCR words are grouped into text lines and every pattern runs
 *     over the full line, so a value split across several OCR tokens is still found but a
 *     match can never span two lines
 *   - Precise boxes: each match is mapped back to only the OCR words it covers; patterns
 *     with a named "v" group mask the value and leave the label (e.g. "SSN:") visible
 *   - Luhn algorithm verification for credit card candidates to prevent false positives
 *   - Context validators to suppress common false positives (version numbers, invoice ids)
 *   - Label fallback: a line with a known PII label ("Email:", "Passport:") whose value
 *     was not recognised (e.g. garbled by a text selection) still has its value masked
 *   - Optional on-device NER (INameRecognizer) for person names with no label or title
 *   - Same-line overlap deduplication
 */

using System.Text;
using System.Text.RegularExpressions;
using ScreenGuard.App.Abstractions;
using ScreenGuard.App.Models;

namespace ScreenGuard.App.Services;

/// <summary>
/// Detects PII patterns in OCR-extracted text using regular expressions and heuristics.
/// </summary>
public class PiiDetector : IPiiDetector
{
    private readonly INameRecognizer? _nameRecognizer;

    /// <param name="nameRecognizer">Optional NER model for free-standing person names.</param>
    public PiiDetector(INameRecognizer? nameRecognizer = null)
    {
        _nameRecognizer = nameRecognizer;
    }

    /// <summary>
    /// A PII pattern. If the regex defines a named group "v", only that group is masked.
    /// The validator receives the full line text and the match, so it can inspect context.
    /// </summary>
    private record PatternDefinition(string Type, Regex Pattern, Func<string, Match, bool>? Validator = null);

    private const RegexOptions Opts = RegexOptions.Compiled;
    private const RegexOptions OptsI = RegexOptions.Compiled | RegexOptions.IgnoreCase;

    // Words that commonly precede a 5-6 digit number without it being a postal code.
    private static readonly HashSet<string> NonAddressWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "invoice", "order", "ticket", "ref", "reference", "case", "id", "no", "number", "account",
        "page", "item", "items", "version", "build", "error", "code", "port", "total", "amount",
        "qty", "quantity", "line", "row", "issue", "pr", "commit", "job", "run", "step", "pid",
        "size", "bytes", "kb", "mb", "gb", "files", "file", "words", "tokens", "year", "step"
    };

    private static readonly Regex VersionContext = new(
        @"\b(?:v|ver|version|build|release|rev|revision|sdk|api)\.?\s*[:#]?\s*$", OptsI);

    /// <summary>
    /// Field labels that announce a PII value. When a line contains one of these followed by
    /// a colon but no pattern matched its value, everything after the label is masked anyway.
    /// </summary>
    private static readonly List<(string Type, Regex Label)> FieldLabels =
    [
        ("SSN", new Regex(@"\b(?:SSN|Social\s+Security(?:\s+(?:No\.?|Number|#))?)\s*:", OptsI)),
        ("Email", new Regex(@"\b(?:E-?mail(?:\s+Address)?)\s*:", OptsI)),
        ("Phone", new Regex(@"\b(?:Phone|Mobile|Cell|Tel(?:ephone)?|Contact\s+(?:Phone|Number))\s*:", OptsI)),
        ("CreditCard", new Regex(@"\b(?:Credit\s*Card|Debit\s*Card|Card\s*(?:No\.?|Number|#))\s*:", OptsI)),
        ("IPAddress", new Regex(@"\b(?:(?:Server\s+|Client\s+)?IP(?:\s+Address)?)\s*:", OptsI)),
        ("DateOfBirth", new Regex(@"\b(?:Date\s+of\s+Birth|DOB|Birth\s*date)\s*:", OptsI)),
        ("Passport", new Regex(@"\b(?:Passport(?:\s+(?:No\.?|Number|ID))?)\s*:", OptsI)),
        ("TaxID", new Regex(@"\b(?:EIN|TIN|Tax\s+ID)\s*:", OptsI)),
        ("Address", new Regex(@"\b(?:(?:Home\s+|Street\s+|Mailing\s+)?Address)\s*:", OptsI)),
        // "...sword:" also catches a password label whose first letters OCR garbled
        ("Credentials", new Regex(@"(?:\b(?:Passcode|Passwd|API\s*Key)|\S{0,4}sword)\s*:", OptsI)),
        ("Name", new Regex(@"\b(?:(?:Customer|Patient|Employee|Client|Full|First|Last)\s+Name|Account\s+Holder)\s*:", OptsI)),
        ("JobTitle", new Regex(@"\b(?:Job\s+Title|Position|Designation|Occupation)\s*:", OptsI)),

        // Labels clipped by the left screen edge ("ssport:", "b Title:"): only at line start
        ("Passport", new Regex(@"^\S{0,4}ssport(?:\s+(?:No\.?|Number|ID))?\s*:", OptsI)),
        ("Email", new Regex(@"^\S{0,3}mail(?:\s+Address)?\s*:", OptsI)),
        ("Phone", new Regex(@"^\S{0,3}hone\s*:", OptsI)),
        ("Address", new Regex(@"^\S{0,4}dress\s*:", OptsI)),
        ("DateOfBirth", new Regex(@"^(?:\S{0,4}\s+)?of\s+Birth\s*:", OptsI)),
        ("CreditCard", new Regex(@"^\S{0,5}\s+Card\s*:", OptsI)),
        ("IPAddress", new Regex(@"^\S{0,5}\s+IP\s*:", OptsI)),
        ("JobTitle", new Regex(@"^\S{1,3}\s+Title\s*:", OptsI)),
    ];

    private static readonly List<PatternDefinition> Patterns =
    [
        // SSN with an explicit label: allows an unseparated 9-digit value
        new("SSN", new Regex(
            @"\b(?:SSN|Social\s+Security(?:\s+(?:No\.?|Number|#))?)\s*[:#]?\s*(?<v>\d{3}[-\s]?\d{2}[-\s]?\d{4})\b",
            OptsI)),

        // SSN without a label: separators required, so arbitrary 9-digit ids are not flagged
        new("SSN", new Regex(
            @"(?<![\d-])(?!000|666|9\d{2})\d{3}[-\s](?!00)\d{2}[-\s](?!0000)\d{4}(?![\d-])",
            Opts)),

        // Email addresses
        new("Email", new Regex(
            @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
            Opts)),

        // Phone numbers (US + international)
        new("Phone", new Regex(
            @"(?<![\d\w])(?:\+\d{1,3}[ \-.]?)?(?:\([0-9]{3}\)|[0-9]{3})[ \-.]?[0-9]{3}[ \-.]?[0-9]{4}(?!\d)",
            Opts)),

        new("Phone", new Regex(
            @"(?<![\d\w])\+\d{1,3}[-.\s]?\d{1,4}[-.\s]?\d{3,4}[-.\s]?\d{3,4}(?!\d)",
            Opts)),

        // Labelled card number: masked even if the checksum fails (typos, test or partial numbers)
        new("CreditCard", new Regex(
            @"\b(?:Credit\s*Card|Debit\s*Card|Card\s*(?:No\.?|Number|#))\s*[:#]?\s*(?<v>\d{4}[-\s]?\d{4,6}[-\s]?\d{4,5}(?:[-\s]?\d{1,4})?)(?!\d)",
            OptsI)),

        // Three 4-digit groups with a card prefix, followed by a group OCR misread as
        // digit look-alikes (e.g. "lllﬂ" under the text caret): masked without a checksum
        new("CreditCard", new Regex(
            @"(?<!\d)(?:4\d{3}|5[1-5]\d{2}|2[2-7]\d{2}|6011|3[47]\d{2})[-\s]\d{4}[-\s]\d{4}[-\s](?=[\dlIi|!oOSsBZzﬂﬁ]*[^\d\s])[\dlIi|!oOSsBZzﬂﬁ]{1,6}(?!\S)",
            Opts)),

        // Unlabelled Credit Cards (Visa, MasterCard, AmEx, Discover) with Luhn validation
        new("CreditCard", new Regex(
            @"(?<!\d)(?:4\d{3}|5[1-5]\d{2}|2[2-7]\d{2}|6011|3[47]\d{2})[-\s]?\d{4}[-\s]?\d{4}[-\s]?\d{1,4}(?!\d)",
            Opts),
            (_, m) => ValidateCreditCardLuhn(m.Value)),

        // IPv4 addresses (not when the context says it is a version number)
        new("IPAddress", new Regex(
            @"(?<![\d.])(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.(?:25[0-5]|2[0-4]\d|[01]?\d\d?)(?![\d.])",
            Opts),
            (line, m) => !VersionContext.IsMatch(line[..m.Index])),

        // Dates: MM/DD/YYYY, MM-DD-YYYY, YYYY-MM-DD
        new("DateOfBirth", new Regex(
            @"\b(?:0[1-9]|1[0-2])[/\-.](?:0[1-9]|[12]\d|3[01])[/\-.](?:19|20)\d{2}\b",
            Opts)),

        new("DateOfBirth", new Regex(
            @"\b(?:19|20)\d{2}[/\-.](?:0[1-9]|1[0-2])[/\-.](?:0[1-9]|[12]\d|3[01])\b",
            Opts)),

        // US Passport: 1 letter followed by 8 digits
        new("Passport", new Regex(
            @"\b[A-Z][0-9]{8}\b",
            Opts)),

        // Employer Identification Number (EIN): XX-XXXXXXX
        new("TaxID", new Regex(
            @"\b[1-9]\d-\d{7}\b",
            Opts)),

        // Personal Name & Recipient Identifiers (e.g., Deliver to DEEPAN, Customer: John, Employee Name: Alexander Hamilton)
        new("Name", new Regex(
            @"\b(?:(?:Employee|Patient|Customer|Client|User(?:name)?|Full)?\s*Name|Account\s+(?:Holder|Name|Owner)|Deliver\s+to|Logged\s+in\s+as|Attn|Attention|Recipient|Owner)[\s:]+(?<v>[A-Za-z][a-zA-Z'\-]*(?:\.[ \t]+|\.[ \t]*|[ \t]+)(?:[A-Z]\.?[ \t]+)?[A-Z][a-zA-Z'\-]*|[A-Za-z][a-zA-Z'\-]*)\b",
            OptsI)),

        new("Name", new Regex(
            @"\b(?:Customer|Patient|Employee|User|Client|Recipient|Owner|Contact)[\s]*:[\s]*(?<v>[A-Za-z][a-zA-Z'\-]*(?:[ \t]+[A-Za-z][a-zA-Z'\-]*|[ \t]+[A-Za-z]\.)*)\b",
            OptsI)),

        // Social "X & 8 other connections" / "X and 3 others liked this" phrasing
        new("Name", new Regex(
            @"\b(?<v>[A-Z][a-z]+(?:\s+[A-Z][a-z]+)?)\s+(?:&|and)\s+\d+\s+others?\b",
            Opts)),

        // Formal titles with name (e.g. Dr. Smith, Mr. Deepan, Dr. Robert J. Oppenheimer, Prof. P. V. Rathish Kumar)
        new("Name", new Regex(
            @"\b(?:Mr\.|Mrs\.|Ms\.|Dr\.|Prof\.)\s+(?<v>(?:[A-Z]\.\s*)*[A-Z][a-zA-Z'\-]*(?:[ \t]+[A-Z]\.)*(?:[ \t]+[A-Z][a-zA-Z'\-]+)*)\b",
            Opts)),

        // Job Titles and Professional Designations (e.g., Job Title: Chief Financial Architect)
        new("JobTitle", new Regex(
            @"\b(?:Job\s+Title|Position|Role|Designation|Occupation)\s*:\s*(?<v>[A-Za-z][a-zA-Z'\-]*(?:[ \t]+[A-Za-z][a-zA-Z'\-]*)*)\b",
            OptsI)),

        // Handles / Usernames (e.g. @username) — not the domain part of an email address
        new("Username", new Regex(
            @"(?<![A-Za-z0-9._%+\-])@[a-zA-Z0-9_]{3,20}\b",
            Opts)),

        // City + Postal / PIN / ZIP code (e.g. Chennai 600032, Seattle 98101)
        new("Address", new Regex(
            @"\b(?<city>[A-Z][A-Za-z]{2,24})\s+(?<v>\d{5,6})\b",
            Opts),
            (_, m) => !NonAddressWords.Contains(m.Groups["city"].Value)),

        // Standard Street Addresses (e.g., 123 Main Street, 45 Park Ave)
        new("Address", new Regex(
            @"\b\d{1,5}\s+(?:[A-Za-z0-9#\.\-]+\s+){1,4}(?:Street|St|Avenue|Ave|Road|Rd|Boulevard|Blvd|Drive|Dr|Lane|Ln|Way|Court|Ct|Circle|Cir|Highway|Hwy|P\.?O\.?\s*Box)\b",
            OptsI)),

        // Labeled Address and Postal/PIN codes
        new("Address", new Regex(
            @"\b(?:PIN|ZIP|Postal|Pincode)(?:\s*Code)?[\s:]*(?<v>\d{5,6})\b",
            OptsI)),

        // Credentials / Passwords
        new("Credentials", new Regex(
            @"\b(?:Password|Passcode|Passwd|Secret|API\s*Key|Token)\s*[:=]\s*(?<v>\S+)",
            OptsI))
    ];

    /// <summary>
    /// Detect PII patterns in word-level OCR results.
    /// Groups words into lines, matches each pattern over every full line, then merges overlaps.
    /// </summary>
    /// <param name="ocrResults">Word-level OCR tokens with bounding boxes.</param>
    /// <returns>Deduplicated list of detected PII matches.</returns>
    public List<PiiMatch> DetectPii(List<OcrResult> ocrResults)
    {
        var rawMatches = new List<PiiMatch>();

        if (ocrResults == null || ocrResults.Count == 0)
            return rawMatches;

        var lines = GroupIntoLines(ocrResults);
        foreach (var line in lines)
        {
            // Build the line text and remember each word's character span
            var sb = new StringBuilder();
            var spans = new List<(int Start, int End, OcrResult Word)>(line.Count);
            foreach (var word in line)
            {
                if (sb.Length > 0) sb.Append(' ');
                int start = sb.Length;
                sb.Append(word.Text);
                spans.Add((start, sb.Length, word));
            }
            string text = sb.ToString();

            foreach (var def in Patterns)
            {
                foreach (Match match in def.Pattern.Matches(text))
                {
                    if (def.Validator != null && !def.Validator(text, match))
                        continue;

                    var valueGroup = match.Groups["v"];
                    int from = valueGroup.Success ? valueGroup.Index : match.Index;
                    int to = valueGroup.Success ? valueGroup.Index + valueGroup.Length : match.Index + match.Length;
                    string value = valueGroup.Success ? valueGroup.Value : match.Value;

                    var covered = spans.Where(s => s.Start < to && s.End > from).Select(s => s.Word).ToList();
                    if (covered.Count > 0)
                        rawMatches.Add(BoxFor(def.Type, value, covered));
                }
            }

            ApplyLabelFallback(text, spans, ocrResults, rawMatches);
        }

        if (_nameRecognizer != null)
        {
            foreach (var name in _nameRecognizer.FindNames(lines))
                rawMatches.Add(BoxFor("Name", string.Join(" ", name.Select(w => w.Text)), name));
        }

        return DeduplicateMatches(rawMatches);
    }

    /// <summary>
    /// For each PII label on the line whose value region has no match yet, mask the words
    /// after the label. Catches values OCR garbled (selection highlight, overlapping UI).
    /// </summary>
    private static void ApplyLabelFallback(string text, List<(int Start, int End, OcrResult Word)> spans,
        List<OcrResult> allWords, List<PiiMatch> matches)
    {
        foreach (var (type, label) in FieldLabels)
        {
            foreach (Match m in label.Matches(text))
            {
                int labelEnd = m.Index + m.Length;

                // The value runs until the next label on the same line (or the end of the line)
                int valueEnd = text.Length;
                foreach (var (_, other) in FieldLabels)
                {
                    var next = other.Match(text, labelEnd);
                    if (next.Success && next.Index < valueEnd)
                        valueEnd = next.Index;
                }

                var valueWords = spans.Where(s => s.Start >= labelEnd && s.End <= valueEnd).Select(s => s.Word).ToList();
                PiiMatch candidate;
                if (valueWords.Count > 0)
                {
                    candidate = BoxFor(type, string.Join(" ", valueWords.Select(w => w.Text)), valueWords);
                }
                else
                {
                    // OCR read the label but no value at all: mask the space to its right
                    var labelWords = spans.Where(s => s.Start < labelEnd && s.End > m.Index).Select(s => s.Word).ToList();
                    if (labelWords.Count == 0 || valueEnd < text.Length)
                        continue;
                    candidate = BoxRightOfLabel(type, labelWords, allWords);
                }

                int left = candidate.X;
                int right = candidate.X + candidate.Width;
                int top = candidate.Y;
                int bottom = candidate.Y + candidate.Height;
                bool alreadyMasked = matches.Any(x =>
                    x.X <= left + 2 && x.X + x.Width >= right - 2 &&
                    Math.Min(x.Y + x.Height, bottom) - Math.Max(x.Y, top) > 0);
                if (alreadyMasked)
                    continue;

                matches.Add(candidate);
            }
        }
    }

    /// <summary>
    /// Box for an unread value: starts just right of the label and extends as far as the
    /// text on neighbouring lines (at least 15 line-heights).
    /// </summary>
    private static PiiMatch BoxRightOfLabel(string type, List<OcrResult> labelWords, List<OcrResult> allWords)
    {
        int top = labelWords.Min(w => w.Y);
        int bottom = labelWords.Max(w => w.Y + w.Height);
        int height = Math.Max(1, bottom - top);
        int x = labelWords.Max(w => w.X + w.Width) + height / 2;
        int labelLeft = labelWords.Min(w => w.X);

        int neighbourRight = allWords
            .Where(w => Math.Abs(w.Y - top) <= height * 4 && w.X + w.Width > labelLeft && w.X < x + height * 40)
            .Select(w => w.X + w.Width)
            .DefaultIfEmpty(x)
            .Max();
        int right = Math.Max(neighbourRight, x + height * 15);

        return new PiiMatch { Type = type, OriginalText = "", X = x, Y = top, Width = right - x, Height = height };
    }

    private static PiiMatch BoxFor(string type, string value, List<OcrResult> words)
    {
        int x = words.Min(w => w.X);
        int y = words.Min(w => w.Y);
        return new PiiMatch
        {
            Type = type,
            OriginalText = value,
            X = x,
            Y = y,
            Width = words.Max(w => w.X + w.Width) - x,
            Height = words.Max(w => w.Y + w.Height) - y
        };
    }

    /// <summary>
    /// Groups OCR words into text lines, ordered top-to-bottom and left-to-right.
    /// Uses the OCR engine's own line ids when present, otherwise vertical overlap.
    /// A large horizontal gap starts a new line so side-by-side columns stay separate.
    /// </summary>
    internal static List<List<OcrResult>> GroupIntoLines(List<OcrResult> words)
    {
        var lines = new List<List<OcrResult>>();

        if (words.All(w => w.LineId >= 0))
        {
            lines.AddRange(words.GroupBy(w => w.LineId).Select(g => g.ToList()));
        }
        else
        {
            foreach (var word in words.OrderBy(w => w.Y + w.Height / 2.0))
            {
                var line = lines.FirstOrDefault(l => BelongsToLine(l, word));
                if (line == null)
                {
                    line = new List<OcrResult>();
                    lines.Add(line);
                }
                line.Add(word);
            }
        }

        // Split each line where the horizontal gap is much larger than the text height
        var result = new List<List<OcrResult>>();
        foreach (var line in lines)
        {
            var sorted = line.OrderBy(w => w.X).ToList();
            int medianHeight = Math.Max(1, sorted.Select(w => w.Height).OrderBy(h => h).ElementAt(sorted.Count / 2));
            var current = new List<OcrResult> { sorted[0] };
            int rightEdge = sorted[0].X + sorted[0].Width;
            for (int i = 1; i < sorted.Count; i++)
            {
                // Measure from the furthest right edge so far: a small token nested inside a
                // wider one (OCR noise, cursor fragments) must not open a fake column gap
                int gap = sorted[i].X - rightEdge;
                if (gap > medianHeight * 4)
                {
                    result.Add(current);
                    current = new List<OcrResult>();
                }
                current.Add(sorted[i]);
                rightEdge = Math.Max(rightEdge, sorted[i].X + sorted[i].Width);
            }
            result.Add(current);
        }

        return result.OrderBy(l => l.Min(w => w.Y)).ThenBy(l => l.Min(w => w.X)).ToList();
    }

    /// <summary>
    /// A word joins a line when its vertical centre lies within the line's typical text band
    /// and its height is comparable. Comparing against the line's median word (not its
    /// min/max extent) stops one tall OCR token from stretching a line across several rows.
    /// </summary>
    private static bool BelongsToLine(List<OcrResult> line, OcrResult word)
    {
        var heights = line.Select(w => w.Height).OrderBy(h => h).ToList();
        int lineHeight = Math.Max(1, heights[heights.Count / 2]);
        var centres = line.Select(w => w.Y + w.Height / 2.0).OrderBy(c => c).ToList();
        double lineCentre = centres[centres.Count / 2];
        double wordCentre = word.Y + word.Height / 2.0;

        double ratio = word.Height / (double)lineHeight;
        if (ratio is >= 0.4 and <= 2.5)
            return Math.Abs(wordCentre - lineCentre) <= Math.Min(word.Height, lineHeight) * 0.6;

        // An over-tall token (glyph merged with the cursor or an icon) still belongs to the
        // line whose text its top edge lines up with; it never widens the line's band
        if (ratio > 2.5)
        {
            double lineTop = lineCentre - lineHeight / 2.0;
            return Math.Abs(word.Y - lineTop) <= lineHeight * 0.5;
        }
        return false;
    }

    /// <summary>
    /// Validates a credit card number using Luhn's algorithm (mod 10).
    /// </summary>
    private static bool ValidateCreditCardLuhn(string number)
    {
        string digits = Regex.Replace(number, @"[^\d]", "");
        if (digits.Length < 13 || digits.Length > 19)
            return false;

        int sum = 0;
        bool alternate = false;

        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int n = digits[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9)
                    n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }

        return (sum % 10 == 0);
    }

    /// <summary>
    /// Merges matches whose boxes overlap on the same text line.
    /// Boxes from different lines are never merged, so a merged box stays one line tall.
    /// </summary>
    private static List<PiiMatch> DeduplicateMatches(List<PiiMatch> matches)
    {
        var deduplicated = new List<PiiMatch>();

        foreach (var candidate in matches)
        {
            bool isDuplicate = false;
            for (int i = 0; i < deduplicated.Count; i++)
            {
                var existing = deduplicated[i];

                int xOverlap = Math.Min(existing.X + existing.Width, candidate.X + candidate.Width) -
                               Math.Max(existing.X, candidate.X);
                int yOverlap = Math.Min(existing.Y + existing.Height, candidate.Y + candidate.Height) -
                               Math.Max(existing.Y, candidate.Y);
                int minHeight = Math.Max(1, Math.Min(existing.Height, candidate.Height));

                bool sameLine = yOverlap >= minHeight * 0.5;
                if (sameLine && xOverlap > 0)
                {
                    int unionX = Math.Min(existing.X, candidate.X);
                    int unionY = Math.Min(existing.Y, candidate.Y);
                    existing.Width = Math.Max(existing.X + existing.Width, candidate.X + candidate.Width) - unionX;
                    existing.Height = Math.Max(existing.Y + existing.Height, candidate.Y + candidate.Height) - unionY;
                    existing.X = unionX;
                    existing.Y = unionY;
                    if (candidate.OriginalText.Length > existing.OriginalText.Length)
                    {
                        existing.OriginalText = candidate.OriginalText;
                        existing.Type = candidate.Type;
                    }

                    isDuplicate = true;
                    break;
                }
            }

            if (!isDuplicate)
            {
                deduplicated.Add(candidate);
            }
        }

        return deduplicated;
    }
}
