using BankRecon.Core.Matching;
using BankRecon.Synthetic;

namespace BankRecon.Lab;

/// <summary>Prints every wrong pair of one scenario with the evidence the matcher used. For tuning, not for reports.</summary>
internal static class ExplainLab
{
    public static void Run(NarrativeStyle style, int seed, int size)
    {
        var scenario = ScenarioGenerator.Generate(new ScenarioOptions { Seed = seed, LedgerItems = size, Style = style });
        scenario = scenario with { Statement = Formats.ReadBack(Formats.Write(scenario)) };
        var lines = scenario.BankLines().ToDictionary(l => l.Id, StringComparer.Ordinal);
        var ledger = scenario.Ledger.ToDictionary(l => l.Id, StringComparer.Ordinal);
        var truth = scenario.Truth.SelectMany(t => t.BankLineIds.SelectMany(b => t.LedgerItemIds.Select(l => (b, l, t.Kind)))).ToHashSet();
        var kindOfBank = scenario.Truth.SelectMany(t => t.BankLineIds.Select(b => (b, t.Kind))).GroupBy(x => x.b).ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);
        var result = new MatchingEngine().Match(lines.Values.ToList(), scenario.Ledger);
        foreach (var match in result.Matches)
        {
            foreach (var b in match.BankLineIds)
            {
                foreach (var l in match.LedgerItemIds)
                {
                    if (truth.Any(t => t.b == b && t.l == l))
                    {
                        continue;
                    }

                    var line = lines[b];
                    var item = ledger[l];
                    Console.WriteLine($"WRONG {match.Rule} bank={b} ({kindOfBank.GetValueOrDefault(b)}) {line.Amount} {line.ValueDate} '{line.Narrative ?? line.Remittance}' name='{line.CounterpartyName}'");
                    Console.WriteLine($"      ledger={l} {item.Amount} {item.ExpectedDate} ref={item.Reference} name='{item.CounterpartyName}'  :: {match.Explanation}");
                }
            }
        }
    }
}
