using System.Globalization;
using System.Text;

namespace BankRecon.Core.Statements;

/// <summary>ISO 13616 IBAN: structure check and the ISO 7064 MOD 97-10 checksum.</summary>
public static class Iban
{
    // Lengths for the countries this project's samples and tests use; other countries get the generic 15..34 check.
    private static readonly Dictionary<string, int> Lengths = new(StringComparer.Ordinal)
    {
        ["TR"] = 26,
        ["DE"] = 22,
        ["NL"] = 18,
        ["GB"] = 22,
        ["FR"] = 27,
        ["AT"] = 20,
        ["BE"] = 16,
        ["CH"] = 21,
        ["ES"] = 24,
        ["IT"] = 27,
        ["PL"] = 28,
        ["SE"] = 24,
    };

    /// <summary>Removes spaces and upper-cases; returns null when the result is not a valid IBAN.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return null;
        }

        var compact = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c != ' ')
            {
                compact.Append(char.ToUpperInvariant(c));
            }
        }

        var iban = compact.ToString();
        return IsValid(iban) ? iban : null;
    }

    public static bool IsValid(string? iban)
    {
        if (iban is null || iban.Length < 15 || iban.Length > 34)
        {
            return false;
        }

        if (!char.IsAsciiLetterUpper(iban[0]) || !char.IsAsciiLetterUpper(iban[1]) ||
            !char.IsAsciiDigit(iban[2]) || !char.IsAsciiDigit(iban[3]))
        {
            return false;
        }

        if (Lengths.TryGetValue(iban[..2], out var expected) && iban.Length != expected)
        {
            return false;
        }

        foreach (var c in iban)
        {
            if (!char.IsAsciiDigit(c) && !char.IsAsciiLetterUpper(c))
            {
                return false;
            }
        }

        return Mod97(iban[4..] + iban[..4]) == 1;
    }

    /// <summary>
    /// Builds a checksum-valid Turkish IBAN: TR, two check digits, 5-digit bank code, a reserved 0, 16-digit account.
    /// </summary>
    public static string CreateTurkish(int bankCode, long accountNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bankCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bankCode, 99_999);
        ArgumentOutOfRangeException.ThrowIfNegative(accountNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(accountNumber, 9_999_999_999_999_999);
        var bban = string.Create(CultureInfo.InvariantCulture, $"{bankCode:D5}0{accountNumber:D16}");
        var check = 98 - Mod97(bban + "TR00");
        return string.Create(CultureInfo.InvariantCulture, $"TR{check:D2}{bban}");
    }

    private static int Mod97(string rearranged)
    {
        var remainder = 0;
        foreach (var c in rearranged)
        {
            if (char.IsAsciiDigit(c))
            {
                remainder = ((remainder * 10) + (c - '0')) % 97;
            }
            else
            {
                remainder = ((remainder * 100) + (c - 'A' + 10)) % 97;
            }
        }

        return remainder;
    }
}
