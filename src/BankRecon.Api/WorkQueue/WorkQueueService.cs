using System.Globalization;
using BankRecon.Api.Infrastructure;
using BankRecon.Api.Reconciliation;
using BankRecon.Core.Matching;
using Npgsql;

namespace BankRecon.Api.WorkQueue;

public sealed record WorkQueueOptions
{
    public int DefaultLeaseSeconds { get; init; } = 300;
    public int MaxLeaseSeconds { get; init; } = 1800;

    /// <summary>Largest difference between bank and ledger totals a person may book as a manual match.</summary>
    public decimal ManualTolerance { get; init; } = 100m;
}

public sealed record WorkItem(
    long Id,
    string Account,
    string SubjectKind,
    long SubjectId,
    string Reason,
    string Candidates,
    decimal Priority,
    string Status,
    string? ClaimedBy,
    DateTimeOffset? LeaseExpiresAt,
    long LeaseToken,
    int Attempts,
    string? Resolution);

public enum ResolveAction
{
    Match,
    WriteOff,
}

public sealed record ResolveRequest(string WorkerId, long LeaseToken, ResolveAction Action, IReadOnlyList<long>? CounterpartIds, string? Note);

public enum QueueOutcome
{
    Ok,
    NotFound,

    /// <summary>The caller's lease is gone: expired and re-claimed, released, or the item was resolved elsewhere.</summary>
    LeaseLost,

    /// <summary>The requested resolution is not acceptable (counterpart already matched, amounts too far apart, ...).</summary>
    Invalid,
}

public sealed record QueueResult<T>(QueueOutcome Outcome, T? Value, string? Error = null);

/// <summary>
/// The work queue for unmatched items. Claiming is a single UPDATE over a FOR UPDATE SKIP LOCKED subquery, so N
/// workers never block one another and never receive the same item. A claim is a lease, not ownership: when it
/// expires the item is claimable again, and the lease token (incremented on every claim) fences the previous holder
/// out of heartbeat, release and resolve.
/// </summary>
internal sealed class WorkQueueService(NpgsqlDataSource dataSource, WorkQueueOptions options, ReconMetrics metrics)
{
    private const string Columns =
        "id, account, subject_kind, subject_id, reason, candidates::text, priority, status, claimed_by, lease_expires_at, lease_token, attempts, resolution";

    public async Task<WorkItem?> ClaimAsync(string workerId, int? leaseSeconds, string? account, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"""
            UPDATE recon.work_item w
            SET status = 'claimed', claimed_by = $1, lease_expires_at = now() + make_interval(secs => $2),
                lease_token = w.lease_token + 1, attempts = w.attempts + 1, updated_at = now()
            WHERE w.id = (
                SELECT id FROM recon.work_item
                WHERE (status = 'open' OR (status = 'claimed' AND lease_expires_at < now()))
                  AND ($3::text IS NULL OR account = $3)
                ORDER BY priority DESC, id
                FOR UPDATE SKIP LOCKED
                LIMIT 1)
            RETURNING {Columns}
            """);
        cmd.Parameters.Add(new() { Value = workerId });
        cmd.Parameters.Add(new() { Value = (double)Lease(leaseSeconds) });
        cmd.Parameters.Add(new() { Value = (object?)account ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<QueueResult<WorkItem>> HeartbeatAsync(long id, string workerId, long leaseToken, int? leaseSeconds, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"""
            UPDATE recon.work_item SET lease_expires_at = now() + make_interval(secs => $4), updated_at = now()
            WHERE id = $1 AND status = 'claimed' AND claimed_by = $2 AND lease_token = $3
            RETURNING {Columns}
            """);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = workerId });
        cmd.Parameters.Add(new() { Value = leaseToken });
        cmd.Parameters.Add(new() { Value = (double)Lease(leaseSeconds) });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new(QueueOutcome.Ok, Read(reader));
        }

        await reader.DisposeAsync();
        return await FailureAsync<WorkItem>(id, cancellationToken);
    }

    public async Task<QueueResult<WorkItem>> ReleaseAsync(long id, string workerId, long leaseToken, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"""
            UPDATE recon.work_item SET status = 'open', claimed_by = NULL, lease_expires_at = NULL, updated_at = now()
            WHERE id = $1 AND status = 'claimed' AND claimed_by = $2 AND lease_token = $3
            RETURNING {Columns}
            """);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = workerId });
        cmd.Parameters.Add(new() { Value = leaseToken });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new(QueueOutcome.Ok, Read(reader));
        }

        await reader.DisposeAsync();
        return await FailureAsync<WorkItem>(id, cancellationToken);
    }

    /// <summary>
    /// Resolves a claimed item. A manual match books the subject against the given counterparts and resolves every
    /// other queue item whose subject is now matched; a write-off closes the subject without a counterpart (bank
    /// charges, an invoice that will never be paid). Lock order: bank lines, ledger items, work items.
    /// </summary>
    public async Task<QueueResult<WorkItem>> ResolveAsync(long id, ResolveRequest request, CancellationToken cancellationToken)
    {
        var peek = await GetAsync(id, cancellationToken);
        if (peek is null)
        {
            return new(QueueOutcome.NotFound, null);
        }

        var counterparts = (request.CounterpartIds ?? []).Distinct().Order().ToArray();
        if (request.Action == ResolveAction.Match && counterparts.Length == 0)
        {
            return new(QueueOutcome.Invalid, null, "A manual match needs at least one counterpart id.");
        }

        if (request.Action == ResolveAction.WriteOff && string.IsNullOrWhiteSpace(request.Note))
        {
            return new(QueueOutcome.Invalid, null, "A write-off needs a note explaining it.");
        }

        var bankIds = peek.SubjectKind == "bank_line" ? [peek.SubjectId] : request.Action == ResolveAction.Match ? counterparts : [];
        var ledgerIds = peek.SubjectKind == "ledger_item" ? [peek.SubjectId] : request.Action == ResolveAction.Match ? counterparts : [];

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        var banks = await LockAsync(connection, tx, "bank_line", bankIds, cancellationToken);
        var ledger = await LockAsync(connection, tx, "ledger_item", ledgerIds, cancellationToken);

        WorkItem? item;
        await using (var lockItem = new NpgsqlCommand($"SELECT {Columns} FROM recon.work_item WHERE id = $1 FOR UPDATE", connection, tx))
        {
            lockItem.Parameters.Add(new() { Value = id });
            await using var reader = await lockItem.ExecuteReaderAsync(cancellationToken);
            item = await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
        }

        if (item is null || item.Status != "claimed" || item.ClaimedBy != request.WorkerId || item.LeaseToken != request.LeaseToken)
        {
            metrics.LeaseConflict();
            return new(QueueOutcome.LeaseLost, item, "The lease is no longer held by this worker (expired and re-claimed, released, or resolved).");
        }

        if (banks.Count != bankIds.Length || ledger.Count != ledgerIds.Length)
        {
            return new(QueueOutcome.Invalid, item, "Unknown bank line or ledger item id.");
        }

        var all = banks.Concat(ledger).ToList();
        if (all.Any(r => r.Status != "open"))
        {
            return new(QueueOutcome.Invalid, item, "Some of these items are already matched or written off.");
        }

        if (all.Any(r => r.Account != item.Account || r.Currency != all[0].Currency))
        {
            return new(QueueOutcome.Invalid, item, "All items of a match must belong to the same account and currency.");
        }

        string resolution;
        if (request.Action == ResolveAction.Match)
        {
            var difference = banks.Sum(b => b.Amount) - ledger.Sum(l => l.Amount);
            if (Math.Abs(difference) > options.ManualTolerance)
            {
                return new(QueueOutcome.Invalid, item, string.Create(CultureInfo.InvariantCulture,
                    $"Bank and ledger totals differ by {difference:0.00}, more than the manual tolerance of {options.ManualTolerance:0.00}."));
            }

            var match = new Match(
                MatchRule.Manual,
                banks.Select(b => b.Id.ToString(CultureInfo.InvariantCulture)).ToList(),
                ledger.Select(l => l.Id.ToString(CultureInfo.InvariantCulture)).ToList(),
                1.0,
                difference,
                string.IsNullOrWhiteSpace(request.Note) ? "manual match" : "manual match: " + Truncate(request.Note, 500));
            await ReconciliationService.InsertMatchAsync(connection, tx, null, item.Account, match, "user:" + request.WorkerId, cancellationToken);
            await ReconciliationService.SetStatusAsync(connection, tx, "bank_line", bankIds, cancellationToken);
            await ReconciliationService.SetStatusAsync(connection, tx, "ledger_item", ledgerIds, cancellationToken);
            await ReconciliationService.ResolveMatchedWorkItemsAsync(connection, tx, null, bankIds, ledgerIds, cancellationToken, "user:" + request.WorkerId);
            resolution = "matched";
        }
        else
        {
            await ReconciliationService.SetStatusAsync(connection, tx, item.SubjectKind, [item.SubjectId], cancellationToken, "written_off");
            resolution = "written_off";
        }

        WorkItem resolved;
        await using (var finish = new NpgsqlCommand($"""
            UPDATE recon.work_item SET status = 'resolved', resolution = $2, resolution_note = $3, resolved_by = $4, resolved_at = now(),
                   claimed_by = NULL, lease_expires_at = NULL, updated_at = now()
            WHERE id = $1
            RETURNING {Columns}
            """, connection, tx))
        {
            finish.Parameters.Add(new() { Value = id });
            finish.Parameters.Add(new() { Value = resolution });
            finish.Parameters.Add(new() { Value = (object?)Truncate(request.Note, 500) ?? DBNull.Value });
            finish.Parameters.Add(new() { Value = "user:" + request.WorkerId });
            await using var reader = await finish.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            resolved = Read(reader);
        }

        await tx.CommitAsync(cancellationToken);
        return new(QueueOutcome.Ok, resolved);
    }

    public async Task<WorkItem?> GetAsync(long id, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {Columns} FROM recon.work_item WHERE id = $1");
        cmd.Parameters.Add(new() { Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<WorkItem>> ListAsync(string? account, string? status, int limit, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT {Columns} FROM recon.work_item
            WHERE ($1::text IS NULL OR account = $1) AND ($2::text IS NULL OR status = $2)
            ORDER BY priority DESC, id LIMIT $3
            """);
        cmd.Parameters.Add(new() { Value = (object?)account ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = (object?)status ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = Math.Clamp(limit, 1, 500) });
        var items = new List<WorkItem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    private int Lease(int? seconds) => Math.Clamp(seconds ?? options.DefaultLeaseSeconds, 5, options.MaxLeaseSeconds);

    private async Task<QueueResult<T>> FailureAsync<T>(long id, CancellationToken cancellationToken)
    {
        var item = await GetAsync(id, cancellationToken);
        if (item is null)
        {
            return new(QueueOutcome.NotFound, default);
        }

        metrics.LeaseConflict();
        return new(QueueOutcome.LeaseLost, default, "The lease is no longer held by this worker (expired and re-claimed, released, or resolved).");
    }

    private sealed record LockedRow(long Id, string Account, string Currency, decimal Amount, string Status);

    private static async Task<List<LockedRow>> LockAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string table, long[] ids, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            return [];
        }

        var sql = table switch
        {
            "bank_line" => "SELECT id, account, currency, amount, status FROM recon.bank_line WHERE id = ANY($1) ORDER BY id FOR UPDATE",
            "ledger_item" => "SELECT id, account, currency, amount, status FROM recon.ledger_item WHERE id = ANY($1) ORDER BY id FOR UPDATE",
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        cmd.Parameters.Add(new() { Value = ids });
        var rows = new List<LockedRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LockedRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3), reader.GetString(4)));
        }

        return rows;
    }

    private static WorkItem Read(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetInt64(3),
        r.GetString(4),
        r.GetString(5),
        r.GetDecimal(6),
        r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
        r.GetInt64(10),
        r.GetInt32(11),
        r.IsDBNull(12) ? null : r.GetString(12));

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
