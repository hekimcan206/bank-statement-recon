using System.Globalization;

namespace BankRecon.Core.Statements.Mt940;

public sealed record Mt940WriteOptions
{
    /// <summary>Transliterate to the SWIFT X character set. Off only to produce the raw-Turkish variant some banks send.</summary>
    public bool XCharsetOnly { get; init; } = true;

    /// <summary>Write the entry date (MMDD) after the value date.</summary>
    public bool WriteEntryDate { get; init; } = true;

    /// <summary>Wrap each message in FIN blocks 1, 2 and 4 instead of separating plain messages with "-".</summary>
    public bool FinBlocks { get; init; }
}

/// <summary>
/// Writes statements as MT940. Used by the synthetic generator and by the round-trip property tests; it rejects
/// values the format cannot carry instead of silently changing them.
/// </summary>
public static class Mt940Writer
{
    private const int NarrativeLineLength = 65;
    private const int MaxNarrativeLines = 6;

    public static void Write(TextWriter writer, IEnumerable<Statement> statements, Mt940WriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(statements);
        options ??= new Mt940WriteOptions();
        foreach (var statement in statements)
        {
            WriteOne(writer, statement, options);
        }
    }

    public static string WriteToString(IEnumerable<Statement> statements, Mt940WriteOptions? options = null)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\r\n" };
        Write(writer, statements, options);
        return writer.ToString();
    }

    private static void WriteOne(TextWriter w, Statement statement, Mt940WriteOptions options)
    {
        var header = statement.Header;
        if (options.FinBlocks)
        {
            w.WriteLine("{1:F01SYNTTRISAXXX0000000000}{2:O9400000000000SYNTTRISAXXX00000000000000000000N}{4:");
        }

        w.WriteLine($":20:{Text(header.StatementId, 16, options)}");
        w.WriteLine($":25:{Text(header.Account, 35, options)}");
        if (header.SequenceNumber is not null)
        {
            w.WriteLine($":28C:{Text(header.SequenceNumber, 16, options)}");
        }

        w.WriteLine($":60F:{FormatBalance(header.Opening)}");
        foreach (var entry in statement.Entries)
        {
            WriteEntry(w, entry, options);
        }

        w.WriteLine($":62F:{FormatBalance(statement.Closing)}");
        if (statement.ClosingAvailable is not null)
        {
            w.WriteLine($":64:{FormatBalance(statement.ClosingAvailable)}");
        }

        w.WriteLine(options.FinBlocks ? "-}" : "-");
    }

    private static void WriteEntry(TextWriter w, StatementEntry entry, Mt940WriteOptions options)
    {
        var mark = (entry.IsReversal, entry.Mark) switch
        {
            (false, CreditDebit.Credit) => "C",
            (false, CreditDebit.Debit) => "D",
            (true, CreditDebit.Debit) => "RC",
            (true, CreditDebit.Credit) => "RD",
            _ => throw new ArgumentOutOfRangeException(nameof(entry), "Unknown debit/credit mark."),
        };

        var entryDate = options.WriteEntryDate && entry.BookingDate is { } booked
            ? booked.ToString("MMdd", CultureInfo.InvariantCulture)
            : string.Empty;
        var customer = entry.CustomerReference is null ? "NONREF" : Text(entry.CustomerReference, 16, options);
        var bank = entry.BankReference is null ? string.Empty : "//" + Text(entry.BankReference, 16, options);
        if (entry.TransactionType.Length != 4)
        {
            throw new ArgumentException("MT940 transaction type must be 4 characters, for example NTRF.", nameof(entry));
        }

        w.WriteLine(
            $":61:{SwiftText.FormatDate(entry.ValueDate)}{entryDate}{mark}{entry.FundsCode}{SwiftText.FormatAmount(entry.Amount)}" +
            $"{entry.TransactionType}{customer}{bank}");
        if (entry.SupplementaryDetails is not null)
        {
            w.WriteLine(Text(entry.SupplementaryDetails, 34, options));
        }

        var narrative = entry.Narrative ?? Compose(entry);
        if (narrative is null)
        {
            return;
        }

        narrative = options.XCharsetOnly ? SwiftText.ToXCharset(narrative) : narrative;
        var lineCount = (narrative.Length + NarrativeLineLength - 1) / NarrativeLineLength;
        if (lineCount > MaxNarrativeLines)
        {
            throw new ArgumentException($"Narrative longer than {MaxNarrativeLines} x {NarrativeLineLength} characters.", nameof(entry));
        }

        for (var line = 0; line < lineCount; line++)
        {
            var chunk = narrative.Substring(
                line * NarrativeLineLength,
                Math.Min(NarrativeLineLength, narrative.Length - (line * NarrativeLineLength)));
            if (line > 0 && (chunk.StartsWith(':') || chunk.StartsWith('-') || chunk.Trim().Length == 0))
            {
                // A continuation line starting with ':' or '-' would be read as a new field or a message end.
                throw new ArgumentException("Narrative would produce a continuation line the format cannot carry.", nameof(entry));
            }

            w.WriteLine(line == 0 ? ":86:" + chunk : chunk);
        }
    }

    /// <summary>When an entry has structured fields but no raw narrative, write them in the SEPA keyword style.</summary>
    private static string? Compose(StatementEntry entry)
    {
        var parts = new List<string>();
        if (entry.EndToEndId is not null)
        {
            parts.Add("/EREF/" + entry.EndToEndId);
        }

        if (entry.CounterpartyIban is not null)
        {
            parts.Add("/IBAN/" + entry.CounterpartyIban);
        }

        if (entry.CounterpartyName is not null)
        {
            parts.Add("/NAME/" + entry.CounterpartyName);
        }

        if (entry.Remittance is not null)
        {
            parts.Add("/REMI/" + entry.Remittance);
        }

        return parts.Count == 0 ? null : string.Concat(parts);
    }

    private static string FormatBalance(Balance balance) =>
        $"{(balance.Mark == CreditDebit.Credit ? 'C' : 'D')}{SwiftText.FormatDate(balance.Date)}{balance.Currency}{SwiftText.FormatAmount(balance.Amount)}";

    private static string Text(string value, int max, Mt940WriteOptions options)
    {
        var text = options.XCharsetOnly ? SwiftText.ToXCharset(value) : value;
        return text.Length > max || text.Length == 0
            ? throw new ArgumentException($"'{value}' does not fit a {max}-character MT940 field.", nameof(value))
            : text;
    }
}
