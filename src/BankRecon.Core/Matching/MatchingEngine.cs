using System.Globalization;
using BankRecon.Core.Text;

namespace BankRecon.Core.Matching;

/// <summary>
/// Rule-based matcher. Passes run from the most certain to the least certain, and every item can be used once:
/// <list type="number">
/// <item>exact reference (amount equal, date in window, ledger reference found verbatim after folding);</item>
/// <item>amount and date, only when the pair is unique in both directions;</item>
/// <item>fuzzy score with an ambiguity guard (a close runner-up sends both items to the work queue);</item>
/// <item>many-to-one and one-to-many by bounded subset-sum search over related items.</item>
/// </list>
/// The engine is a pure function of its inputs: same lines, same items, same options, same result.
/// </summary>
public sealed class MatchingEngine(MatchingOptions? options = null)
{
    private static readonly HashSet<string> LegalFormTokens = new(StringComparer.Ordinal)
    {
        "as", "a", "s", "ltd", "sti", "san", "ve", "tic", "ith", "ihr", "sirketi", "anonim", "limited", "koll", "inc",
        "gmbh", "bv", "co", "llc", "plc",
    };

    private const int MaxCustomerReferenceLength = 16;
    private const double ContradictingName = 0.3;

    private readonly MatchingOptions _options = options ?? new MatchingOptions();

    public MatchingOptions Options => _options;

    public MatchResult Match(IReadOnlyList<BankLine> bankLines, IReadOnlyList<LedgerItem> ledgerItems)
    {
        ArgumentNullException.ThrowIfNull(bankLines);
        ArgumentNullException.ThrowIfNull(ledgerItems);

        var banks = bankLines.OrderBy(b => b.ValueDate).ThenBy(b => b.Id, StringComparer.Ordinal)
            .Select((b, i) => new BankSide(b, i)).ToArray();
        var ledgers = ledgerItems.OrderBy(l => l.ExpectedDate).ThenBy(l => l.Id, StringComparer.Ordinal)
            .Select((l, i) => new LedgerSide(l, i)).ToArray();
        var run = new Run(banks, ledgers);

        if (_options.ExactReference)
        {
            ExactReferencePass(run);
        }

        if (_options.AmountDate)
        {
            AmountDatePass(run);
        }

        FuzzyPass(run);

        if (_options.Groups)
        {
            ManyToOnePass(run);
            OneToManyPass(run);
        }

        return Build(run);
    }

    private void ExactReferencePass(Run run)
    {
        foreach (var bank in run.Banks)
        {
            LedgerSide? best = null;
            var bestDays = int.MaxValue;
            var tie = false;
            foreach (var ledger in run.LedgersNear(bank.Line.Currency, bank.Line.ValueDate, _options.ReferenceWindowDays))
            {
                if (ledger.Used || ledger.Item.Amount != bank.Line.Amount || !ContainsReference(bank, ledger))
                {
                    continue;
                }

                var days = Math.Abs(bank.Line.ValueDate.DayNumber - ledger.Item.ExpectedDate.DayNumber);
                if (days < bestDays)
                {
                    best = ledger;
                    bestDays = days;
                    tie = false;
                }
                else if (days == bestDays)
                {
                    tie = true;
                }
            }

            if (best is not null && !tie)
            {
                run.Accept(new Match(
                    MatchRule.ExactReference,
                    [bank.Line.Id],
                    [best.Item.Id],
                    1.0,
                    0m,
                    $"reference '{best.Item.Reference}' found on the bank line; amount equal; value date {Days(bank, best)}"),
                    [bank],
                    [best]);
            }
        }
    }

    private void AmountDatePass(Run run)
    {
        foreach (var bank in run.Banks)
        {
            if (bank.Used)
            {
                continue;
            }

            var sameAmount = run.LedgersNear(bank.Line.Currency, bank.Line.ValueDate, _options.DateWindowDays)
                .Where(l => !l.Used && l.Item.Amount == bank.Line.Amount).ToList();
            if (sameAmount.Count == 0)
            {
                continue;
            }

            // First: the amount is unique in the window. Otherwise: unique among items of this counterparty.
            var byCounterparty = false;
            var only = sameAmount.Count == 1 ? sameAmount[0] : null;
            if (only is null)
            {
                only = Single(sameAmount.Where(l => NameScore(bank, l) >= _options.CounterpartyThreshold));
                byCounterparty = only is not null;
            }

            if (only is null)
            {
                continue;
            }

            var reverseCandidates = run.BanksNear(bank.Line.Currency, only.Item.ExpectedDate, _options.DateWindowDays)
                .Where(b => !b.Used && b.Line.Amount == only.Item.Amount);
            if (byCounterparty)
            {
                reverseCandidates = reverseCandidates.Where(b => NameScore(b, only) >= _options.CounterpartyThreshold);
            }

            if (!ReferenceEquals(Single(reverseCandidates), bank))
            {
                continue;
            }

            // Unique amounts are strong evidence, but not against a counterparty that is clearly someone else.
            var name = NameScore(bank, only);
            if (name is < ContradictingName)
            {
                continue;
            }

            run.Accept(new Match(
                MatchRule.AmountDate,
                [bank.Line.Id],
                [only.Item.Id],
                byCounterparty ? 0.8 : 0.85,
                0m,
                byCounterparty
                    ? $"only item of this counterparty with amount {Money(bank.Line.Amount)} in the window, both directions; value date {Days(bank, only)}"
                    : $"only candidate in both directions: amount {Money(bank.Line.Amount)} equal, value date {Days(bank, only)}"),
                [bank],
                [only]);
        }
    }

    private void FuzzyPass(Run run)
    {
        var pairs = new List<(BankSide Bank, LedgerSide Ledger, double Score, string Why)>();
        foreach (var bank in run.Banks)
        {
            if (bank.Used)
            {
                continue;
            }

            foreach (var ledger in run.LedgersNear(bank.Line.Currency, bank.Line.ValueDate, _options.DateWindowDays))
            {
                if (ledger.Used || Math.Sign(ledger.Item.Amount) != Math.Sign(bank.Line.Amount))
                {
                    continue;
                }

                var tolerance = Tolerance(ledger.Item.Amount);
                var difference = bank.Line.Amount - ledger.Item.Amount;
                if (Math.Abs(difference) > tolerance)
                {
                    continue;
                }

                var (score, why) = FuzzyScore(bank, ledger, difference, tolerance);
                bank.Candidates.Add(new Candidate(ledger.Item.Id, score));
                ledger.Candidates.Add(new Candidate(bank.Line.Id, score));
                if (score >= _options.FuzzyThreshold)
                {
                    pairs.Add((bank, ledger, score, why));
                }
            }
        }

        if (!_options.Fuzzy)
        {
            return;
        }

        var byBank = pairs.ToLookup(p => p.Bank.Index);
        var byLedger = pairs.ToLookup(p => p.Ledger.Index);
        foreach (var pair in pairs.OrderByDescending(p => p.Score).ThenBy(p => p.Bank.Index).ThenBy(p => p.Ledger.Index))
        {
            if (pair.Bank.Used || pair.Ledger.Used)
            {
                continue;
            }

            var bankRunnerUp = byBank[pair.Bank.Index].Where(p => p.Ledger != pair.Ledger && !p.Ledger.Used)
                .Select(p => p.Score).DefaultIfEmpty(0).Max();
            var ledgerRunnerUp = byLedger[pair.Ledger.Index].Where(p => p.Bank != pair.Bank && !p.Bank.Used)
                .Select(p => p.Score).DefaultIfEmpty(0).Max();
            if (Math.Max(bankRunnerUp, ledgerRunnerUp) >= pair.Score - _options.AmbiguityMargin)
            {
                pair.Bank.Ambiguous = true;
                pair.Ledger.Ambiguous = true;
                continue;
            }

            run.Accept(new Match(
                MatchRule.Fuzzy,
                [pair.Bank.Line.Id],
                [pair.Ledger.Item.Id],
                Math.Round(pair.Score, 4),
                pair.Bank.Line.Amount - pair.Ledger.Item.Amount,
                pair.Why),
                [pair.Bank],
                [pair.Ledger]);
        }
    }

    private void ManyToOnePass(Run run)
    {
        foreach (var bank in run.Banks)
        {
            if (bank.Used)
            {
                continue;
            }

            var related = run.LedgersNear(bank.Line.Currency, bank.Line.ValueDate, _options.GroupWindowDays)
                .Where(l => !l.Used && Math.Sign(l.Item.Amount) == Math.Sign(bank.Line.Amount) &&
                            Math.Abs(l.Item.Amount) < Math.Abs(bank.Line.Amount))
                .Select(l => (Ledger: l, Relation: Relation(bank, l)))
                .Where(x => x.Relation > 0)
                .OrderByDescending(x => x.Relation).ThenBy(x => x.Ledger.Index)
                .Take(_options.MaxGroupCandidates)
                .ToList();
            if (related.Count < 2)
            {
                continue;
            }

            var target = Math.Abs(bank.Line.Amount);
            var chosen = BestSubset(
                related.Select(x => (Amount: Math.Abs(x.Ledger.Item.Amount), Strong: ContainsReference(bank, x.Ledger))).ToList(),
                target,
                Tolerance(bank.Line.Amount),
                out var ambiguous);
            if (chosen is null)
            {
                if (ambiguous)
                {
                    bank.Ambiguous = true;
                }

                continue;
            }

            var items = chosen.Select(i => related[i].Ledger).ToList();
            var sum = items.Sum(l => l.Item.Amount);
            run.Accept(new Match(
                MatchRule.ManyToOne,
                [bank.Line.Id],
                items.Select(l => l.Item.Id).ToList(),
                sum == bank.Line.Amount ? 0.9 : 0.8,
                bank.Line.Amount - sum,
                $"one bank line of {Money(bank.Line.Amount)} settles {items.Count} related ledger items summing to {Money(sum)}"),
                [bank],
                items);
        }
    }

    private void OneToManyPass(Run run)
    {
        foreach (var ledger in run.Ledgers)
        {
            if (ledger.Used)
            {
                continue;
            }

            var related = run.BanksNear(ledger.Item.Currency, ledger.Item.ExpectedDate, _options.GroupWindowDays)
                .Where(b => !b.Used && Math.Sign(b.Line.Amount) == Math.Sign(ledger.Item.Amount) &&
                            Math.Abs(b.Line.Amount) < Math.Abs(ledger.Item.Amount))
                .Select(b => (Bank: b, Relation: Relation(b, ledger)))
                .Where(x => x.Relation > 0)
                .OrderByDescending(x => x.Relation).ThenBy(x => x.Bank.Index)
                .Take(_options.MaxGroupCandidates)
                .ToList();
            if (related.Count < 2)
            {
                continue;
            }

            // Here the bank side is the one that may be short by deducted charges, so the search target is the
            // ledger amount minus the tolerance up to the ledger amount.
            var tolerance = Tolerance(ledger.Item.Amount);
            var target = Math.Abs(ledger.Item.Amount) - tolerance;
            var chosen = BestSubset(
                related.Select(x => (Amount: Math.Abs(x.Bank.Line.Amount), Strong: ContainsReference(x.Bank, ledger))).ToList(),
                target,
                tolerance,
                out var ambiguous);
            if (chosen is null)
            {
                if (ambiguous)
                {
                    ledger.Ambiguous = true;
                }

                continue;
            }

            var lines = chosen.Select(i => related[i].Bank).ToList();
            var sum = lines.Sum(b => b.Line.Amount);
            run.Accept(new Match(
                MatchRule.OneToMany,
                lines.Select(b => b.Line.Id).ToList(),
                [ledger.Item.Id],
                sum == ledger.Item.Amount ? 0.9 : 0.8,
                sum - ledger.Item.Amount,
                $"{lines.Count} related bank lines summing to {Money(sum)} settle one ledger item of {Money(ledger.Item.Amount)}"),
                lines,
                [ledger]);
        }
    }

    /// <summary>
    /// Finds the subsets (2..MaxGroupSize elements) whose sum lies in [target, target + tolerance]. Among several,
    /// the one with the most reference-confirmed members wins, then the one closest to the target; a tie is
    /// ambiguous. Depth-first over amounts sorted descending, pruned by the running sum and by the largest sum the
    /// remaining elements could still add.
    /// </summary>
    internal List<int>? BestSubset(IReadOnlyList<(decimal Amount, bool Strong)> items, decimal target, decimal tolerance, out bool ambiguous)
    {
        ambiguous = false;
        var order = Enumerable.Range(0, items.Count).OrderByDescending(i => items[i].Amount).ThenBy(i => i).ToArray();
        var suffix = new decimal[order.Length + 1];
        for (var k = order.Length - 1; k >= 0; k--)
        {
            suffix[k] = suffix[k + 1] + items[order[k]].Amount;
        }

        List<int>? best = null;
        (int Strong, decimal Distance) bestKey = (-1, decimal.MaxValue);
        var bestCount = 0;
        var solutions = 0;
        var stack = new List<int>(_options.MaxGroupSize);
        Search(0, 0m);
        ambiguous = best is null ? false : bestCount > 1;
        return ambiguous ? null : best;

        void Search(int from, decimal sum)
        {
            if (solutions > 64)
            {
                return;
            }

            if (stack.Count >= 2 && sum >= target && sum <= target + tolerance)
            {
                solutions++;
                var key = (stack.Count(i => items[i].Strong), sum - target);
                var better = key.Item1 > bestKey.Strong || (key.Item1 == bestKey.Strong && key.Item2 < bestKey.Distance);
                if (better)
                {
                    best = [.. stack];
                    bestKey = key;
                    bestCount = 1;
                }
                else if (key.Item1 == bestKey.Strong && key.Item2 == bestKey.Distance)
                {
                    bestCount++;
                }
            }

            if (stack.Count == _options.MaxGroupSize)
            {
                return;
            }

            for (var k = from; k < order.Length; k++)
            {
                var amount = items[order[k]].Amount;
                if (sum + amount > target + tolerance)
                {
                    continue;
                }

                if (sum + suffix[k] < target)
                {
                    return;
                }

                stack.Add(order[k]);
                Search(k + 1, sum + amount);
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }

    private (double Score, string Why) FuzzyScore(BankSide bank, LedgerSide ledger, decimal difference, decimal tolerance)
    {
        var reference = ReferenceScore(bank, ledger);
        var name = NameScore(bank, ledger);
        var amount = difference == 0 ? 1.0 : tolerance == 0 ? 0.0 : 1.0 - (double)(Math.Abs(difference) / tolerance);
        var days = Math.Abs(bank.Line.ValueDate.DayNumber - ledger.Item.ExpectedDate.DayNumber);
        var date = 1.0 - ((double)days / (_options.DateWindowDays + 1));
        var score = name is { } n
            ? (0.5 * reference) + (0.2 * n) + (0.2 * amount) + (0.1 * date)
            : (0.65 * reference) + (0.2 * amount) + (0.15 * date);

        // A clearly different counterparty contradicts a merely similar reference ("INV-0323" from one company is
        // not "INV-0383" of another): halve the score so it cannot pass on reference likeness alone.
        if (name is < ContradictingName && reference < 1.0)
        {
            score *= 0.5;
        }
        var why = string.Create(
            CultureInfo.InvariantCulture,
            $"reference {reference:0.00}, counterparty {(name is null ? "n/a" : name.Value.ToString("0.00", CultureInfo.InvariantCulture))}, " +
            $"amount difference {Money(difference)}, value date {Days(bank, ledger)}");
        return (score, why);
    }

    /// <summary>How strongly two items are related for group search; 0 means unrelated.</summary>
    private double Relation(BankSide bank, LedgerSide ledger)
    {
        if (ContainsReference(bank, ledger))
        {
            return 2;
        }

        var name = NameScore(bank, ledger);
        return name >= _options.GroupNameThreshold ? name.Value : 0;
    }

    private decimal Tolerance(decimal amount) =>
        Math.Min(_options.AbsoluteTolerance, Math.Round(Math.Abs(amount) * _options.RelativeTolerance, 2));

    internal static bool ContainsReference(BankSide bank, LedgerSide ledger)
    {
        var key = ledger.ReferenceKey;
        if (key.Length < 6)
        {
            return false;
        }

        foreach (var field in bank.FieldKeys)
        {
            if (field.Contains(key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Best similarity between the ledger reference and any 1-3 token window of the bank text. Digits are treated
    /// as more important than letters: "INV-00123" and "INV-00132" are different invoices for Jaro-Winkler too
    /// similar to tell apart, so a digit mismatch beyond one edit halves the score.
    /// </summary>
    internal static double ReferenceScore(BankSide bank, LedgerSide ledger)
    {
        var key = ledger.ReferenceKey;
        if (key.Length == 0)
        {
            return 0;
        }

        if (ContainsReference(bank, ledger))
        {
            return 1;
        }

        // A :61: customer reference that fills all 16 characters may have been cut; a ledger reference that starts
        // with it is very likely the same one. Shorter fields are complete, so a prefix there means nothing.
        if (bank.TruncatedReferenceKey is { Length: >= 8 } truncated && key.Length > truncated.Length &&
            key.StartsWith(truncated, StringComparison.Ordinal))
        {
            return 0.97;
        }

        var best = 0.0;
        foreach (var window in bank.Windows)
        {

            var ratio = (double)window.Length / key.Length;
            if (window.Length < 4 || ratio < 0.6 || ratio > 1.6)
            {
                continue;
            }

            var score = Similarity.JaroWinkler(key, window);
            if (score <= best)
            {
                continue;
            }

            var distance = DigitDistance(key, window);
            best = Math.Max(best, distance == 0 ? score : distance == 1 ? score * 0.92 : score * 0.5);
        }

        return best;
    }

    internal static double? NameScore(BankSide bank, LedgerSide ledger)
    {
        if (ledger.NameTokens.Length == 0)
        {
            return null;
        }

        if (bank.NameTokens.Length > 0)
        {
            return Similarity.TokenSet(ledger.NameTokens, bank.NameTokens);
        }

        if (bank.TextTokens.Length == 0)
        {
            return null;
        }

        // Free-text narratives (common in Turkish MT940) carry the name somewhere in the text: count how many of
        // the ledger name's words appear there.
        var found = 0;
        foreach (var token in ledger.NameTokens)
        {
            foreach (var candidate in bank.TextTokens)
            {
                if (string.Equals(token, candidate, StringComparison.Ordinal) ||
                    (token.Length >= 4 && Similarity.JaroWinkler(token, candidate) >= 0.92))
                {
                    found++;
                    break;
                }
            }
        }

        return (double)found / ledger.NameTokens.Length;
    }

    /// <summary>Damerau-Levenshtein distance between the digit sequences, capped at 2.</summary>
    internal static int DigitDistance(string a, string b)
    {
        var da = new string(a.Where(char.IsAsciiDigit).ToArray());
        var db = new string(b.Where(char.IsAsciiDigit).ToArray());
        if (string.Equals(da, db, StringComparison.Ordinal))
        {
            return 0;
        }

        if (Math.Abs(da.Length - db.Length) > 1)
        {
            return 2;
        }

        var d = new int[da.Length + 1, db.Length + 1];
        for (var i = 0; i <= da.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= db.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= da.Length; i++)
        {
            for (var j = 1; j <= db.Length; j++)
            {
                var cost = da[i - 1] == db[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && da[i - 1] == db[j - 2] && da[i - 2] == db[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return Math.Min(2, d[da.Length, db.Length]);
    }

    private static MatchResult Build(Run run)
    {
        var unmatchedBanks = run.Banks.Where(b => !b.Used)
            .Select(b => new Unmatched(b.Line.Id, Reason(b.Ambiguous, b.Candidates.Count), Top(b.Candidates, id => run.LedgerUsed(id))))
            .ToList();
        var unmatchedLedgers = run.Ledgers.Where(l => !l.Used)
            .Select(l => new Unmatched(l.Item.Id, Reason(l.Ambiguous, l.Candidates.Count), Top(l.Candidates, id => run.BankUsed(id))))
            .ToList();
        return new MatchResult(run.Matches, unmatchedBanks, unmatchedLedgers);

        static UnmatchedReason Reason(bool ambiguous, int candidates) =>
            ambiguous ? UnmatchedReason.Ambiguous : candidates > 0 ? UnmatchedReason.BelowThreshold : UnmatchedReason.NoCandidate;

        static IReadOnlyList<Candidate> Top(List<Candidate> candidates, Func<string, bool> used) =>
            candidates.Where(c => !used(c.Id)).OrderByDescending(c => c.Score).ThenBy(c => c.Id, StringComparer.Ordinal)
                .Take(3).Select(c => c with { Score = Math.Round(c.Score, 4) }).ToList();
    }

    private static T? Single<T>(IEnumerable<T> source)
        where T : class
    {
        T? found = null;
        foreach (var item in source)
        {
            if (found is not null)
            {
                return null;
            }

            found = item;
        }

        return found;
    }

    private static string Money(decimal amount) => amount.ToString("#,0.00", CultureInfo.InvariantCulture);

    private static string Days(BankSide bank, LedgerSide ledger)
    {
        var days = bank.Line.ValueDate.DayNumber - ledger.Item.ExpectedDate.DayNumber;
        return days == 0 ? "same day" : string.Create(CultureInfo.InvariantCulture, $"{days:+0;-0} day(s)");
    }

    internal sealed class BankSide
    {
        public BankSide(BankLine line, int index)
        {
            Line = line;
            Index = index;
            string?[] fields = [line.CustomerReference, line.EndToEndId, line.Remittance, line.Narrative];
            FieldKeys = fields.Where(f => !string.IsNullOrWhiteSpace(f)).Select(TextNormalizer.Key).Where(k => k.Length > 0).ToArray();
            var windows = new HashSet<string>(StringComparer.Ordinal);
            var tokens = new List<string>();
            foreach (var field in fields)
            {
                var fieldTokens = TextNormalizer.Tokens(field);
                tokens.AddRange(fieldTokens);
                for (var i = 0; i < fieldTokens.Length; i++)
                {
                    var window = fieldTokens[i];
                    windows.Add(window);
                    for (var j = i + 1; j < Math.Min(fieldTokens.Length, i + 3); j++)
                    {
                        window += fieldTokens[j];
                        windows.Add(window);
                    }
                }
            }

            Windows = [.. windows];
            TextTokens = tokens.Where(t => !LegalFormTokens.Contains(t)).Distinct(StringComparer.Ordinal).ToArray();
            NameTokens = NameTokensOf(line.CounterpartyName);
            TruncatedReferenceKey = line.CustomerReference is { Length: MaxCustomerReferenceLength } reference
                ? TextNormalizer.Key(reference)
                : null;
        }

        public BankLine Line { get; }
        public int Index { get; }
        public string[] FieldKeys { get; }
        public string[] Windows { get; }
        public string[] TextTokens { get; }
        public string[] NameTokens { get; }
        public string? TruncatedReferenceKey { get; }
        public bool Used { get; set; }
        public bool Ambiguous { get; set; }
        public List<Candidate> Candidates { get; } = [];
    }

    internal sealed class LedgerSide(LedgerItem item, int index)
    {
        public LedgerItem Item { get; } = item;
        public int Index { get; } = index;
        public string ReferenceKey { get; } = TextNormalizer.Key(item.Reference);
        public string[] NameTokens { get; } = NameTokensOf(item.CounterpartyName);
        public bool Used { get; set; }
        public bool Ambiguous { get; set; }
        public List<Candidate> Candidates { get; } = [];
    }

    private static string[] NameTokensOf(string? name) =>
        TextNormalizer.Tokens(name).Where(t => !LegalFormTokens.Contains(t)).ToArray();

    private sealed class Run
    {
        private readonly Dictionary<string, LedgerSide[]> _ledgersByCurrency;
        private readonly Dictionary<string, BankSide[]> _banksByCurrency;
        private readonly Dictionary<string, BankSide> _bankById;
        private readonly Dictionary<string, LedgerSide> _ledgerById;

        public Run(BankSide[] banks, LedgerSide[] ledgers)
        {
            Banks = banks;
            Ledgers = ledgers;
            _ledgersByCurrency = ledgers.GroupBy(l => l.Item.Currency, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            _banksByCurrency = banks.GroupBy(b => b.Line.Currency, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            _bankById = banks.ToDictionary(b => b.Line.Id, StringComparer.Ordinal);
            _ledgerById = ledgers.ToDictionary(l => l.Item.Id, StringComparer.Ordinal);
        }

        public BankSide[] Banks { get; }
        public LedgerSide[] Ledgers { get; }
        public List<Match> Matches { get; } = [];

        public bool BankUsed(string id) => _bankById[id].Used;
        public bool LedgerUsed(string id) => _ledgerById[id].Used;

        public void Accept(Match match, IReadOnlyList<BankSide> banks, IReadOnlyList<LedgerSide> ledgers)
        {
            foreach (var bank in banks)
            {
                bank.Used = true;
            }

            foreach (var ledger in ledgers)
            {
                ledger.Used = true;
            }

            Matches.Add(match);
        }

        // Both sides are sorted by date, so a window is a binary search plus a short scan.
        public IEnumerable<LedgerSide> LedgersNear(string currency, DateOnly date, int days) =>
            _ledgersByCurrency.TryGetValue(currency, out var list)
                ? Window(list, l => l.Item.ExpectedDate.DayNumber, date.DayNumber, days)
                : [];

        public IEnumerable<BankSide> BanksNear(string currency, DateOnly date, int days) =>
            _banksByCurrency.TryGetValue(currency, out var list)
                ? Window(list, b => b.Line.ValueDate.DayNumber, date.DayNumber, days)
                : [];

        private static IEnumerable<T> Window<T>(T[] sorted, Func<T, int> day, int center, int days)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (day(sorted[mid]) < center - days)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            for (var i = lo; i < sorted.Length && day(sorted[i]) <= center + days; i++)
            {
                yield return sorted[i];
            }
        }
    }
}
