using System.Globalization;
using System.Text;

namespace BankRecon.Core.Statements.Mt940;

/// <summary>SWIFT FIN primitives: the X character set, YYMMDD dates and comma-decimal amounts.</summary>
public static class SwiftText
{
    /// <summary>Maximum number of characters of a SWIFT amount, decimal comma included (15d).</summary>
    public const int MaxAmountLength = 15;

    /// <summary>True when the character belongs to the SWIFT X character set (CR and LF excluded).</summary>
    public static bool IsXChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '?' or ':' or '(' or ')' or '.' or ',' or '\'' or '+' or ' ';

    /// <summary>
    /// Maps text to the X character set. Turkish letters are transliterated (Ş to S, ı to i, İ to I, and so on) instead
    /// of being dropped, because a dropped letter breaks reference matching later; anything else becomes a space.
    /// </summary>
    public static string ToXCharset(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            var mapped = c switch
            {
                'ç' => 'c',
                'Ç' => 'C',
                'ğ' => 'g',
                'Ğ' => 'G',
                'ı' => 'i',
                'İ' => 'I',
                'ö' => 'o',
                'Ö' => 'O',
                'ş' => 's',
                'Ş' => 'S',
                'ü' => 'u',
                'Ü' => 'U',
                _ => IsXChar(c) ? c : ' ',
            };
            builder.Append(mapped);
        }

        return builder.ToString();
    }

    public static string FormatAmount(decimal amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        var text = amount.ToString("0.00#", CultureInfo.InvariantCulture).Replace('.', ',');
        return text.Length > MaxAmountLength
            ? throw new ArgumentOutOfRangeException(nameof(amount), "Amount does not fit the 15-character SWIFT field.")
            : text;
    }

    /// <summary>Parses 15d: digits with exactly one decimal comma, for example "1234,5" or "1234,".</summary>
    public static bool TryParseAmount(ReadOnlySpan<char> text, out decimal amount)
    {
        amount = 0;
        if (text.IsEmpty || text.Length > MaxAmountLength)
        {
            return false;
        }

        var comma = text.IndexOf(',');
        if (comma <= 0 || text[(comma + 1)..].Contains(','))
        {
            return false;
        }

        var integer = text[..comma];
        var fraction = text[(comma + 1)..];
        if (fraction.Length > 3 || !IsDigits(integer) || !IsDigits(fraction))
        {
            return false;
        }

        long whole = 0;
        foreach (var c in integer)
        {
            whole = (whole * 10) + (c - '0');
        }

        long minor = 0;
        foreach (var c in fraction)
        {
            minor = (minor * 10) + (c - '0');
        }

        amount = whole + (minor / (decimal)Pow10(fraction.Length));
        return true;
    }

    public static string FormatDate(DateOnly date) => date.ToString("yyMMdd", CultureInfo.InvariantCulture);

    /// <summary>YYMMDD with the SWIFT century rule used by most banks: 00-79 is 20xx, 80-99 is 19xx.</summary>
    public static bool TryParseDate(ReadOnlySpan<char> text, out DateOnly date)
    {
        date = default;
        if (text.Length != 6 || !IsDigits(text))
        {
            return false;
        }

        var yy = Number(text[..2]);
        var year = yy <= 79 ? 2000 + yy : 1900 + yy;
        return TryCreate(year, Number(text.Slice(2, 2)), Number(text.Slice(4, 2)), out date);
    }

    /// <summary>
    /// The MT940 entry date is MMDD without a year. The year is the one that puts it closest to the value date, which
    /// handles a 31 December value date booked on 2 January.
    /// </summary>
    public static bool TryParseEntryDate(ReadOnlySpan<char> mmdd, DateOnly valueDate, out DateOnly date)
    {
        date = default;
        if (mmdd.Length != 4 || !IsDigits(mmdd))
        {
            return false;
        }

        var month = Number(mmdd[..2]);
        var day = Number(mmdd.Slice(2, 2));
        var best = int.MaxValue;
        for (var year = valueDate.Year - 1; year <= valueDate.Year + 1; year++)
        {
            if (TryCreate(year, month, day, out var candidate))
            {
                var distance = Math.Abs(candidate.DayNumber - valueDate.DayNumber);
                if (distance < best)
                {
                    best = distance;
                    date = candidate;
                }
            }
        }

        return best != int.MaxValue;
    }

    internal static bool IsDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static int Number(ReadOnlySpan<char> digits)
    {
        var n = 0;
        foreach (var c in digits)
        {
            n = (n * 10) + (c - '0');
        }

        return n;
    }

    private static long Pow10(int n)
    {
        long p = 1;
        for (var i = 0; i < n; i++)
        {
            p *= 10;
        }

        return p;
    }

    private static bool TryCreate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }
}
