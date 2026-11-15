using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BankRecon.Api.Infrastructure;
using BankRecon.Core.Matching;
using Npgsql;
using NpgsqlTypes;

namespace BankRecon.Api.Reconciliation;

public sealed record RunSummary(
    long RunId,
    string Account,
    int BankLines,
    int LedgerItems,
    int Matches,
    IReadOnlyDictionary<string, int> MatchesByRule,
    int Queued,
    int AutoResolved,
    double DurationMs);

/// <summary>
/// One reconciliation run for one account: every open bank line against every open ledger item (open-item model,
/// so yesterday's unmatched lines get another chance when today's ledger arrives). The run takes a per-account
/// advisory lock, locks the open rows it reads, books matches, resolves work items whose subject just matched and
/// queues what is left, all in one transaction.
/// </summary>
internal sealed partial class ReconciliationService(
    NpgsqlDataSource dataSource,
    MatchingOptions options,
    ReconMetrics metrics,
    ILogger<ReconciliationService> logger)
{
    public async Task<RunSummary> RunAsync(string account, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        // Two runs for the same account would read the same open rows; the second waits instead of double-booking.
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('recon:' || $1, 0))", connection, tx))
        {
            lockCmd.Parameters.Add(new() { Value = account });
            await lockCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        long runId;
        await using (var insertRun = new NpgsqlCommand("INSERT INTO recon.recon_run (account, options) VALUES ($1, $2) RETURNING id", connection, tx))
        {
            insertRun.Parameters.Add(new() { Value = account });
            insertRun.Parameters.Add(new() { Value = JsonSerializer.Serialize(options), NpgsqlDbType = NpgsqlDbType.Jsonb });
            runId = (long)(await insertRun.ExecuteScalarAsync(cancellationToken))!;
        }

        // Lock order everywhere: bank lines, then ledger items, then work items (ascending id). The work queue's
        // resolve path follows the same order, so the two cannot deadlock.
        var bankLines = await LoadBankLinesAsync(connection, tx, account, cancellationToken);
        var ledgerItems = await LoadLedgerItemsAsync(connection, tx, account, cancellationToken);
        var result = new MatchingEngine(options).Match(bankLines, ledgerItems);

        var matchedBanks = new List<long>();
        var matchedLedger = new List<long>();
        foreach (var match in result.Matches)
        {
            await InsertMatchAsync(connection, tx, runId, account, match, $"run:{runId}", cancellationToken);
            matchedBanks.AddRange(match.BankLineIds.Select(Id));
            matchedLedger.AddRange(match.LedgerItemIds.Select(Id));
        }

        await SetStatusAsync(connection, tx, "bank_line", matchedBanks, cancellationToken);
        await SetStatusAsync(connection, tx, "ledger_item", matchedLedger, cancellationToken);

        var autoResolved = await ResolveMatchedWorkItemsAsync(connection, tx, runId, matchedBanks, matchedLedger, cancellationToken);
        var amounts = bankLines.ToDictionary(b => b.Id, b => b.Amount, StringComparer.Ordinal);
        var ledgerAmounts = ledgerItems.ToDictionary(l => l.Id, l => l.Amount, StringComparer.Ordinal);
        var queued = await QueueAsync(connection, tx, account, "bank_line", result.UnmatchedBankLines, amounts, cancellationToken);
        queued += await QueueAsync(connection, tx, account, "ledger_item", result.UnmatchedLedgerItems, ledgerAmounts, cancellationToken);

        await using (var finish = new NpgsqlCommand(
            "UPDATE recon.recon_run SET finished_at = now(), bank_lines = $2, ledger_items = $3, matches = $4, queued = $5, auto_resolved = $6 WHERE id = $1",
            connection, tx))
        {
            finish.Parameters.Add(new() { Value = runId });
            finish.Parameters.Add(new() { Value = bankLines.Count });
            finish.Parameters.Add(new() { Value = ledgerItems.Count });
            finish.Parameters.Add(new() { Value = result.Matches.Count });
            finish.Parameters.Add(new() { Value = queued });
            finish.Parameters.Add(new() { Value = autoResolved });
            await finish.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        var byRule = result.Matches.GroupBy(m => m.Rule.ToString()).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var (rule, count) in byRule)
        {
            metrics.Matched(rule, count);
        }

        foreach (var reason in result.UnmatchedBankLines.Concat(result.UnmatchedLedgerItems).GroupBy(u => u.Reason))
        {
            metrics.Queued(reason.Key.ToString(), reason.Count());
        }

        metrics.RunCompleted(stopwatch.Elapsed);
        LogRun(runId, account, bankLines.Count, ledgerItems.Count, result.Matches.Count, queued, stopwatch.ElapsedMilliseconds);
        return new RunSummary(runId, account, bankLines.Count, ledgerItems.Count, result.Matches.Count, byRule, queued, autoResolved,
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1));
    }

    internal static async Task InsertMatchAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long? runId, string account, Match match, string decidedBy, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("""
            WITH m AS (
                INSERT INTO recon.match (run_id, account, rule, score, amount_difference, explanation, decided_by)
                VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id),
            b AS (INSERT INTO recon.match_bank_line (match_id, bank_line_id) SELECT m.id, unnest($8::bigint[]) FROM m)
            INSERT INTO recon.match_ledger_item (match_id, ledger_item_id) SELECT m.id, unnest($9::bigint[]) FROM m
            """, connection, tx);
        cmd.Parameters.Add(new() { Value = (object?)runId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new() { Value = account });
        cmd.Parameters.Add(new() { Value = match.Rule.ToString() });
        cmd.Parameters.Add(new() { Value = (decimal)Math.Round(match.Score, 4) });
        cmd.Parameters.Add(new() { Value = match.AmountDifference });
        cmd.Parameters.Add(new() { Value = match.Explanation });
        cmd.Parameters.Add(new() { Value = decidedBy });
        cmd.Parameters.Add(new() { Value = match.BankLineIds.Select(Id).ToArray() });
        cmd.Parameters.Add(new() { Value = match.LedgerItemIds.Select(Id).ToArray() });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task SetStatusAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string table, IReadOnlyList<long> ids, CancellationToken cancellationToken, string status = "matched")
    {
        if (ids.Count == 0)
        {
            return;
        }

        // table is one of two constants chosen by the caller, never user input.
        var sql = table switch
        {
            "bank_line" => "UPDATE recon.bank_line SET status = $2 WHERE id = ANY($1) AND status = 'open'",
            "ledger_item" => "UPDATE recon.ledger_item SET status = $2 WHERE id = ANY($1) AND status = 'open'",
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        cmd.Parameters.Add(new() { Value = ids.ToArray() });
        cmd.Parameters.Add(new() { Value = status });
        var updated = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (updated != ids.Count)
        {
            // The rows were locked when read, so this means a bug, not a race: refuse to commit a partial booking.
            throw new InvalidOperationException($"Expected to update {ids.Count} open {table} rows, updated {updated}.");
        }
    }

    internal static async Task<int> ResolveMatchedWorkItemsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long? runId, IReadOnlyList<long> banks, IReadOnlyList<long> ledger, CancellationToken cancellationToken, string? resolvedBy = null)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE recon.work_item SET status = 'resolved', resolution = 'matched', resolved_by = $3, resolved_at = now(),
                   claimed_by = NULL, lease_expires_at = NULL, updated_at = now()
            WHERE id IN (
                SELECT id FROM recon.work_item
                WHERE status <> 'resolved'
                  AND ((subject_kind = 'bank_line' AND subject_id = ANY($1)) OR (subject_kind = 'ledger_item' AND subject_id = ANY($2)))
                ORDER BY id FOR UPDATE)
            """, connection, tx);
        cmd.Parameters.Add(new() { Value = banks.ToArray() });
        cmd.Parameters.Add(new() { Value = ledger.ToArray() });
        cmd.Parameters.Add(new() { Value = resolvedBy ?? $"run:{runId}" });
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> QueueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string account,
        string kind,
        IReadOnlyList<Unmatched> unmatched,
        Dictionary<string, decimal> amounts,
        CancellationToken cancellationToken)
    {
        if (unmatched.Count == 0)
        {
            return 0;
        }

        // An item already in the queue keeps its claim and history; only the matcher's view (reason, candidates) is refreshed.
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO recon.work_item (account, subject_kind, subject_id, reason, candidates, priority)
            SELECT $1, $2, s.id, s.reason, s.candidates, s.priority
            FROM unnest($3::bigint[], $4::text[], $5::jsonb[], $6::numeric[]) AS s(id, reason, candidates, priority)
            ON CONFLICT (subject_kind, subject_id) WHERE status <> 'resolved'
            DO UPDATE SET reason = EXCLUDED.reason, candidates = EXCLUDED.candidates, updated_at = now()
            """, connection, tx);
        cmd.Parameters.Add(new() { Value = account });
        cmd.Parameters.Add(new() { Value = kind });
        cmd.Parameters.Add(new() { Value = unmatched.Select(u => Id(u.Id)).ToArray() });
        cmd.Parameters.Add(new() { Value = unmatched.Select(u => u.Reason.ToString()).ToArray() });
        cmd.Parameters.Add(new()
        {
            Value = unmatched.Select(u => JsonSerializer.Serialize(u.Candidates.Select(c => new { id = Id(c.Id), score = c.Score }))).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb,
        });
        cmd.Parameters.Add(new() { Value = unmatched.Select(u => Math.Abs(amounts[u.Id])).ToArray() });
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<BankLine>> LoadBankLinesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string account, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id, amount, currency, value_date, customer_reference, end_to_end_id, remittance, narrative, counterparty_name
            FROM recon.bank_line WHERE account = $1 AND status = 'open' ORDER BY id FOR UPDATE
            """, connection, tx);
        cmd.Parameters.Add(new() { Value = account });
        var lines = new List<BankLine>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new BankLine
            {
                Id = reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                Amount = reader.GetDecimal(1),
                Currency = reader.GetString(2),
                ValueDate = reader.GetFieldValue<DateOnly>(3),
                CustomerReference = Text(reader, 4),
                EndToEndId = Text(reader, 5),
                Remittance = Text(reader, 6),
                Narrative = Text(reader, 7),
                CounterpartyName = Text(reader, 8),
            });
        }

        return lines;
    }

    private static async Task<List<LedgerItem>> LoadLedgerItemsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string account, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id, amount, currency, expected_date, reference, counterparty_name
            FROM recon.ledger_item WHERE account = $1 AND status = 'open' ORDER BY id FOR UPDATE
            """, connection, tx);
        cmd.Parameters.Add(new() { Value = account });
        var items = new List<LedgerItem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new LedgerItem
            {
                Id = reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                Amount = reader.GetDecimal(1),
                Currency = reader.GetString(2),
                ExpectedDate = reader.GetFieldValue<DateOnly>(3),
                Reference = Text(reader, 4),
                CounterpartyName = Text(reader, 5),
            });
        }

        return items;
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    internal static long Id(string id) => long.Parse(id, CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} for {Account}: {BankLines} bank lines, {LedgerItems} ledger items, {Matches} matches, {Queued} queued in {ElapsedMs} ms")]
    private partial void LogRun(long runId, string account, int bankLines, int ledgerItems, int matches, int queued, long elapsedMs);
}
