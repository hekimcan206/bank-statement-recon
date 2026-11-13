using BankRecon.Core.Matching;

namespace BankRecon.Synthetic;

/// <summary>Per-kind outcome: how many truth pairs were found, and for unmatched-by-design items how many were wrongly matched.</summary>
public sealed record KindOutcome(string Kind, int Groups, int TruthPairs, int FoundPairs, int WronglyMatchedItems);

/// <summary>
/// Pair-level scoring against the generator's ground truth. A match of one bank line to three ledger items is three
/// (bank line, ledger item) pairs, so a group match that gets one invoice wrong costs exactly one false pair.
/// </summary>
public sealed record MatchEvaluation(
    int TruthPairs,
    int PredictedPairs,
    int TruePositives,
    int BankLines,
    int LedgerItems,
    int QueuedBankLines,
    int QueuedLedgerItems,
    IReadOnlyList<KindOutcome> Kinds)
{
    public double Precision => PredictedPairs == 0 ? 1 : (double)TruePositives / PredictedPairs;
    public double Recall => TruthPairs == 0 ? 1 : (double)TruePositives / TruthPairs;
    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);

    /// <summary>Share of all items (both sides) left for a person.</summary>
    public double QueueShare => (double)(QueuedBankLines + QueuedLedgerItems) / Math.Max(1, BankLines + LedgerItems);

    public static MatchEvaluation Evaluate(Scenario scenario, IReadOnlyList<BankLine> bankLines, MatchResult result)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(bankLines);
        ArgumentNullException.ThrowIfNull(result);

        var truthPairs = new HashSet<(string, string)>();
        foreach (var group in scenario.Truth)
        {
            foreach (var bank in group.BankLineIds)
            {
                foreach (var ledger in group.LedgerItemIds)
                {
                    truthPairs.Add((bank, ledger));
                }
            }
        }

        var predicted = new HashSet<(string, string)>();
        var matchedBanks = new HashSet<string>(StringComparer.Ordinal);
        var matchedLedgers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var match in result.Matches)
        {
            foreach (var bank in match.BankLineIds)
            {
                matchedBanks.Add(bank);
                foreach (var ledger in match.LedgerItemIds)
                {
                    predicted.Add((bank, ledger));
                }
            }

            foreach (var ledger in match.LedgerItemIds)
            {
                matchedLedgers.Add(ledger);
            }
        }

        var kinds = scenario.Truth.GroupBy(g => g.Kind, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var pairs = g.SelectMany(t => t.BankLineIds.SelectMany(b => t.LedgerItemIds.Select(l => (b, l)))).ToList();
            var wrong = g.Where(t => t.BankLineIds.Count == 0 || t.LedgerItemIds.Count == 0)
                .Sum(t => t.BankLineIds.Count(matchedBanks.Contains) + t.LedgerItemIds.Count(matchedLedgers.Contains));
            return new KindOutcome(g.Key, g.Count(), pairs.Count, pairs.Count(predicted.Contains), wrong);
        }).ToList();

        return new MatchEvaluation(
            truthPairs.Count,
            predicted.Count,
            predicted.Count(truthPairs.Contains),
            bankLines.Count,
            scenario.Ledger.Count,
            result.UnmatchedBankLines.Count,
            result.UnmatchedLedgerItems.Count,
            kinds);
    }
}
