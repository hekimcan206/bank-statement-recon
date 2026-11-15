using System.Net.Http.Json;
using System.Text;
using BankRecon.Api.WorkQueue;
using BankRecon.Core.Statements;
using BankRecon.Core.Statements.Camt;
using BankRecon.Core.Statements.Mt940;
using BankRecon.Synthetic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BankRecon.IntegrationTests.Infrastructure;

/// <summary>Boots the real application in memory; only configuration differs from production.</summary>
public sealed class ApiFactory(string connectionString, string? apiKey = TestApi.Key, IReadOnlyDictionary<string, string>? settings = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Api:Keys", apiKey ?? string.Empty);
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }
    }

    public HttpClient CreateApiClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApi.Key);
        client.Timeout = TimeSpan.FromMinutes(2);
        return client;
    }
}

public static class TestApi
{
    public const string Key = "integration-test-key";

    // Fresh container per run, so fixed seeds keep the tests deterministic; each call gets its own account.
    private static int _seed = 5000;

    /// <summary>A synthetic scenario whose account no other test uses.</summary>
    public static Scenario NewScenario(int ledgerItems, NarrativeStyle style = NarrativeStyle.TurkishFreeText) =>
        ScenarioGenerator.Generate(new ScenarioOptions { Seed = Interlocked.Increment(ref _seed), LedgerItems = ledgerItems, Style = style });

    public static byte[] File(Scenario scenario, Statement? statement = null)
    {
        statement ??= scenario.Statement;
        if (scenario.Options.Style == NarrativeStyle.Camt)
        {
            using var stream = new MemoryStream();
            Camt053Writer.Write(stream, [statement]);
            return stream.ToArray();
        }

        return Encoding.UTF8.GetBytes(Mt940Writer.WriteToString([statement]));
    }

    public static async Task<HttpResponseMessage> UploadAsync(this HttpClient client, byte[] file) =>
        await client.PostAsync("/api/v1/statements", new ByteArrayContent(file));

    public static async Task LoadLedgerAsync(this HttpClient client, Scenario scenario)
    {
        var account = scenario.Statement.Header.Account;
        var items = scenario.Ledger.Select(l => new
        {
            externalId = l.Id,
            account,
            currency = l.Currency,
            amount = l.Amount,
            expectedDate = l.ExpectedDate,
            reference = l.Reference,
            counterpartyName = l.CounterpartyName,
        });
        (await client.PostAsJsonAsync("/api/v1/ledger-items", items)).EnsureSuccessStatusCode();
    }

    public static async Task<RunDto> RunAsync(this HttpClient client, string account)
    {
        var response = await client.PostAsync($"/api/v1/accounts/{account}/reconciliation-runs", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RunDto>())!;
    }

    public static async Task<WorkItem?> ClaimAsync(this HttpClient client, string worker, string account, int? leaseSeconds = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/work-items/claim", new { workerId = worker, account, leaseSeconds });
        response.EnsureSuccessStatusCode();
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<WorkItem>();
    }

    public static Task<HttpResponseMessage> ResolveAsync(this HttpClient client, WorkItem item, string worker, string action, long[]? counterparts = null, string? note = null) =>
        client.PostAsJsonAsync($"/api/v1/work-items/{item.Id}/resolve", new { workerId = worker, leaseToken = item.LeaseToken, action, counterpartIds = counterparts, note });

    public sealed record RunDto(long RunId, string Account, int BankLines, int LedgerItems, int Matches, Dictionary<string, int> MatchesByRule, int Queued, int AutoResolved);

    public sealed record ImportDto(long FileId, string Format, bool Duplicate, List<ImportedStatementDto> Statements);

    public sealed record ImportedStatementDto(long Id, string StatementRef, string Account, int Entries, int BankLines);

    public sealed record ProblemDto(int Status, string? Title, string? Detail, string? Code);
}
