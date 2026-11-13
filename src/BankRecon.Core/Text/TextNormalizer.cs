using System.Text;

namespace BankRecon.Core.Text;

/// <summary>
/// Culture-independent folding for matching. Bank systems disagree about Turkish letters: one sends "ŞİŞLİ", the
/// next "SISLI", a third "şişli", and culture-sensitive case mapping turns "I" into "ı" under tr-TR but into "i"
/// elsewhere. Folding every variant of i/ı/İ/I to 'i' (and ş to s, ğ to g, ...) makes the comparison the same on
/// every machine and for every bank.
/// </summary>
public static class TextNormalizer
{
    /// <summary>Folds to lower-case ASCII letters and digits; every other character becomes a single space.</summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = true;
        foreach (var c in value)
        {
            if (c == CombiningDotAbove)
            {
                continue;
            }

            var folded = FoldChar(c);
            if (folded == ' ')
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(folded);
            lastWasSpace = false;
        }

        if (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Letters and digits only, folded: "INV-2026/00123" and "inv 2026 00123" share the key "inv202600123".
    /// </summary>
    public static string Key(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            var folded = FoldChar(c);
            if (folded != ' ')
            {
                builder.Append(folded);
            }
        }

        return builder.ToString();
    }

    // "İ".ToLowerInvariant() is "i" followed by U+0307 on some platforms; the dot must not split the word.
    private const char CombiningDotAbove = '̇';

    public static string[] Tokens(string? value) =>
        Fold(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static char FoldChar(char c) => c switch
    {
        >= 'a' and <= 'z' or >= '0' and <= '9' => c,
        >= 'A' and <= 'Z' => (char)(c + 32),
        'ı' or 'İ' or 'î' or 'Î' or 'í' or 'ì' => 'i',
        'ş' or 'Ş' => 's',
        'ğ' or 'Ğ' => 'g',
        'ü' or 'Ü' or 'û' or 'Û' => 'u',
        'ö' or 'Ö' => 'o',
        'ç' or 'Ç' => 'c',
        'â' or 'Â' or 'ä' or 'Ä' or 'á' or 'à' => 'a',
        'é' or 'è' or 'ê' or 'ë' => 'e',
        _ => ' ',
    };
}
