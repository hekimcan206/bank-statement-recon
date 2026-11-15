using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BankRecon.Core.Statements;
using BankRecon.Core.Statements.Camt;
using BankRecon.Core.Statements.Mt940;
using BankRecon.Synthetic;

namespace BankRecon.Lab;

/// <summary>
/// Parse throughput on large synthetic files, streaming versus buffering. The file is written once from a
/// generated statement repeated under different statement ids, then parsed several times; the median is reported.
/// </summary>
internal static class ParseLab
{
    public static void Run(int mt940Entries, int camtEntries, int runs, string outputDirectory)
    {
        var work = Path.Combine(Path.GetTempPath(), "bank-recon-lab-" + Environment.ProcessId);
        Directory.CreateDirectory(work);
        var rows = new List<Row>();
        try
        {
            rows.AddRange(Measure("MT940 (Turkish free text)", WriteMt940(work, mt940Entries), runs));
            rows.AddRange(Measure("camt.053.001.08", WriteCamt(work, camtEntries), runs));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }

        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"Median of {runs} runs per row (min-max in brackets). Peak heap is sampled every 10,000 entries.");
        md.AppendLine();
        md.AppendLine("| Format | Mode | File size | Entries | Time (s) | MB/s | Entries/s | Allocated per entry | Peak managed heap |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Format} | {r.Mode} | {r.FileMb:0.0} MB | {r.Entries:N0} | {r.Seconds:0.00} ({r.MinSeconds:0.00}-{r.MaxSeconds:0.00}) | {r.MbPerSecond:0.0} | {r.EntriesPerSecond:N0} | {r.BytesPerEntry:N0} B | {r.PeakHeapMb:0} MB |");
        }

        Console.WriteLine(md);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "parse.md"), md.ToString());
        File.WriteAllText(Path.Combine(outputDirectory, "parse.json"), JsonSerializer.Serialize(rows, LabJson.Options));
    }

    private static Statement Template(NarrativeStyle style) =>
        ScenarioGenerator.Generate(new ScenarioOptions { Seed = 4242, LedgerItems = 6000, Style = style }).Statement;

    private static IEnumerable<Statement> Repeat(Statement template, int entries)
    {
        for (var i = 0; entries > 0; i++)
        {
            var take = Math.Min(entries, template.Entries.Count);
            var part = template.Entries.Take(take).ToList();
            var header = template.Header with { StatementId = string.Create(CultureInfo.InvariantCulture, $"LAB{i:D6}") };
            var closing = Balance.FromSigned(header.Opening.SignedAmount + part.Sum(e => e.SignedAmount), template.Closing.Date, header.Currency);
            yield return new Statement { Header = header, Entries = part, Closing = closing };
            entries -= take;
        }
    }

    private static string WriteMt940(string directory, int entries)
    {
        var path = Path.Combine(directory, "large.sta");
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16) { NewLine = "\r\n" };
        Mt940Writer.Write(writer, Repeat(Template(NarrativeStyle.TurkishFreeText), entries), new Mt940WriteOptions { XCharsetOnly = false });
        return path;
    }

    private static string WriteCamt(string directory, int entries)
    {
        var path = Path.Combine(directory, "large.xml");
        using var stream = File.Create(path);
        Camt053Writer.Write(stream, Repeat(Template(NarrativeStyle.Camt), entries).ToList());
        return path;
    }

    private static IEnumerable<Row> Measure(string format, string path, int runs)
    {
        var size = new FileInfo(path).Length / 1024.0 / 1024.0;
        foreach (var buffered in new[] { false, true })
        {
            var samples = new List<(double Seconds, long Entries, long Allocated, long PeakHeap)>();
            for (var run = 0; run < (buffered ? 1 : runs); run++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                long peak = 0;
                long entries = 0;
                var stopwatch = Stopwatch.StartNew();
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                {
                    var parts = StatementFile.Read(stream, out _);
                    if (buffered)
                    {
                        // What a naive importer does: materialise every statement before touching the database.
                        var all = new List<Statement>();
                        foreach (var statement in parts.ToStatements())
                        {
                            all.Add(statement);
                            entries += statement.Entries.Count;
                            peak = Math.Max(peak, GC.GetTotalMemory(false));
                        }

                        peak = Math.Max(peak, GC.GetTotalMemory(false));
                        GC.KeepAlive(all);
                    }
                    else
                    {
                        decimal sum = 0;
                        foreach (var part in parts)
                        {
                            if (part is EntryRead read)
                            {
                                sum += read.Entry.SignedAmount;
                                if (++entries % 10_000 == 0)
                                {
                                    peak = Math.Max(peak, GC.GetTotalMemory(false));
                                }
                            }
                        }

                        GC.KeepAlive(sum);
                    }
                }

                stopwatch.Stop();
                samples.Add((stopwatch.Elapsed.TotalSeconds, entries, GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore, peak));
            }

            var ordered = samples.OrderBy(s => s.Seconds).ToList();
            var median = ordered[ordered.Count / 2];
            yield return new Row(
                format,
                buffered ? "buffered (ToStatements, 1 run)" : "streaming",
                size,
                median.Entries,
                median.Seconds,
                ordered[0].Seconds,
                ordered[^1].Seconds,
                size / median.Seconds,
                median.Entries / median.Seconds,
                median.Allocated / Math.Max(1, median.Entries),
                median.PeakHeap / 1024.0 / 1024.0);
        }
    }

    internal sealed record Row(string Format, string Mode, double FileMb, long Entries, double Seconds, double MinSeconds, double MaxSeconds,
        double MbPerSecond, double EntriesPerSecond, long BytesPerEntry, double PeakHeapMb);
}
