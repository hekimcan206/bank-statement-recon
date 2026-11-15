using BankRecon.Core.Matching;
using BankRecon.Synthetic;
using FsCheck.Xunit;

namespace BankRecon.UnitTests;

public sealed class MatchingEngineTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);

    private static BankLine Bank(string id, decimal amount, int day = 0, string? text = null, string? name = null, string? customerRef = null) => new()
    {
        Id = id,
        Amount = amount,
        Currency = "TRY",
        ValueDate = Day.AddDays(day),
        Remittance = text,
        CounterpartyName = name,
        CustomerReference = customerRef,
    };

    private static LedgerItem Ledger(string id, decimal amount, int day = 0, string? reference = null, string? name = null) => new()
    {
        Id = id,
        Amount = amount,
        Currency = "TRY",
        ExpectedDate = Day.AddDays(day),
        Reference = reference,
        CounterpartyName = name,
    };

    private static MatchResult Run(IReadOnlyList<BankLine> banks, IReadOnlyList<LedgerItem> ledger, MatchingOptions? options = null) =>
        new MatchingEngine(options).Match(banks, ledger);

    [Fact]
    public void Exact_reference_matches_even_when_amounts_collide()
    {
        var result = Run(
            [Bank("b1", 1000m, 1, "FATURA INV-2026-000123 ODEMESI")],
            [Ledger("l1", 1000m, 0, "INV-2026-000999"), Ledger("l2", 1000m, 0, "INV-2026-000123")]);

        result.Matches.Should().ContainSingle().Which.Should().Match<Match>(m =>
            m.Rule == MatchRule.ExactReference && m.LedgerItemIds.Single() == "l2");
    }

    [Fact]
    public void Folding_lets_a_reformatted_reference_match_exactly() =>
        Run([Bank("b1", 50m, 0, "inv 2026 000123")], [Ledger("l1", 50m, 0, "INV-2026/000123")])
            .Matches.Single().Rule.Should().Be(MatchRule.ExactReference);

    [Fact]
    public void Unique_amount_in_both_directions_matches_without_a_reference() =>
        Run([Bank("b1", 1234.56m, 2, "ODEME")], [Ledger("l1", 1234.56m, 0, "INV-1"), Ledger("l2", 99m, 0, "INV-2")])
            .Matches.Single().Rule.Should().Be(MatchRule.AmountDate);

    [Fact]
    public void Two_open_items_with_the_same_amount_are_not_guessed()
    {
        var result = Run([Bank("b1", 1000m, 0, "ODEME")], [Ledger("l1", 1000m), Ledger("l2", 1000m, 1)]);

        result.Matches.Should().BeEmpty();
        result.UnmatchedBankLines.Single().Reason.Should().NotBe(UnmatchedReason.NoCandidate);
    }

    [Fact]
    public void Same_amount_is_resolved_by_counterparty_when_only_one_item_belongs_to_the_payer()
    {
        var result = Run(
            [Bank("b1", 1000m, 1, "ODEME", "ŞİMŞEK GIDA LTD ŞTİ")],
            [Ledger("l1", 1000m, 0, "INV-1", "Ege Lojistik A.Ş."), Ledger("l2", 1000m, 0, "INV-2", "Şimşek Gıda Ltd. Şti.")]);

        result.Matches.Single().LedgerItemIds.Should().Equal("l2");
    }

    [Fact]
    public void A_mistyped_reference_with_deducted_charges_matches_fuzzily_and_reports_the_difference()
    {
        var match = Run(
            [Bank("b1", 4987.50m, 2, "FATURA INV-2026-001234", "ANADOLU TEKSTIL AS")],
            [Ledger("l1", 5000m, 0, "INV-2026-001243", "Anadolu Tekstil A.Ş."), Ledger("l2", 5000m, 0, "INV-2026-777777", "Ege Kimya A.Ş.")])
            .Matches.Single();

        match.Rule.Should().Be(MatchRule.Fuzzy);
        match.LedgerItemIds.Should().Equal("l1");
        match.AmountDifference.Should().Be(-12.50m);
    }

    [Fact]
    public void A_reference_cut_at_sixteen_characters_still_identifies_the_invoice()
    {
        var match = Run(
            [Bank("b1", 7320.40m, 1, text: null, customerRef: "SO/2026/TR/00123")],
            [Ledger("l1", 7320.40m, 0, "SO/2026/TR/0012345/01"), Ledger("l2", 7320.40m, 0, "SO/2026/TR/0099999/01")])
            .Matches.Single();

        match.LedgerItemIds.Should().Equal("l1");
    }

    [Fact]
    public void A_short_prefix_in_free_text_is_not_mistaken_for_a_truncated_reference()
    {
        // "INV-2026-000" appears because a GVC subfield broke the line; it must not match INV-2026-000920.
        var result = Run([Bank("b1", 29387.98m, 0, "FATURALAR INV-2026-000")], [Ledger("l1", 29373.55m, 0, "INV-2026-000920")]);
        result.Matches.Should().BeEmpty();
    }

    [Fact]
    public void A_clearly_different_counterparty_vetoes_a_merely_similar_reference()
    {
        var result = Run(
            [Bank("b1", 2000m, 1, "INV-2026-000323 Marmara", "MARMARA MOBILYA TIC LTD STI")],
            [Ledger("l1", 2000m, 0, "INV-2026-000383", "Ege Otomotiv Tic. Ltd. Şti."), Ledger("l2", 2000m, 0, "X", "Ege Otomotiv")]);
        result.Matches.Should().BeEmpty();
    }

    [Fact]
    public void Close_runner_up_makes_a_fuzzy_pair_ambiguous_and_both_sides_go_to_the_queue()
    {
        var result = Run(
            [Bank("b1", 990m, 0, "INV-2026-000100")],
            [Ledger("l1", 1000m, 0, "INV-2026-000101"), Ledger("l2", 1000m, 0, "INV-2026-000102")]);

        result.Matches.Should().BeEmpty();
        result.UnmatchedBankLines.Single().Reason.Should().Be(UnmatchedReason.Ambiguous);
        result.UnmatchedBankLines.Single().Candidates.Select(c => c.Id).Should().BeEquivalentTo(["l1", "l2"]);
    }

    [Fact]
    public void One_bank_line_settles_several_invoices_of_the_same_payer()
    {
        var match = Run(
            [Bank("b1", 3500m, 3, "FATURALAR INV-2026-000001 INV-2026-000002", "CINAR LOJISTIK AS")],
            [
                Ledger("l1", 1200m, 0, "INV-2026-000001", "Çınar Lojistik A.Ş."),
                Ledger("l2", 2300m, -6, "INV-2026-000002", "Çınar Lojistik A.Ş."),
                Ledger("l3", 2300m, 1, "INV-2026-000003", "Ege Kimya A.Ş."),
            ]).Matches.Single();

        match.Rule.Should().Be(MatchRule.ManyToOne);
        match.LedgerItemIds.Should().BeEquivalentTo(["l1", "l2"]);
        match.AmountDifference.Should().Be(0m);
    }

    [Fact]
    public void Instalments_are_matched_one_to_many()
    {
        var match = Run(
            [
                Bank("b1", 3000m, 0, "INV-2026-000050 TAKSIT 1"),
                Bank("b2", 3000m, 4, "INV-2026-000050 TAKSIT 2"),
                Bank("b3", 1500m, 6, "AVANS", "EGE KIMYA AS"),
            ],
            [Ledger("l1", 6000m, 0, "INV-2026-000050", "Doğan Mobilya A.Ş.")]).Matches.Single();

        match.Rule.Should().Be(MatchRule.OneToMany);
        match.BankLineIds.Should().BeEquivalentTo(["b1", "b2"]);
    }

    [Fact]
    public void Group_search_refuses_to_pick_between_two_equally_good_subsets()
    {
        var engine = new MatchingEngine();
        var chosen = engine.BestSubset([(100m, false), (200m, false), (300m, false)], 300m, 0m, out var ambiguous);
        chosen.Should().BeEquivalentTo([0, 1], "300 alone is not a group, so 100 + 200 is the only answer");
        ambiguous.Should().BeFalse();

        chosen = engine.BestSubset([(100m, false), (200m, false), (150m, false), (150m, false)], 300m, 0m, out ambiguous);
        chosen.Should().BeNull();
        ambiguous.Should().BeTrue();
    }

    [Fact]
    public void Reference_evidence_breaks_a_tie_between_subsets()
    {
        var chosen = new MatchingEngine().BestSubset([(100m, true), (200m, true), (150m, false), (150m, false)], 300m, 0m, out var ambiguous);
        ambiguous.Should().BeFalse();
        chosen.Should().BeEquivalentTo([0, 1]);
    }

    [Fact]
    public void Currencies_and_directions_never_mix()
    {
        var result = Run(
            [Bank("b1", -1000m, 0, "INV-2026-000123"), Bank("b2", 1000m, 0, "INV-2026-000124") with { Currency = "EUR" }],
            [Ledger("l1", 1000m, 0, "INV-2026-000123"), Ledger("l2", 1000m, 0, "INV-2026-000124")]);
        result.Matches.Should().BeEmpty();
        result.UnmatchedBankLines.Should().OnlyContain(u => u.Reason == UnmatchedReason.NoCandidate);
    }

    [Property(MaxTest = 40)]
    public void Every_item_is_used_at_most_once_and_every_match_respects_its_tolerance(int seed)
    {
        var scenario = ScenarioGenerator.Generate(new ScenarioOptions { Seed = Math.Abs(seed % 10_000) + 1, LedgerItems = 150, Style = (NarrativeStyle)(Math.Abs(seed) % 4) });
        var lines = scenario.BankLines();
        var options = new MatchingOptions();
        var result = new MatchingEngine(options).Match(lines, scenario.Ledger);

        var usedBanks = result.Matches.SelectMany(m => m.BankLineIds).ToList();
        var usedLedger = result.Matches.SelectMany(m => m.LedgerItemIds).ToList();
        usedBanks.Should().OnlyHaveUniqueItems();
        usedLedger.Should().OnlyHaveUniqueItems();
        (usedBanks.Count + result.UnmatchedBankLines.Count).Should().Be(lines.Count);
        (usedLedger.Count + result.UnmatchedLedgerItems.Count).Should().Be(scenario.Ledger.Count);

        var bankAmounts = lines.ToDictionary(l => l.Id, l => l.Amount);
        var ledgerAmounts = scenario.Ledger.ToDictionary(l => l.Id, l => l.Amount);
        foreach (var match in result.Matches)
        {
            var difference = match.BankLineIds.Sum(id => bankAmounts[id]) - match.LedgerItemIds.Sum(id => ledgerAmounts[id]);
            difference.Should().Be(match.AmountDifference);
            Math.Abs(difference).Should().BeLessThanOrEqualTo(options.AbsoluteTolerance);
        }
    }

    [Property(MaxTest = 30)]
    public void Matching_the_leftovers_again_finds_nothing_new(int seed)
    {
        // A reconciliation run re-matches every open item; it must be idempotent.
        var scenario = ScenarioGenerator.Generate(new ScenarioOptions { Seed = Math.Abs(seed % 10_000) + 1, LedgerItems = 200, Style = (NarrativeStyle)(Math.Abs(seed) % 4) });
        var lines = scenario.BankLines();
        var first = new MatchingEngine().Match(lines, scenario.Ledger);
        var openBanks = first.UnmatchedBankLines.Select(u => u.Id).ToHashSet();
        var openLedger = first.UnmatchedLedgerItems.Select(u => u.Id).ToHashSet();

        var second = new MatchingEngine().Match(lines.Where(l => openBanks.Contains(l.Id)).ToList(), scenario.Ledger.Where(l => openLedger.Contains(l.Id)).ToList());

        second.Matches.Should().BeEmpty();
    }

    [Property(MaxTest = 20)]
    public void The_result_does_not_depend_on_input_order(int seed)
    {
        var scenario = ScenarioGenerator.Generate(new ScenarioOptions { Seed = Math.Abs(seed % 10_000) + 1, LedgerItems = 150 });
        var lines = scenario.BankLines();
        var random = new Random(seed);
        var shuffledLines = lines.OrderBy(_ => random.Next()).ToList();
        var shuffledLedger = scenario.Ledger.OrderBy(_ => random.Next()).ToList();

        var a = new MatchingEngine().Match(lines, scenario.Ledger);
        var b = new MatchingEngine().Match(shuffledLines, shuffledLedger);

        b.Matches.Should().BeEquivalentTo(a.Matches);
        b.UnmatchedBankLines.Should().BeEquivalentTo(a.UnmatchedBankLines);
    }
}
