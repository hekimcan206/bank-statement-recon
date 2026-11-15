using System.Globalization;
using System.Text.Json;
using BankRecon.Lab;

var mode = args.Length > 0 ? args[0] : "help";
var output = Environment.GetEnvironmentVariable("LAB_OUTPUT") ?? "benchmark-results";
int Arg(int index, int fallback) => args.Length > index ? int.Parse(args[index], CultureInfo.InvariantCulture) : fallback;

switch (mode)
{
    case "accuracy":
        AccuracyLab.Run(seeds: Arg(1, 20), size: Arg(2, 2000), output);
        break;
    case "parse":
        ParseLab.Run(mt940Entries: Arg(1, 1_000_000), camtEntries: Arg(2, 250_000), runs: Arg(3, 5), output);
        break;
    case "throughput":
        MatchingThroughputLab.Run([1_000, 5_000, 10_000, 20_000], runs: Arg(1, 5), output);
        break;
    case "database":
        await DatabaseLab.RunAsync(output);
        break;
    case "explain":
        ExplainLab.Run(Enum.Parse<BankRecon.Synthetic.NarrativeStyle>(args[1]), Arg(2, 1), Arg(3, 1000));
        break;
    default:
        Console.WriteLine("""
            usage:
              accuracy   [seeds=20] [ledgerItems=2000]        precision/recall per matcher configuration and file format
              parse      [mt940=1000000] [camt=250000] [runs=5] streaming vs buffered parse throughput
              throughput [runs=5]                               in-memory matching time by ledger size
              database                                          import, reconciliation run and queue drain on PostgreSQL (Testcontainers)
              explain    <style> [seed] [ledgerItems]           print every wrong pair of one scenario
            """);
        break;
}

internal static class LabJson
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
