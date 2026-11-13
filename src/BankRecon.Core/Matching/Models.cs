namespace BankRecon.Core.Matching;

/// <summary>
/// One movement on the bank side, flattened for matching. A camt.053 batch booking with itemised amounts becomes one
/// line per transaction detail, so each part can meet its own ledger item.
/// </summary>
public sealed record BankLine
{
    public required string Id { get; init; }

    /// <summary>Signed: credits positive, debits negative.</summary>
    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
    public required DateOnly ValueDate { get; init; }
    public string? CustomerReference { get; init; }
    public string? EndToEndId { get; init; }
    public string? Remittance { get; init; }
    public string? Narrative { get; init; }
    public string? CounterpartyName { get; init; }
}

/// <summary>An expected movement from the books: an open invoice, a payable, a payroll run.</summary>
public sealed record LedgerItem
{
    public required string Id { get; init; }

    /// <summary>Signed the same way as the bank: money expected in is positive.</summary>
    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
    public required DateOnly ExpectedDate { get; init; }
    public string? Reference { get; init; }
    public string? CounterpartyName { get; init; }
}

public enum MatchRule
{
    /// <summary>Same amount, date in window, and the ledger reference appears verbatim (after folding) on the bank side.</summary>
    ExactReference,

    /// <summary>Same amount and date in window, and the pair is the only candidate in both directions.</summary>
    AmountDate,

    /// <summary>Weighted score of reference similarity, counterparty similarity, amount and date closeness.</summary>
    Fuzzy,

    /// <summary>One bank line settles several ledger items (a customer pays three invoices at once).</summary>
    ManyToOne,

    /// <summary>Several bank lines settle one ledger item (instalments, a payment split by the payer's bank).</summary>
    OneToMany,

    /// <summary>Decided by a person in the work queue.</summary>
    Manual,
}

public sealed record Match(
    MatchRule Rule,
    IReadOnlyList<string> BankLineIds,
    IReadOnlyList<string> LedgerItemIds,
    double Score,
    decimal AmountDifference,
    string Explanation);

public enum UnmatchedReason
{
    /// <summary>Nothing on the other side is even plausible (amount, date and currency rule everything out).</summary>
    NoCandidate,

    /// <summary>Plausible candidates exist but none scored high enough.</summary>
    BelowThreshold,

    /// <summary>Two or more candidates scored too close to call; a person has to choose.</summary>
    Ambiguous,
}

public sealed record Candidate(string Id, double Score);

public sealed record Unmatched(string Id, UnmatchedReason Reason, IReadOnlyList<Candidate> Candidates);

public sealed record MatchResult(
    IReadOnlyList<Match> Matches,
    IReadOnlyList<Unmatched> UnmatchedBankLines,
    IReadOnlyList<Unmatched> UnmatchedLedgerItems);

/// <summary>Tuning for the matcher. The defaults are the ones measured in docs/benchmarks.md.</summary>
public sealed record MatchingOptions
{
    public int DateWindowDays { get; init; } = 5;

    /// <summary>Largest absolute amount difference accepted by the fuzzy rule (deducted transfer charges).</summary>
    public decimal AbsoluteTolerance { get; init; } = 25m;

    /// <summary>Largest relative difference accepted by the fuzzy rule; the smaller of the two limits applies.</summary>
    public decimal RelativeTolerance { get; init; } = 0.01m;

    public double FuzzyThreshold { get; init; } = 0.72;

    /// <summary>A runner-up within this distance of the best fuzzy score makes the pair ambiguous.</summary>
    public double AmbiguityMargin { get; init; } = 0.05;

    /// <summary>Counterparty similarity that relates items for one-to-many and many-to-one search.</summary>
    public double GroupNameThreshold { get; init; } = 0.8;

    public int MaxGroupSize { get; init; } = 5;
    public int MaxGroupCandidates { get; init; } = 20;

    public bool ExactReference { get; init; } = true;
    public bool AmountDate { get; init; } = true;
    public bool Fuzzy { get; init; } = true;
    public bool Groups { get; init; } = true;
}
