using System.Net;
using System.Net.Http.Json;
using BankRecon.IntegrationTests.Infrastructure;
using Npgsql;

namespace BankRecon.IntegrationTests;

/// <summary>
/// Two people working different queue items both decide that the same ledger item is theirs. The row locks and the
/// UNIQUE constraint on match_ledger_item must let exactly one booking through.
/// </summary>
public sealed class ManualMatchRaceTests(PostgresFixture db) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        // A wide manual tolerance, so both bookings are acceptable on amount and only the race decides.
        _factory = new ApiFactory(db.AppConnectionString, settings: new Dictionary<string, string> { ["WorkQueue:ManualTolerance"] = "100000000" });
        _client = _factory.CreateApiClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Exactly_one_of_two_concurrent_manual_matches_on_one_ledger_item_succeeds()
    {
        for (var round = 0; round < 10; round++)
        {
            var scenario = TestApi.NewScenario(40);
            var account = scenario.Statement.Header.Account;
            (await _client.UploadAsync(TestApi.File(scenario))).EnsureSuccessStatusCode();
            await _client.RunAsync(account);
            var ledgerId = await AddLedgerItemAsync(account);

            var a = (await _client.ClaimAsync("alice", account))!;
            var b = (await _client.ClaimAsync("bob", account))!;
            var responses = await Task.WhenAll(
                _client.ResolveAsync(a, "alice", "match", [ledgerId], "race"),
                _client.ResolveAsync(b, "bob", "match", [ledgerId], "race"));

            responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1, $"round {round}");
            responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).Should().Be(1, "the loser sees the ledger item already matched");
            (await LinksAsync(ledgerId)).Should().Be(1);
        }
    }

    private async Task<long> AddLedgerItemAsync(string account)
    {
        var externalId = "race-" + Guid.NewGuid().ToString("N")[..8];
        (await _client.PostAsJsonAsync("/api/v1/ledger-items", new[]
        {
            new { externalId, account, currency = "TRY", amount = 1000m, expectedDate = new DateOnly(2026, 11, 10), reference = "RACE", counterpartyName = "Race" },
        })).EnsureSuccessStatusCode();
        await using var conn = new NpgsqlConnection(db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT id FROM recon.ledger_item WHERE external_id = $1", conn);
        cmd.Parameters.Add(new() { Value = externalId });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> LinksAsync(long ledgerId)
    {
        await using var conn = new NpgsqlConnection(db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM recon.match_ledger_item WHERE ledger_item_id = $1", conn);
        cmd.Parameters.Add(new() { Value = ledgerId });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
