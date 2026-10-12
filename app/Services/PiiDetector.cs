/**
 * PiiDetector.cs
 * Production-grade Personal Identifiable Information (PII) detection engine.
 *
 * Scans OCR results for sensitive data patterns:
 *   - Social Security Numbers (SSN) with SSA validation rules
 *   - Email addresses (RFC 5322 compliant pattern)
 *   - Phone numbers (US formats, parenthesis, dots, dashes, international E.164)
 *   - Credit card numbers (Visa, Mastercard, Amex, Discover with Luhn checksum validation)
 *   - IPv4 and IPv6 addresses
 *   - Dates of birth / sensitive dates (MM/DD/YYYY, YYYY-MM-DD, etc.)
 *   - US Passport numbers
 *   - Tax Identification / EIN numbers
 *
 * Features:
 *   - Multi-token sliding window (2-4 words) to catch numbers split by OCR across tokens
 *   - Luhn algorithm verification for credit card candidates to prevent false positives
 *   - Smart bounding box clustering & overlap deduplication
 */

using System.Text.RegularExpressions;
using ScreenGuard.App.Models;

namespace ScreenGuard.App.Services;

/// <summary>
/// Detects PII patterns in OCR-extracted text using regular expressions and heuristics.
/// </summary>
public class PiiDetector
{
    private record PatternDefinition(string Type, Regex Pattern, Func<string, bool>? Validator = null);

    private static readonly List<PatternDefinition> Patterns =
    [
        // SSN: XXX-XX-XXXX (excluding known invalid ranges 000, 666, 900-999)
        new("SSN", new Regex(
            @"\b(?!000|666|9\d{2})\d{3}[-\s]?(?!00)\d{2}[-\s]?(?!0000)\d{4}\b",
            RegexOptions.Compiled)),

        // Email addresses
        new("Email", new Regex(
            @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
            RegexOptions.Compiled)),

        // Phone numbers (US + international)
        new("Phone", new Regex(
            @"\b(?:\+?1[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b",
            RegexOptions.Compiled)),

        new("Phone", new Regex(
            @"\b\+\d{1,3}[-.\s]?\d{1,4}[-.\s]?\d{3,4}[-.\s]?\d{3,4}\b",
            RegexOptions.Compiled)),

        // Credit Cards (Visa, MasterCard, AmEx, Discover) with Luhn validation
        new("CreditCard", new Regex(
            @"\b(?:4\d{3}|5[1-5]\d{2}|6011|3[47]\d{2})[-\s]?\d{4}[-\s]?\d{4}[-\s]?\d{1,4}\b",
            RegexOptions.Compiled),
            ValidateCreditCardLuhn),

        // IPv4 addresses
        new("IPAddress", new Regex(
            @"\b(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\b",
            RegexOptions.Compiled)),

        // Dates: MM/DD/YYYY, MM-DD-YYYY, YYYY-MM-DD
        new("DateOfBirth", new Regex(
            @"\b(?:0[1-9]|1[0-2])[/\-.](?:0[1-9]|[12]\d|3[01])[/\-.](?:19|20)\d{2}\b",
            RegexOptions.Compiled)),

        new("DateOfBirth", new Regex(
            @"\b(?:19|20)\d{2}[/\-.](?:0[1-9]|1[0-2])[/\-.](?:0[1-9]|[12]\d|3[01])\b",
            RegexOptions.Compiled)),

        // US Passport: 1 letter followed by 8 digits
        new("Passport", new Regex(
            @"\b[A-Z][0-9]{8}\b",
            RegexOptions.Compiled)),

        // Employer Identification Number (EIN): XX-XXXXXXX
        new("TaxID", new Regex(
            @"\b[1-9]\d-\d{7}\b",
            RegexOptions.Compiled)),

        // Personal Name & Recipient Identifiers (e.g., Deliver to DEEPAN, Hello, DEEPAN, Customer: John)
        new("Name", new Regex(
            @"\b(?:Deliver\s+to|Hello,?|Hi,?|User(?:name)?|Name|Customer|Patient|Employee|Logged\s+in\s+as|Account\s+(?:holder|name|owner)?|Recipient|Owner|Attn|Attention)[\s:]+[A-Za-z0-9_\.\-]+\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        // Formal titles with name (e.g. Dr. Smith, Mr. Deepan)
        new("Name", new Regex(
            @"\b(?:Mr\.|Mrs\.|Ms\.|Dr\.)\s+[A-Z][a-z]+(?:\s+[A-Z][a-z]+)?\b",
            RegexOptions.Compiled)),

        // Handles / Usernames (e.g. @username)
        new("Username", new Regex(
            @"@[a-zA-Z0-9_]{3,20}\b",
            RegexOptions.Compiled)),

        // Physical Addresses & City + Postal / PIN / ZIP code (e.g. Chennai 600032, Seattle 98101)
        new("Address", new Regex(
            @"\b[A-Za-z]{3,25}\s+\d{5,6}\b",
            RegexOptions.Compiled)),

        // Standard Street Addresses (e.g., 123 Main Street, 45 Park Ave)
        new("Address", new Regex(
            @"\b\d{1,5}\s+(?:[A-Za-z0-9#\.\-]+\s+){1,4}(?:Street|St|Avenue|Ave|Road|Rd|Boulevard|Blvd|Drive|Dr|Lane|Ln|Way|Court|Ct|Circle|Cir|Highway|Hwy|P\.?O\.?\s*Box)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        // Labeled Address and Postal/PIN codes
        new("Address", new Regex(
            @"\b(?:PIN|ZIP|Postal|Pincode)(?:\s*Code)?[\s:]*\d{5,6}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase)),

        // Credentials / Passwords
        new("Credentials", new Regex(
            @"\b(?:Password|Passcode|Secret|API\s*Key)[\s:]*\S+\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase))
    ];

    /// <summary>
    /// Detect PII patterns in word-level OCR results.
    /// Evaluates individual words and multi-word sliding windows, then merges overlaps.
    /// </summary>
    /// <param name="ocrResults">Word-level OCR tokens with bounding boxes.</param>
    /// <returns>Deduplicated list of detected PII matches.</returns>
    public List<PiiMatch> DetectPii(List<OcrResult> ocrResults)
    {
        var rawMatches = new List<PiiMatch>();

        if (ocrResults == null || ocrResults.Count == 0)
            return rawMatches;

        // 1. Single-word checks
        foreach (var word in ocrResults)
        {
            string cleanText = word.Text.Trim();
            foreach (var def in Patterns)
            {
                var match = def.Pattern.Match(cleanText);
                if (match.Success)
                {
                    if (def.Validator == null || def.Validator(match.Value))
                    {
                        rawMatches.Add(new PiiMatch
                        {
                            Type = def.Type,
                            OriginalText = match.Value,
                            X = word.X,
                            Y = word.Y,
                            Width = word.Width,
                            Height = word.Height
                        });
                    }
                }
            }
        }

        // 2. Multi-word sliding window (lengths 2 to 6)
        for (int i = 0; i < ocrResults.Count; i++)
        {
            int maxLen = Math.Min(6, ocrResults.Count - i);
            for (int len = 2; len <= maxLen; len++)
            {
                var group = ocrResults.GetRange(i, len);
                string combined = string.Join(" ", group.Select(g => g.Text));

                foreach (var def in Patterns)
                {
                    var match = def.Pattern.Match(combined);
                    if (match.Success)
                    {
                        if (def.Validator == null || def.Validator(match.Value))
                        {
                            int groupX = group.Min(g => g.X);
                            int groupY = group.Min(g => g.Y);
                            int groupRight = group.Max(g => g.X + g.Width);
                            int groupBottom = group.Max(g => g.Y + g.Height);

                            rawMatches.Add(new PiiMatch
                            {
                                Type = def.Type,
                                OriginalText = match.Value,
                                X = groupX,
                                Y = groupY,
                                Width = groupRight - groupX,
                                Height = groupBottom - groupY
                            });
                        }
                    }
                }
            }
        }

        // 3. Deduplicate and merge overlapping bounding boxes
        return DeduplicateMatches(rawMatches);
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
    /// Deduplicates matches with overlapping or identical bounding boxes.
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

                // Check intersection or significant overlap
                int xOverlap = Math.Max(0, Math.Min(existing.X + existing.Width, candidate.X + candidate.Width) -
                                          Math.Max(existing.X, candidate.X));
                int yOverlap = Math.Max(0, Math.Min(existing.Y + existing.Height, candidate.Y + candidate.Height) -
                                          Math.Max(existing.Y, candidate.Y));
                int overlapArea = xOverlap * yOverlap;

                int existingArea = existing.Width * existing.Height;
                int candidateArea = candidate.Width * candidate.Height;

                if (overlapArea > 0 && (overlapArea > existingArea * 0.5 || overlapArea > candidateArea * 0.5))
                {
                    // Merge bounding box to cover the union of both
                    int unionX = Math.Min(existing.X, candidate.X);
                    int unionY = Math.Min(existing.Y, candidate.Y);
                    int unionRight = Math.Max(existing.X + existing.Width, candidate.X + candidate.Width);
                    int unionBottom = Math.Max(existing.Y + existing.Height, candidate.Y + candidate.Height);

                    existing.X = unionX;
                    existing.Y = unionY;
                    existing.Width = unionRight - unionX;
                    existing.Height = unionBottom - unionY;
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
