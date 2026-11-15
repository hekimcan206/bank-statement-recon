using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BankRecon.Core.Matching;
using BankRecon.Synthetic;

namespace BankRecon.Lab;

/// <summary>In-memory matching time for growing ledgers (one account, 20-day statement window).</summary>
internal static class MatchingThroughputLab
{
    public static void Run(int[] sizes, int runs, string outputDirectory)
    {
        var rows = new List<Row>();
        foreach (var size in sizes)
        {
            var scenario = ScenarioGenerator.Generate(new ScenarioOptions { Seed = 31, LedgerItems = size, Days = Math.Max(20, size / 250) });
            var lines = scenario.BankLines();
            _ = new MatchingEngine().Match(lines, scenario.Ledger); // warm-up (JIT)
            var times = new List<double>();
            MatchResult? result = null;
            for (var r = 0; r < runs; r++)
            {
                var stopwatch = Stopwatch.StartNew();
                result = new MatchingEngine().Match(lines, scenario.Ledger);
                times.Add(stopwatch.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            var median = times[times.Count / 2];
            var evaluation = MatchEvaluation.Evaluate(scenario, lines, result!);
            rows.Add(new Row(size, lines.Count, scenario.Options.Days, median, times[0], times[^1], (size + lines.Count) / (median / 1000), evaluation.Precision, evaluation.Recall));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{size} items: median {median:0.0} ms"));
        }

        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"Median of {runs} runs after one warm-up. The statement window grows with the ledger (about 250 invoices per day).");
        md.AppendLine();
        md.AppendLine("| Ledger items | Bank lines | Days | Median (ms) | Min-max (ms) | Items/s (both sides) | Precision | Recall |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.LedgerItems:N0} | {r.BankLines:N0} | {r.Days} | {r.MedianMs:0.0} | {r.MinMs:0.0}-{r.MaxMs:0.0} | {r.ItemsPerSecond:N0} | {r.Precision:0.0000} | {r.Recall:0.0000} |");
        }

        Console.WriteLine(md);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "matching-throughput.md"), md.ToString());
        File.WriteAllText(Path.Combine(outputDirectory, "matching-throughput.json"), JsonSerializer.Serialize(rows, LabJson.Options));
    }

    internal sealed record Row(int LedgerItems, int BankLines, int Days, double MedianMs, double MinMs, double MaxMs, double ItemsPerSecond, double Precision, double Recall);
}
