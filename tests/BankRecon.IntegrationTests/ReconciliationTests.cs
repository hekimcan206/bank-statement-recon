using System.Net;
using System.Net.Http.Json;
using BankRecon.Api.WorkQueue;
using BankRecon.IntegrationTests.Infrastructure;
using BankRecon.Synthetic;
using Npgsql;

namespace BankRecon.IntegrationTests;

public sealed class ReconciliationTests(PostgresFixture db) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(db.AppConnectionString);
        _client = _factory.CreateApiClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Theory]
    [InlineData(NarrativeStyle.TurkishFreeText)]
    [InlineData(NarrativeStyle.Camt)]
    public async Task A_run_books_matches_queues_the_rest_and_a_second_run_changes_nothing(NarrativeStyle style)
    {
        var scenario = TestApi.NewScenario(300, style);
        var account = scenario.Statement.Header.Account;
        await _client.LoadLedgerAsync(scenario);
        (await _client.UploadAsync(TestApi.File(scenario))).EnsureSuccessStatusCode();

        var run = await _client.RunAsync(account);

        run.Matches.Should().BeGreaterThan(150);
        run.BankLines.Should().Be(scenario.BankLines().Count);
        run.LedgerItems.Should().Be(scenario.Ledger.Count);
        var counts = await CountsAsync(account);
        counts.OpenBank.Should().Be(counts.QueuedBank, "every open bank line has exactly one work item");
        counts.OpenLedger.Should().Be(counts.QueuedLedger);
        counts.MatchedBank.Should().Be(counts.LinkedBank, "a matched line belongs to exactly one match");
        counts.MatchedLedger.Should().Be(counts.LinkedLedger);

        var again = await _client.RunAsync(account);
        again.Matches.Should().Be(0);
        (await CountsAsync(account)).Should().Be(counts, "re-running neither books twice nor duplicates queue items");
    }

    [Fact]
    public async Task Same_matches_as_the_in_memory_engine()
    {
        var scenario = TestApi.NewScenario(250);
        await _client.LoadLedgerAsync(scenario);
        (await _client.UploadAsync(TestApi.File(scenario))).EnsureSuccessStatusCode();
        var run = await _client.RunAsync(scenario.Statement.Header.Account);

        var parsed = TestApi.File(scenario);
        using var stream = new MemoryStream(parsed);
        var statement = Core.Statements.StatementStreamExtensions.ToStatements(Core.Statements.StatementFile.Read(stream, out _)).Single();
        var expected = new Core.Matching.MatchingEngine().Match((scenario with { Statement = statement }).BankLines(), scenario.Ledger);

        run.Matches.Should().Be(expected.Matches.Count);
        run.MatchesByRule.Should().BeEquivalentTo(expected.Matches.GroupBy(m => m.Rule.ToString()).ToDictionary(g => g.Key, g => g.Count()));
    }

    [Fact]
    public async Task A_ledger_item_that_arrives_later_is_matched_by_the_next_run_and_its_work_item_resolves_itself()
    {
        var scenario = TestApi.NewScenario(120);
        var account = scenario.Statement.Header.Account;
        (await _client.UploadAsync(TestApi.File(scenario))).EnsureSuccessStatusCode();
        var first = await _client.RunAsync(account);
        first.Matches.Should().Be(0, "no ledger yet");
        first.Queued.Should().Be(scenario.BankLines().Count);

        await _client.LoadLedgerAsync(scenario);
        var second = await _client.RunAsync(account);

        second.Matches.Should().BeGreaterThan(50);
        second.AutoResolved.Should().BeGreaterThanOrEqualTo(second.Matches, "each matched bank line had a work item from the first run");
    }

    [Fact]
    public async Task Concurrent_runs_for_one_account_serialise_and_book_each_line_once()
    {
        var scenario = TestApi.NewScenario(300);
        var account = scenario.Statement.Header.Account;
        await _client.LoadLedgerAsync(scenario);
        (await _client.UploadAsync(TestApi.File(scenario))).EnsureSuccessStatusCode();

        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _client.RunAsync(account)));

        runs.Count(r => r.Matches > 0).Should().Be(1, "the advisory lock lets one run book, the others find nothing left");
        var counts = await CountsAsync(account);
        counts.MatchedBank.Should().Be(counts.LinkedBank);
    }

    private async Task<Counts> CountsAsync(string account)
    {
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM recon.bank_line WHERE account = $1 AND status = 'open'),
              (SELECT count(*) FROM recon.bank_line WHERE account = $1 AND status = 'matched'),
              (SELECT count(*) FROM recon.match_bank_line mb JOIN recon.bank_line b ON b.id = mb.bank_line_id WHERE b.account = $1),
              (SELECT count(*) FROM recon.ledger_item WHERE account = $1 AND status = 'open'),
              (SELECT count(*) FROM recon.ledger_item WHERE account = $1 AND status = 'matched'),
              (SELECT count(*) FROM recon.match_ledger_item ml JOIN recon.ledger_item l ON l.id = ml.ledger_item_id WHERE l.account = $1),
              (SELECT count(*) FROM recon.work_item WHERE account = $1 AND status <> 'resolved' AND subject_kind = 'bank_line'),
              (SELECT count(*) FROM recon.work_item WHERE account = $1 AND status <> 'resolved' AND subject_kind = 'ledger_item')
            """, connection);
        cmd.Parameters.Add(new() { Value = account });
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return new Counts(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7));
    }

    private sealed record Counts(long OpenBank, long MatchedBank, long LinkedBank, long OpenLedger, long MatchedLedger, long LinkedLedger, long QueuedBank, long QueuedLedger);
}

public sealed class WorkQueueTests(PostgresFixture db) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(db.AppConnectionString);
        _client = _factory.CreateApiClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<string> QueuedAccountAsync(int ledgerItems = 120)
    {
        // Statement without ledger: every bank line ends up in the queue.
        var scenario = TestApi.NewScenario(ledgerItems);
        (await _client.UploadAsync(TestApi.File(scenario))).EnsureSuccessStatusCode();
        await _client.RunAsync(scenario.Statement.Header.Account);
        return scenario.Statement.Header.Account;
    }

    [Fact]
    public async Task Concurrent_workers_never_receive_the_same_item()
    {
        var account = await QueuedAccountAsync(200);
        var claimed = new System.Collections.Concurrent.ConcurrentBag<long>();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async w =>
        {
            while (await _client.ClaimAsync($"worker-{w}", account) is { } item)
            {
                claimed.Add(item.Id);
            }
        }));

        claimed.Should().OnlyHaveUniqueItems();
        var open = await _client.GetFromJsonAsync<List<WorkItem>>($"/api/v1/work-items?account={account}&status=open&limit=500");
        open.Should().BeEmpty();
        claimed.Count.Should().BeGreaterThan(100);
    }

    [Fact]
    public async Task Claims_come_highest_amount_first()
    {
        var account = await QueuedAccountAsync();
        var first = (await _client.ClaimAsync("w", account))!;
        var second = (await _client.ClaimAsync("w", account))!;
        first.Priority.Should().BeGreaterThanOrEqualTo(second.Priority);
    }

    [Fact]
    public async Task An_expired_lease_is_reclaimed_and_the_old_holder_is_fenced_out()
    {
        var account = await QueuedAccountAsync();
        var mine = (await _client.ClaimAsync("slow-worker", account))!;
        await ExpireLeaseAsync(mine.Id);

        var theirs = (await _client.ClaimAsync("fast-worker", account))!;
        theirs.Id.Should().Be(mine.Id, "an expired lease makes the item claimable again, and it is the highest priority");
        theirs.LeaseToken.Should().Be(mine.LeaseToken + 1);
        theirs.Attempts.Should().Be(2);

        var stale = await _client.ResolveAsync(mine, "slow-worker", "write-off", note: "bank fee");
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var heartbeat = await _client.PostAsJsonAsync($"/api/v1/work-items/{mine.Id}/heartbeat", new { workerId = "slow-worker", leaseToken = mine.LeaseToken });
        heartbeat.StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await _client.ResolveAsync(theirs, "fast-worker", "write-off", note: "bank fee")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Heartbeat_extends_and_release_returns_the_item()
    {
        var account = await QueuedAccountAsync();
        var item = (await _client.ClaimAsync("w1", account, leaseSeconds: 30))!;

        var extended = await _client.PostAsJsonAsync($"/api/v1/work-items/{item.Id}/heartbeat", new { workerId = "w1", leaseToken = item.LeaseToken, leaseSeconds = 600 });
        extended.EnsureSuccessStatusCode();
        (await extended.Content.ReadFromJsonAsync<WorkItem>())!.LeaseExpiresAt.Should().BeAfter(item.LeaseExpiresAt!.Value);

        (await _client.PostAsJsonAsync($"/api/v1/work-items/{item.Id}/release", new { workerId = "w1", leaseToken = item.LeaseToken })).EnsureSuccessStatusCode();
        (await _client.ClaimAsync("w2", account))!.Id.Should().Be(item.Id);
    }

    [Fact]
    public async Task A_manual_match_books_both_sides_and_resolves_the_counterparts_work_item()
    {
        var scenario = TestApi.NewScenario(80);
        var account = scenario.Statement.Header.Account;
        (await _client.UploadAsync(TestApi.File(scenario))).EnsureSuccessStatusCode();
        await _client.LoadLedgerAsync(scenario);

        var truth = scenario.Truth.First(t => t.Kind == "clean");
        var (bankId, ledgerId) = await IdsAsync(account, truth);
        await _client.RunAsync(account);
        await using (var conn = new NpgsqlConnection(db.SuperuserConnectionString))
        {
            // Undo the engine's booking of this one pair so the queue holds it: simulates "the matcher was unsure".
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                WITH m AS (SELECT match_id FROM recon.match_bank_line WHERE bank_line_id = $1),
                     d1 AS (DELETE FROM recon.match_bank_line WHERE match_id IN (SELECT match_id FROM m)),
                     d2 AS (DELETE FROM recon.match_ledger_item WHERE match_id IN (SELECT match_id FROM m)),
                     u1 AS (UPDATE recon.bank_line SET status = 'open' WHERE id = $1),
                     u2 AS (UPDATE recon.ledger_item SET status = 'open' WHERE id = $2)
                INSERT INTO recon.work_item (account, subject_kind, subject_id, reason, priority)
                VALUES ($3, 'bank_line', $1, 'Ambiguous', 1e12), ($3, 'ledger_item', $2, 'Ambiguous', 1e12 - 1)
                """, conn);
            cmd.Parameters.Add(new() { Value = bankId });
            cmd.Parameters.Add(new() { Value = ledgerId });
            cmd.Parameters.Add(new() { Value = account });
            await cmd.ExecuteNonQueryAsync();
        }

        var bankItem = (await _client.ClaimAsync("alice", account))!;
        var ledgerItem = (await _client.ClaimAsync("bob", account))!;
        bankItem.SubjectId.Should().Be(bankId);
        ledgerItem.SubjectId.Should().Be(ledgerId);

        var resolved = await _client.ResolveAsync(bankItem, "alice", "match", [ledgerId], "same invoice, payer typo");
        resolved.StatusCode.Should().Be(HttpStatusCode.OK);

        var bobsAttempt = await _client.ResolveAsync(ledgerItem, "bob", "match", [bankId]);
        bobsAttempt.StatusCode.Should().Be(HttpStatusCode.Conflict, "alice's match resolved bob's item too");
        var bobsView = await _client.GetFromJsonAsync<WorkItem>($"/api/v1/work-items/{ledgerItem.Id}");
        bobsView!.Status.Should().Be("resolved");
    }

    [Fact]
    public async Task Write_off_needs_a_note_and_a_match_must_stay_within_the_manual_tolerance()
    {
        var account = await QueuedAccountAsync();
        var item = (await _client.ClaimAsync("w", account))!;

        (await _client.ResolveAsync(item, "w", "write-off")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var far = await AddLedgerItemAsync(account, 0.01m);
        (await _client.ResolveAsync(item, "w", "match", [far])).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await _client.ResolveAsync(item, "w", "write-off", note: "bank charge")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<long> AddLedgerItemAsync(string account, decimal amount)
    {
        var externalId = "manual-" + Guid.NewGuid().ToString("N")[..8];
        (await _client.PostAsJsonAsync("/api/v1/ledger-items", new[]
        {
            new { externalId, account, currency = "TRY", amount, expectedDate = new DateOnly(2026, 11, 10), reference = (string?)null, counterpartyName = (string?)null },
        })).EnsureSuccessStatusCode();
        await using var conn = new NpgsqlConnection(db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT id FROM recon.ledger_item WHERE external_id = $1", conn);
        cmd.Parameters.Add(new() { Value = externalId });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(long Bank, long Ledger)> IdsAsync(string account, TruthGroup truth)
    {
        var entryIndex = int.Parse(truth.BankLineIds[0][1..], System.Globalization.CultureInfo.InvariantCulture);
        await using var conn = new NpgsqlConnection(db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT (SELECT b.id FROM recon.bank_line b WHERE b.account = $1 AND b.entry_index = $2),
                   (SELECT l.id FROM recon.ledger_item l WHERE l.account = $1 AND l.external_id = $3)
            """, conn);
        cmd.Parameters.Add(new() { Value = account });
        cmd.Parameters.Add(new() { Value = entryIndex });
        cmd.Parameters.Add(new() { Value = truth.LedgerItemIds[0] });
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.GetInt64(0), r.GetInt64(1));
    }

    private async Task ExpireLeaseAsync(long id)
    {
        await using var conn = new NpgsqlConnection(db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE recon.work_item SET lease_expires_at = now() - interval '1 second' WHERE id = $1", conn);
        cmd.Parameters.Add(new() { Value = id });
        await cmd.ExecuteNonQueryAsync();
    }
}
