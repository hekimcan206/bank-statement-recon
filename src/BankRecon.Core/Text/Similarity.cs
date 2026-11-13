namespace BankRecon.Core.Text;

/// <summary>String similarity measures used by the matcher. All return a value in [0, 1].</summary>
public static class Similarity
{
    /// <summary>
    /// Jaro-Winkler with the usual prefix scale 0.1 over at most 4 characters. Chosen for references because the
    /// typical damage is a typo, a transposition or a truncated tail (MT940 cuts the customer reference at 16
    /// characters), and Jaro-Winkler rewards a shared prefix.
    /// </summary>
    public static double JaroWinkler(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length == 0 && b.Length == 0)
        {
            return 1;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var jaro = Jaro(a, b);
        var prefix = 0;
        var maxPrefix = Math.Min(4, Math.Min(a.Length, b.Length));
        while (prefix < maxPrefix && a[prefix] == b[prefix])
        {
            prefix++;
        }

        return jaro + (prefix * 0.1 * (1 - jaro));
    }

    public static double Jaro(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length == 0 && b.Length == 0)
        {
            return 1;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var window = Math.Max(0, (Math.Max(a.Length, b.Length) / 2) - 1);
        Span<bool> aMatched = a.Length <= 256 ? stackalloc bool[a.Length] : new bool[a.Length];
        Span<bool> bMatched = b.Length <= 256 ? stackalloc bool[b.Length] : new bool[b.Length];
        var matches = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var from = Math.Max(0, i - window);
            var to = Math.Min(b.Length - 1, i + window);
            for (var j = from; j <= to; j++)
            {
                if (!bMatched[j] && a[i] == b[j])
                {
                    aMatched[i] = true;
                    bMatched[j] = true;
                    matches++;
                    break;
                }
            }
        }

        if (matches == 0)
        {
            return 0;
        }

        var transpositions = 0;
        var k = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (!aMatched[i])
            {
                continue;
            }

            while (!bMatched[k])
            {
                k++;
            }

            if (a[i] != b[k])
            {
                transpositions++;
            }

            k++;
        }

        double m = matches;
        return ((m / a.Length) + (m / b.Length) + ((m - (transpositions / 2.0)) / m)) / 3.0;
    }

    /// <summary>
    /// Soft token-set similarity (a Dice coefficient where two tokens count as equal when their Jaro-Winkler score
    /// reaches <paramref name="tokenThreshold"/>). Word order, legal-form suffixes and one misspelt word do not sink
    /// a counterparty name: "ANADOLU TEKSTIL SAN. VE TIC. A.S." vs "Anadolu Tekstil AS".
    /// </summary>
    public static double TokenSet(IReadOnlyList<string> a, IReadOnlyList<string> b, double tokenThreshold = 0.9)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        Span<bool> used = large.Count <= 64 ? stackalloc bool[large.Count] : new bool[large.Count];
        var matched = 0;
        foreach (var token in small)
        {
            var best = -1;
            var bestScore = tokenThreshold;
            for (var j = 0; j < large.Count; j++)
            {
                if (used[j])
                {
                    continue;
                }

                var score = string.Equals(token, large[j], StringComparison.Ordinal) ? 1.0 : JaroWinkler(token, large[j]);
                if (score >= bestScore)
                {
                    bestScore = score;
                    best = j;
                }
            }

            if (best >= 0)
            {
                used[best] = true;
                matched++;
            }
        }

        return 2.0 * matched / (a.Count + b.Count);
    }
}
