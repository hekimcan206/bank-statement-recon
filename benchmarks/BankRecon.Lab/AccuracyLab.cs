using System.Globalization;
using System.Text.Json;
using BankRecon.Core.Matching;
using BankRecon.Synthetic;

namespace BankRecon.Lab;

/// <summary>
/// Precision and recall of each matcher configuration on labelled synthetic statements. Every scenario goes through
/// the real file format (written, then parsed), so truncation and transliteration effects are included.
/// </summary>
internal static class AccuracyLab
{
    private static readonly (string Name, MatchingOptions? Options)[] Configurations =
    [
        ("amount + date window, first fit (baseline)", null),
        ("exact reference only", new MatchingOptions { AmountDate = false, Fuzzy = false, Groups = false }),
        ("+ unique amount/date", new MatchingOptions { Fuzzy = false, Groups = false }),
        ("+ fuzzy (Jaro-Winkler, token set)", new MatchingOptions { Groups = false }),
        ("+ one-to-many / many-to-one (full)", new MatchingOptions()),
        ("full, no ambiguity guard", new MatchingOptions { AmbiguityMargin = -1 }),
    ];

    public static void Run(int seeds, int size, string outputDirectory, int firstSeed = 101)
    {
        var styles = Enum.GetValues<NarrativeStyle>();
        var rows = new List<Row>();
        var kindRows = new List<(NarrativeStyle Style, KindOutcome Kind)>();
        foreach (var style in styles)
        {
            var perConfig = Configurations.Select(_ => new List<MatchEvaluation>()).ToArray();
            for (var seed = firstSeed; seed < firstSeed + seeds; seed++)
            {
                var scenario = ScenarioGenerator.Generate(new ScenarioOptions { Seed = seed, LedgerItems = size, Style = style });
                var parsed = Formats.ReadBack(Formats.Write(scenario));
                var lines = scenario with { Statement = parsed };
                var bankLines = lines.BankLines();
                for (var c = 0; c < Configurations.Length; c++)
                {
                    var result = Configurations[c].Options is { } options
                        ? new MatchingEngine(options).Match(bankLines, scenario.Ledger)
                        : Baseline.Match(bankLines, scenario.Ledger, 5);
                    var evaluation = MatchEvaluation.Evaluate(scenario, bankLines, result);
                    perConfig[c].Add(evaluation);
                    if (c == 4)
                    {
                        kindRows.AddRange(evaluation.Kinds.Select(k => (style, k)));
                    }
                }
            }

            for (var c = 0; c < Configurations.Length; c++)
            {
                var list = perConfig[c];
                rows.Add(new Row(
                    Formats.Label(style),
                    Configurations[c].Name,
                    Median(list.Select(e => e.Precision)),
                    list.Min(e => e.Precision),
                    Median(list.Select(e => e.Recall)),
                    list.Min(e => e.Recall),
                    Median(list.Select(e => e.F1)),
                    Median(list.Select(e => e.QueueShare))));
            }
        }

        Directory.CreateDirectory(outputDirectory);
        var markdown = new System.Text.StringBuilder();
        markdown.AppendLine(CultureInfo.InvariantCulture, $"Seeds {firstSeed}-{firstSeed + seeds - 1} per format (the rules were tuned on seeds 1-3 only), ledger items per scenario: {size}. Median over seeds (min in brackets).");
        markdown.AppendLine();
        markdown.AppendLine("| Format | Configuration | Precision | Recall | F1 | Queue share |");
        markdown.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            markdown.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Format} | {r.Configuration} | {r.Precision:0.0000} ({r.PrecisionMin:0.0000}) | {r.Recall:0.0000} ({r.RecallMin:0.0000}) | {r.F1:0.0000} | {r.QueueShare:P1} |");
        }

        markdown.AppendLine();
        markdown.AppendLine("Full configuration, by case (summed over seeds):");
        markdown.AppendLine();
        markdown.AppendLine("| Format | Case | Groups | Truth pairs | Found | Recall | Wrongly matched items |");
        markdown.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var g in kindRows.GroupBy(k => (k.Style, k.Kind.Kind)))
        {
            var groups = g.Sum(x => x.Kind.Groups);
            var truth = g.Sum(x => x.Kind.TruthPairs);
            var found = g.Sum(x => x.Kind.FoundPairs);
            var wrong = g.Sum(x => x.Kind.WronglyMatchedItems);
            var recall = truth == 0 ? "-" : ((double)found / truth).ToString("0.000", CultureInfo.InvariantCulture);
            markdown.AppendLine(CultureInfo.InvariantCulture, $"| {Formats.Label(g.Key.Style)} | {g.Key.Item2} | {groups} | {truth} | {found} | {recall} | {wrong} |");
        }

        var text = markdown.ToString();
        Console.WriteLine(text);
        File.WriteAllText(Path.Combine(outputDirectory, "accuracy.md"), text);
        File.WriteAllText(Path.Combine(outputDirectory, "accuracy.json"), JsonSerializer.Serialize(rows, LabJson.Options));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    internal sealed record Row(string Format, string Configuration, double Precision, double PrecisionMin, double Recall, double RecallMin, double F1, double QueueShare);
}

/// <summary>The approach many spreadsheets use: first open item with the same amount within the date window.</summary>
internal static class Baseline
{
    public static MatchResult Match(IReadOnlyList<BankLine> bankLines, IReadOnlyList<LedgerItem> ledger, int windowDays)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<Match>();
        var unmatchedBanks = new List<Unmatched>();
        foreach (var bank in bankLines.OrderBy(b => b.ValueDate).ThenBy(b => b.Id, StringComparer.Ordinal))
        {
            var hit = ledger.Where(l => !used.Contains(l.Id) && l.Currency == bank.Currency && l.Amount == bank.Amount &&
                                        Math.Abs(l.ExpectedDate.DayNumber - bank.ValueDate.DayNumber) <= windowDays)
                .OrderBy(l => l.ExpectedDate).ThenBy(l => l.Id, StringComparer.Ordinal).FirstOrDefault();
            if (hit is null)
            {
                unmatchedBanks.Add(new Unmatched(bank.Id, UnmatchedReason.NoCandidate, []));
                continue;
            }

            used.Add(hit.Id);
            matches.Add(new Match(MatchRule.AmountDate, [bank.Id], [hit.Id], 1, 0, "baseline"));
        }

        var unmatchedLedger = ledger.Where(l => !used.Contains(l.Id)).Select(l => new Unmatched(l.Id, UnmatchedReason.NoCandidate, [])).ToList();
        return new MatchResult(matches, unmatchedBanks, unmatchedLedger);
    }
}
