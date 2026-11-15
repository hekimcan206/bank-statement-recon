using System.Security.Cryptography;
using System.Text;

namespace BankRecon.Api.Infrastructure;

public sealed record ApiOptions
{
    /// <summary>Accepted values of the X-Api-Key header. Empty means no key is required (local development only).</summary>
    public IReadOnlyList<string> Keys { get; init; } = [];
}

/// <summary>
/// Shared-key check for every /api route. Deliberately minimal: it keeps the demo from being an open endpoint for
/// bank data; a production deployment sits behind the organisation's identity provider (see the threat model).
/// </summary>
internal sealed class ApiKeyFilter(ApiOptions options) : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    private readonly byte[][] _keys = options.Keys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => Encoding.UTF8.GetBytes(k)).ToArray();

    public bool Enabled => _keys.Length > 0;

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!Enabled)
        {
            return next(context);
        }

        var presented = context.HttpContext.Request.Headers[HeaderName].ToString();
        var bytes = Encoding.UTF8.GetBytes(presented);
        var accepted = false;
        foreach (var key in _keys)
        {
            // Constant-time comparison; every key is compared so timing does not reveal which one is close.
            accepted |= CryptographicOperations.FixedTimeEquals(bytes, key);
        }

        return accepted
            ? next(context)
            : ValueTask.FromResult<object?>(Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Missing or invalid API key."));
    }
}
