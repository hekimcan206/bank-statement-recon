using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace BankRecon.Api.Features;

public sealed record LedgerItemInput(
    string ExternalId,
    string Account,
    string Currency,
    decimal Amount,
    DateOnly ExpectedDate,
    string? Reference,
    string? CounterpartyName);

public sealed record LedgerImportResult(int Received, int Inserted, int AlreadyKnown);

/// <summary>Open items from the books. Idempotent per (account, external id): re-sending a batch inserts nothing twice.</summary>
internal sealed class LedgerItemStore(NpgsqlDataSource dataSource)
{
    public const int MaxBatch = 10_000;

    public static IReadOnlyList<string> Validate(IReadOnlyList<LedgerItemInput> items)
    {
        var errors = new List<string>();
        if (items.Count is 0 or > MaxBatch)
        {
            errors.Add($"Send between 1 and {MaxBatch} items per request.");
            return errors;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (string.IsNullOrWhiteSpace(item.ExternalId) || item.ExternalId.Length > 100)
            {
                errors.Add(Invariant($"[{i}] externalId must be 1-100 characters."));
            }

            if (string.IsNullOrWhiteSpace(item.Account) || item.Account.Length > 64)
            {
                errors.Add(Invariant($"[{i}] account must be 1-64 characters."));
            }

            if (item.Currency is not { Length: 3 } || !item.Currency.All(char.IsAsciiLetterUpper))
            {
                errors.Add(Invariant($"[{i}] currency must be an ISO 4217 code such as TRY."));
            }

            if (item.Amount == 0 || Math.Abs(item.Amount) >= 1_000_000_000_000_000m || decimal.Round(item.Amount, 3) != item.Amount)
            {
                errors.Add(Invariant($"[{i}] amount must be non-zero with at most 3 decimals."));
            }

            if (item.Reference?.Length > 140 || item.CounterpartyName?.Length > 140)
            {
                errors.Add(Invariant($"[{i}] reference and counterpartyName are limited to 140 characters."));
            }
        }

        if (items.Select(i => (i.Account, i.ExternalId)).Distinct().Count() != items.Count)
        {
            errors.Add("The batch contains the same (account, externalId) twice.");
        }

        return errors;
    }

    public async Task<LedgerImportResult> InsertAsync(IReadOnlyList<LedgerItemInput> items, CancellationToken cancellationToken)
    {
        // One statement for the whole batch: arrays in, unnest, ON CONFLICT keeps the call idempotent.
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO recon.ledger_item (external_id, account, currency, amount, expected_date, reference, counterparty_name)
            SELECT * FROM unnest($1::text[], $2::text[], $3::char(3)[], $4::numeric[], $5::date[], $6::text[], $7::text[])
            ON CONFLICT (account, external_id) DO NOTHING
            """);
        cmd.Parameters.Add(new() { Value = items.Select(i => i.ExternalId).ToArray() });
        cmd.Parameters.Add(new() { Value = items.Select(i => Iban(i.Account)).ToArray() });
        cmd.Parameters.Add(new() { Value = items.Select(i => i.Currency).ToArray() });
        cmd.Parameters.Add(new() { Value = items.Select(i => i.Amount).ToArray() });
        cmd.Parameters.Add(new() { Value = items.Select(i => i.ExpectedDate).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Date });
        cmd.Parameters.Add(new() { Value = items.Select(i => (object?)i.Reference ?? DBNull.Value).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = items.Select(i => (object?)i.CounterpartyName ?? DBNull.Value).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        var inserted = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return new LedgerImportResult(items.Count, inserted, items.Count - inserted);
    }

    /// <summary>Accounts are filed under the normalised IBAN when the value is one, like statements.</summary>
    internal static string Iban(string account) => Core.Statements.Iban.Normalize(account) ?? account.Trim();

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
