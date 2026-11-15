using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace BankRecon.Api.Infrastructure;

/// <summary>
/// Turns transient contention into a retryable 503 instead of a 500: the transaction rolled back completely, so
/// repeating the request is safe (imports are idempotent by file hash, queue operations are fenced by lease token).
/// </summary>
internal sealed class DatabaseExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } pg)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = "1";
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "The account is busy.",
                Detail = "The request lost a lock race (for example with a reconciliation run) and was rolled back. Retry it.",
                Extensions = { ["code"] = "recon.busy", ["sqlState"] = pg.SqlState },
            },
        });
    }
}
