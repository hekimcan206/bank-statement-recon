namespace BankRecon.Core.Statements;

/// <summary>Direction of a movement or balance from the account holder's point of view.</summary>
public enum CreditDebit
{
    Credit,
    Debit,
}

/// <summary>A booked balance (opening, closing or closing available).</summary>
public sealed record Balance(CreditDebit Mark, DateOnly Date, string Currency, decimal Amount)
{
    public decimal SignedAmount => Mark == CreditDebit.Credit ? Amount : -Amount;

    public static Balance FromSigned(decimal signedAmount, DateOnly date, string currency) =>
        new(signedAmount < 0 ? CreditDebit.Debit : CreditDebit.Credit, date, currency, Math.Abs(signedAmount));
}

/// <summary>
/// One underlying transaction of a booked entry. A camt.053 batch booking carries several of these (for example one
/// collective transfer paying many invoices); MT940 never does.
/// </summary>
public sealed record TransactionDetail
{
    public decimal? Amount { get; init; }
    public string? EndToEndId { get; init; }
    public string? BankReference { get; init; }
    public string? Remittance { get; init; }
    public string? CounterpartyName { get; init; }
    public string? CounterpartyIban { get; init; }
}

/// <summary>
/// A booked movement on the statement, in a format-neutral shape. <see cref="Mark"/> is the effective direction: an
/// MT940 "RC" (reversal of a credit) is a debit with <see cref="IsReversal"/> set.
/// </summary>
public sealed record StatementEntry
{
    public required DateOnly ValueDate { get; init; }
    public DateOnly? BookingDate { get; init; }
    public required CreditDebit Mark { get; init; }
    public bool IsReversal { get; init; }

    /// <summary>Always positive; the direction is in <see cref="Mark"/>.</summary>
    public required decimal Amount { get; init; }

    public string? FundsCode { get; init; }

    /// <summary>MT940 transaction type (for example NTRF) or the camt.053 bank transaction code.</summary>
    public string TransactionType { get; init; } = "NTRF";

    public string? CustomerReference { get; init; }
    public string? BankReference { get; init; }
    public string? SupplementaryDetails { get; init; }

    /// <summary>Free text as the bank sent it: the MT940 :86: field or camt.053 AddtlNtryInf.</summary>
    public string? Narrative { get; init; }

    public string? Remittance { get; init; }
    public string? CounterpartyName { get; init; }
    public string? CounterpartyIban { get; init; }
    public string? EndToEndId { get; init; }
    public IReadOnlyList<TransactionDetail> Details { get; init; } = [];

    public decimal SignedAmount => Mark == CreditDebit.Credit ? Amount : -Amount;
}

/// <summary>Everything known about a statement before its first entry.</summary>
public sealed record StatementHeader
{
    /// <summary>"MT940" or the camt.053 message version, for example "camt.053.001.08".</summary>
    public required string Format { get; init; }

    public required string StatementId { get; init; }
    public string? SequenceNumber { get; init; }

    /// <summary>The account identification as sent (an IBAN, or bank code and account number).</summary>
    public required string Account { get; init; }

    public string? Iban { get; init; }
    public required string Currency { get; init; }
    public required Balance Opening { get; init; }

    /// <summary>camt.053 states the closing balance before the entries; MT940 only after them.</summary>
    public Balance? Closing { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>
/// The parsers stream a file as a sequence of parts, so a statement with a million entries never has to be held in
/// memory. <see cref="StatementStreamExtensions.ToStatements"/> buffers them when that is what the caller wants.
/// </summary>
public abstract record StatementPart;

public sealed record StatementStarted(StatementHeader Header) : StatementPart;

public sealed record EntryRead(StatementHeader Header, StatementEntry Entry, int Index) : StatementPart;

public sealed record StatementEnded(
    StatementHeader Header,
    Balance Closing,
    Balance? ClosingAvailable,
    int EntryCount,
    decimal ComputedClosing) : StatementPart
{
    /// <summary>Opening balance plus every movement equals the closing balance the bank stated.</summary>
    public bool IsBalanced => ComputedClosing == Closing.SignedAmount;
}

/// <summary>A fully buffered statement.</summary>
public sealed record Statement
{
    public required StatementHeader Header { get; init; }
    public required IReadOnlyList<StatementEntry> Entries { get; init; }
    public required Balance Closing { get; init; }
    public Balance? ClosingAvailable { get; init; }

    public decimal ComputedClosing => Header.Opening.SignedAmount + Entries.Sum(e => e.SignedAmount);
    public bool IsBalanced => ComputedClosing == Closing.SignedAmount;
}

/// <summary>The input is not a well-formed statement. Parsers throw nothing else for bad input.</summary>
public sealed class StatementFormatException : FormatException
{
    public StatementFormatException()
    {
    }

    public StatementFormatException(string message)
        : base(message)
    {
    }

    public StatementFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public StatementFormatException(string message, long line)
        : base($"{message} (line {line})") => Line = line;

    /// <summary>1-based line of the offending field, when known.</summary>
    public long? Line { get; }
}
