using BankRecon.Core.Statements;

namespace BankRecon.Core.Matching;

/// <summary>Turns statement entries into matcher input.</summary>
public static class BankLineFactory
{
    /// <summary>
    /// A batch booking whose transaction details carry amounts that add up to the entry becomes one line per detail
    /// (id "entry#k"), because each detail usually pays a different ledger item. Anything else is one line.
    /// </summary>
    public static IEnumerable<BankLine> From(string entryId, StatementEntry entry, string currency)
    {
        ArgumentNullException.ThrowIfNull(entryId);
        ArgumentNullException.ThrowIfNull(entry);
        var sign = entry.Mark == CreditDebit.Credit ? 1m : -1m;
        if (entry.Details.Count > 1 && entry.Details.All(d => d.Amount is > 0) &&
            entry.Details.Sum(d => d.Amount!.Value) == entry.Amount)
        {
            return entry.Details.Select((d, k) => new BankLine
            {
                Id = $"{entryId}#{k}",
                Amount = sign * d.Amount!.Value,
                Currency = currency,
                ValueDate = entry.ValueDate,
                CustomerReference = entry.CustomerReference,
                EndToEndId = d.EndToEndId,
                Remittance = d.Remittance,
                Narrative = entry.Narrative,
                CounterpartyName = d.CounterpartyName ?? entry.CounterpartyName,
            }).ToList();
        }

        return
        [
            new BankLine
            {
                Id = entryId,
                Amount = sign * entry.Amount,
                Currency = currency,
                ValueDate = entry.ValueDate,
                CustomerReference = entry.CustomerReference,
                EndToEndId = entry.EndToEndId,
                Remittance = JoinDetails(entry),
                Narrative = entry.Narrative,
                CounterpartyName = entry.CounterpartyName,
            },
        ];
    }

    private static string? JoinDetails(StatementEntry entry)
    {
        if (entry.Details.Count == 0)
        {
            return entry.Remittance;
        }

        // Unitemised batch: keep every detail's remittance so references can still be found.
        var parts = entry.Details.Select(d => d.Remittance).Where(r => r is not null).Prepend(entry.Remittance).Where(r => r is not null);
        var joined = string.Join(' ', parts);
        return joined.Length == 0 ? null : joined;
    }
}
