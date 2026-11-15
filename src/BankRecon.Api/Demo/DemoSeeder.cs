using System.Text;
using System.Text.Json;
using BankRecon.Api.Features;
using BankRecon.Api.Reconciliation;
using BankRecon.Core.Statements.Camt;
using BankRecon.Core.Statements.Mt940;
using BankRecon.Synthetic;

namespace BankRecon.Api.Demo;

/// <summary>
/// `--seed-demo`: generates two synthetic accounts (one MT940 with Turkish free-text narratives, one camt.053.001.08),
/// imports their ledger and statement files through the same services the API uses, and runs reconciliation.
/// Idempotent: a second run finds the files and ledger items already there and books nothing twice.
/// </summary>
internal sealed class DemoSeeder(StatementImporter importer, LedgerItemStore ledger, ReconciliationService reconciliation)
{
    public async Task<object> SeedAsync(CancellationToken cancellationToken)
    {
        var results = new List<object>();
        foreach (var (seed, style) in new[] { (11, NarrativeStyle.TurkishFreeText), (12, NarrativeStyle.Camt) })
        {
            var scenario = ScenarioGenerator.Generate(new ScenarioOptions { Seed = seed, LedgerItems = 400, Style = style });
            var account = scenario.Statement.Header.Account;
            var items = scenario.Ledger.Select(l => new LedgerItemInput(l.Id, account, l.Currency, l.Amount, l.ExpectedDate, l.Reference, l.CounterpartyName)).ToList();
            var ledgerResult = await ledger.InsertAsync(items, cancellationToken);

            byte[] file;
            if (style == NarrativeStyle.Camt)
            {
                using var stream = new MemoryStream();
                Camt053Writer.Write(stream, [scenario.Statement]);
                file = stream.ToArray();
            }
            else
            {
                file = Encoding.UTF8.GetBytes(Mt940Writer.WriteToString([scenario.Statement], new Mt940WriteOptions { XCharsetOnly = false }));
            }

            using var body = new MemoryStream(file);
            var import = await importer.ImportAsync(body, style == NarrativeStyle.Camt ? "demo.camt053.xml" : "demo.mt940.sta", cancellationToken);
            var run = await reconciliation.RunAsync(account, cancellationToken);
            results.Add(new { account, style = style.ToString(), ledger = ledgerResult, duplicateFile = import.Duplicate, run });
        }

        return results;
    }

    public static string ToJson(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
}
