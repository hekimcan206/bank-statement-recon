using System.Text;
using System.Text.RegularExpressions;

namespace BankRecon.Core.Statements.Mt940;

/// <summary>Which convention a bank used to structure the MT940 :86: field.</summary>
public enum Narrative86Style
{
    /// <summary>Unstructured text, as many banks (Turkish banks among them) send it.</summary>
    FreeText,

    /// <summary>German GVC style: a 3-digit business transaction code followed by ?NN subfields (?20-?29 purpose).</summary>
    QuestionMarkSubfields,

    /// <summary>SEPA keyword style common in the Benelux: /NAME/.../REMI/.../EREF/...</summary>
    SlashKeywords,
}

/// <summary>What could be extracted from an :86: field. The raw text is kept on the entry regardless.</summary>
public sealed partial record Narrative86(
    Narrative86Style Style,
    string? Remittance,
    string? CounterpartyName,
    string? CounterpartyIban,
    string? EndToEndId,
    string? PostingText)
{
    private static readonly HashSet<string> SlashKeys = new(StringComparer.Ordinal)
    {
        "TRTP", "IBAN", "BIC", "NAME", "REMI", "EREF", "CSID", "MARF", "ORDP", "BENM", "ADDR", "ID", "PREF", "KREF",
        "RTRN", "ULTD", "ULTC", "ULTB", "PURP", "CDTRREF", "CDTRREFTP", "ISSR", "FEES", "OCMT", "COAM", "CODE",
    };

    // SEPA identifiers inside the GVC purpose lines (?20-?29), for example "EREF+INV123" and "SVWZ+payment text".
    private static readonly string[] SepaPrefixes = ["EREF+", "KREF+", "MREF+", "CRED+", "DEBT+", "SVWZ+", "ABWA+", "ABWE+"];

    /// <summary>
    /// Detects the style and extracts what it can from the field with its physical lines already joined (GVC
    /// subfields and SEPA keywords may wrap anywhere, so they can only be parsed from the joined text).
    /// </summary>
    public static Narrative86 Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var text = raw.Trim();
        if (IsQuestionMarkStyle(text))
        {
            return ParseQuestionMark(text);
        }

        if (text.StartsWith('/') && TryParseSlash(text, out var slash))
        {
            return slash;
        }

        return new Narrative86(Narrative86Style.FreeText, text.Length == 0 ? null : text, null, FindIban(text), null, null);
    }

    private static bool IsQuestionMarkStyle(string text)
    {
        // "166?00..." (GVC code first) or a field that starts directly with a subfield.
        var start = text.Length >= 4 && SwiftText.IsDigits(text.AsSpan(0, 3)) && text[3] == '?' ? 3 : 0;
        return text.Length >= start + 3 && text[start] == '?' && char.IsAsciiDigit(text[start + 1]) &&
               char.IsAsciiDigit(text[start + 2]);
    }

    private static Narrative86 ParseQuestionMark(string text)
    {
        var purpose = new StringBuilder();
        var name = new StringBuilder();
        string? iban = null;
        string? posting = null;
        var i = text.IndexOf('?', StringComparison.Ordinal);
        while (i >= 0 && i + 2 < text.Length)
        {
            var next = NextSubfield(text, i + 3);
            var code = text.AsSpan(i + 1, 2);
            var value = text[(i + 3)..(next < 0 ? text.Length : next)];
            switch (code)
            {
                case "00":
                    posting = value;
                    break;
                case "31" or "38":
                    iban = Iban.Normalize(value) ?? iban;
                    break;
                case "32" or "33":
                    name.Append(value);
                    break;
                default:
                    if ((code[0] == '2' || code[0] == '6') && char.IsAsciiDigit(code[1]))
                    {
                        purpose.Append(value);
                    }

                    break;
            }

            i = next;
        }

        var purposeText = purpose.ToString();
        var endToEnd = SepaValue(purposeText, "EREF+");
        var remittance = SepaValue(purposeText, "SVWZ+") ?? (endToEnd is null ? NullIfEmpty(purposeText) : null);
        return new Narrative86(
            Narrative86Style.QuestionMarkSubfields,
            remittance,
            NullIfEmpty(name.ToString()),
            iban,
            endToEnd,
            posting);
    }

    private static int NextSubfield(string text, int from)
    {
        for (var i = from; i + 2 < text.Length; i++)
        {
            if (text[i] == '?' && char.IsAsciiDigit(text[i + 1]) && char.IsAsciiDigit(text[i + 2]))
            {
                return i;
            }
        }

        return -1;
    }

    private static string? SepaValue(string purpose, string prefix)
    {
        var start = purpose.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += prefix.Length;
        var end = purpose.Length;
        foreach (var other in SepaPrefixes)
        {
            var at = purpose.IndexOf(other, start, StringComparison.Ordinal);
            if (at >= 0 && at < end)
            {
                end = at;
            }
        }

        return NullIfEmpty(purpose[start..end].Trim());
    }

    private static bool TryParseSlash(string text, out Narrative86 result)
    {
        result = null!;
        var tokens = text.Split('/');
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentKey = null;
        var current = new StringBuilder();
        var keys = 0;

        // tokens[0] is the empty string before the leading slash.
        for (var t = 1; t < tokens.Length; t++)
        {
            var token = tokens[t];
            if (SlashKeys.Contains(token))
            {
                Flush();
                currentKey = token;
                keys++;
                continue;
            }

            if (currentKey is null)
            {
                return false;
            }

            if (current.Length > 0)
            {
                current.Append('/');
            }

            current.Append(token);
        }

        Flush();
        if (keys == 0)
        {
            return false;
        }

        var remittance = values.GetValueOrDefault("REMI");
        if (remittance is not null && remittance.StartsWith("USTD", StringComparison.Ordinal))
        {
            remittance = remittance[4..].TrimStart('/');
        }

        result = new Narrative86(
            Narrative86Style.SlashKeywords,
            NullIfEmpty(remittance?.TrimEnd('/')),
            NullIfEmpty(values.GetValueOrDefault("NAME")),
            Iban.Normalize(values.GetValueOrDefault("IBAN")),
            NullIfEmpty(values.GetValueOrDefault("EREF")),
            NullIfEmpty(values.GetValueOrDefault("TRTP")));
        return true;

        void Flush()
        {
            if (currentKey is not null)
            {
                values.TryAdd(currentKey, current.ToString());
            }

            current.Clear();
        }
    }

    private static string? FindIban(string text)
    {
        foreach (Match match in IbanPattern().Matches(text))
        {
            var iban = Iban.Normalize(match.Value);
            if (iban is not null)
            {
                return iban;
            }
        }

        return null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex(@"\b[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex IbanPattern();
}
