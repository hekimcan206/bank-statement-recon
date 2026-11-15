using BankRecon.Api.Reconciliation;
using BankRecon.Api.WorkQueue;
using BankRecon.Core.Statements;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace BankRecon.Api.Features;

internal static class Endpoints
{
    /// <summary>Upload limit. A busy corporate account's daily camt.053 is a few MB; 64 MB leaves room without inviting abuse.</summary>
    public const long MaxStatementBytes = 64L * 1024 * 1024;

    public static RouteGroupBuilder MapReconApi(this RouteGroupBuilder api)
    {
        api.MapPost("/statements", ImportStatementAsync)
            .WithSummary("Import an MT940 or camt.053 file (raw request body). The same bytes twice are reported as a duplicate.")
            .Accepts<string>("text/plain", "application/xml", "application/octet-stream")
            .Produces<ImportResult>(StatusCodes.Status201Created)
            .Produces<ImportResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        api.MapPost("/ledger-items", ImportLedgerAsync)
            .WithSummary("Add open ledger items (expected receipts and payments). Idempotent per account and external id.");

        api.MapPost("/accounts/{account}/reconciliation-runs", RunAsync)
            .WithSummary("Match every open bank line of the account against its open ledger items and queue the rest.");

        api.MapGet("/accounts/{account}/summary", SummaryAsync)
            .WithSummary("Open, matched and written-off counts plus the work queue depth for one account.");

        api.MapGet("/work-items", ListWorkItemsAsync).WithSummary("List work items (highest amount first).");
        api.MapGet("/work-items/{id:long}", GetWorkItemAsync);
        api.MapPost("/work-items/claim", ClaimAsync)
            .WithSummary("Lease the next unclaimed item. 204 when the queue is empty. Keep the returned leaseToken.");
        api.MapPost("/work-items/{id:long}/heartbeat", HeartbeatAsync).WithSummary("Extend a lease you still hold.");
        api.MapPost("/work-items/{id:long}/release", ReleaseAsync).WithSummary("Give an item back to the queue.");
        api.MapPost("/work-items/{id:long}/resolve", ResolveAsync)
            .WithSummary("Book a manual match or a write-off. 409 when the lease was lost.");
        return api;
    }

    private static async Task<IResult> ImportStatementAsync(HttpContext context, StatementImporter importer, [FromQuery] string? fileName, CancellationToken cancellationToken)
    {
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = MaxStatementBytes;
        }

        if (context.Request.ContentLength > MaxStatementBytes)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Statement file too large.");
        }

        try
        {
            var result = await importer.ImportAsync(context.Request.Body, fileName, cancellationToken);
            return result.Duplicate ? Results.Ok(result) : Results.Created($"/api/v1/statement-files/{result.FileId}", result);
        }
        catch (StatementFormatException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "The file is not a valid statement.", detail: ex.Message,
                extensions: new Dictionary<string, object?> { ["code"] = "statement.malformed", ["line"] = ex.Line });
        }
        catch (ImportRejectedException ex)
        {
            return Results.Problem(statusCode: ex.StatusCode, title: "The statement was not imported.", detail: ex.Message,
                extensions: new Dictionary<string, object?> { ["code"] = ex.StatusCode == 409 ? "statement.duplicate" : "statement.unbalanced" });
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Statement file too large.");
        }
    }

    private static async Task<IResult> ImportLedgerAsync(LedgerItemInput[] items, LedgerItemStore store, CancellationToken cancellationToken)
    {
        var errors = LedgerItemStore.Validate(items);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["items"] = [.. errors] });
        }

        return Results.Ok(await store.InsertAsync(items, cancellationToken));
    }

    private static async Task<IResult> RunAsync(string account, ReconciliationService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.RunAsync(LedgerItemStore.Iban(account), cancellationToken));

    private static async Task<IResult> SummaryAsync(string account, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT
              (SELECT count(*) FILTER (WHERE status = 'open') FROM recon.bank_line WHERE account = $1),
              (SELECT count(*) FILTER (WHERE status = 'matched') FROM recon.bank_line WHERE account = $1),
              (SELECT count(*) FILTER (WHERE status = 'written_off') FROM recon.bank_line WHERE account = $1),
              (SELECT count(*) FILTER (WHERE status = 'open') FROM recon.ledger_item WHERE account = $1),
              (SELECT count(*) FILTER (WHERE status = 'matched') FROM recon.ledger_item WHERE account = $1),
              (SELECT count(*) FILTER (WHERE status = 'written_off') FROM recon.ledger_item WHERE account = $1),
              (SELECT count(*) FILTER (WHERE status = 'open') FROM recon.work_item WHERE account = $1),
              (SELECT count(*) FILTER (WHERE status = 'claimed') FROM recon.work_item WHERE account = $1),
              (SELECT count(*) FILTER (WHERE status = 'resolved') FROM recon.work_item WHERE account = $1),
              (SELECT count(*) FROM recon.match WHERE account = $1)
            """);
        cmd.Parameters.Add(new() { Value = LedgerItemStore.Iban(account) });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return Results.Ok(new
        {
            account = LedgerItemStore.Iban(account),
            bankLines = new { open = reader.GetInt64(0), matched = reader.GetInt64(1), writtenOff = reader.GetInt64(2) },
            ledgerItems = new { open = reader.GetInt64(3), matched = reader.GetInt64(4), writtenOff = reader.GetInt64(5) },
            workItems = new { open = reader.GetInt64(6), claimed = reader.GetInt64(7), resolved = reader.GetInt64(8) },
            matches = reader.GetInt64(9),
        });
    }

    private static async Task<IResult> ListWorkItemsAsync(WorkQueueService queue, string? account, string? status, int? limit, CancellationToken cancellationToken) =>
        Results.Ok(await queue.ListAsync(account is null ? null : LedgerItemStore.Iban(account), status, limit ?? 50, cancellationToken));

    private static async Task<IResult> GetWorkItemAsync(long id, WorkQueueService queue, CancellationToken cancellationToken) =>
        await queue.GetAsync(id, cancellationToken) is { } item ? Results.Ok(item) : Results.NotFound();

    public sealed record ClaimRequest(string WorkerId, int? LeaseSeconds, string? Account);

    public sealed record LeaseRequest(string WorkerId, long LeaseToken, int? LeaseSeconds);

    private static async Task<IResult> ClaimAsync(ClaimRequest request, WorkQueueService queue, CancellationToken cancellationToken)
    {
        if (!ValidWorker(request.WorkerId))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["workerId"] = ["1-64 characters required."] });
        }

        var item = await queue.ClaimAsync(request.WorkerId, request.LeaseSeconds, request.Account is null ? null : LedgerItemStore.Iban(request.Account), cancellationToken);
        return item is null ? Results.NoContent() : Results.Ok(item);
    }

    private static async Task<IResult> HeartbeatAsync(long id, LeaseRequest request, WorkQueueService queue, CancellationToken cancellationToken) =>
        ToResult(await queue.HeartbeatAsync(id, request.WorkerId, request.LeaseToken, request.LeaseSeconds, cancellationToken));

    private static async Task<IResult> ReleaseAsync(long id, LeaseRequest request, WorkQueueService queue, CancellationToken cancellationToken) =>
        ToResult(await queue.ReleaseAsync(id, request.WorkerId, request.LeaseToken, cancellationToken));

    private static async Task<IResult> ResolveAsync(long id, ResolveRequest request, WorkQueueService queue, CancellationToken cancellationToken)
    {
        if (!ValidWorker(request.WorkerId) || request.Note?.Length > 500 || request.CounterpartIds?.Count > 100)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["workerId 1-64 characters, note up to 500 characters, at most 100 counterparts."] });
        }

        return ToResult(await queue.ResolveAsync(id, request, cancellationToken));
    }

    private static bool ValidWorker(string? workerId) => !string.IsNullOrWhiteSpace(workerId) && workerId.Length <= 64;

    private static IResult ToResult(QueueResult<WorkItem> result) => result.Outcome switch
    {
        QueueOutcome.Ok => Results.Ok(result.Value),
        QueueOutcome.NotFound => Results.NotFound(),
        QueueOutcome.LeaseLost => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Lease lost.", detail: result.Error,
            extensions: new Dictionary<string, object?> { ["code"] = "work_item.lease_lost" }),
        _ => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Resolution rejected.", detail: result.Error,
            extensions: new Dictionary<string, object?> { ["code"] = "work_item.invalid" }),
    };
}
