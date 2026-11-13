using System.Text;
using BankRecon.Core.Matching;
using BankRecon.Core.Statements;
using BankRecon.Core.Statements.Camt;
using BankRecon.Core.Statements.Mt940;
using BankRecon.Synthetic;

namespace BankRecon.UnitTests;

public sealed class SyntheticTests
{
    [Fact]
    public void Same_seed_same_scenario()
    {
        var a = ScenarioGenerator.Generate(new ScenarioOptions { Seed = 42, LedgerItems = 300 });
        var b = ScenarioGenerator.Generate(new ScenarioOptions { Seed = 42, LedgerItems = 300 });
        Mt940Writer.WriteToString([a.Statement]).Should().Be(Mt940Writer.WriteToString([b.Statement]));
        b.Truth.Should().BeEquivalentTo(a.Truth, o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData(NarrativeStyle.TurkishFreeText)]
    [InlineData(NarrativeStyle.QuestionMarkSubfields)]
    [InlineData(NarrativeStyle.SlashKeywords)]
    [InlineData(NarrativeStyle.Camt)]
    public void Generated_statements_balance_use_valid_ibans_and_survive_their_file_format(NarrativeStyle style)
    {
        var scenario = ScenarioGenerator.Generate(new ScenarioOptions { Seed = 3, LedgerItems = 400, Style = style });
        scenario.Statement.IsBalanced.Should().BeTrue();
        Iban.IsValid(scenario.Statement.Header.Account).Should().BeTrue();

        byte[] file;
        if (style == NarrativeStyle.Camt)
        {
            using var stream = new MemoryStream();
            Camt053Writer.Write(stream, [scenario.Statement]);
            file = stream.ToArray();
        }
        else
        {
            file = Encoding.UTF8.GetBytes(Mt940Writer.WriteToString([scenario.Statement]));
        }

        var parsed = TestFiles.Parse(file).Single();
        parsed.Entries.Should().HaveCount(scenario.Statement.Entries.Count);
        parsed.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public void Every_ledger_item_and_bank_line_appears_in_exactly_one_truth_group()
    {
        var scenario = ScenarioGenerator.Generate(new ScenarioOptions { Seed = 9, LedgerItems = 500, Style = NarrativeStyle.Camt });
        var lines = scenario.BankLines();
        scenario.Truth.SelectMany(t => t.LedgerItemIds).Should().BeEquivalentTo(scenario.Ledger.Select(l => l.Id));
        scenario.Truth.SelectMany(t => t.BankLineIds).Should().BeEquivalentTo(lines.Select(l => l.Id));
    }

    /// <summary>
    /// Regression floor on a seed the tuning never looked at (tuning used seeds 1-3). The measured numbers live in
    /// docs/benchmarks.md; this only catches a change that makes the matcher clearly worse.
    /// </summary>
    [Theory]
    [InlineData(NarrativeStyle.TurkishFreeText)]
    [InlineData(NarrativeStyle.Camt)]
    public void Full_matcher_stays_above_the_accuracy_floor_on_an_unseen_seed(NarrativeStyle style)
    {
        var scenario = ScenarioGenerator.Generate(new ScenarioOptions { Seed = 777, LedgerItems = 800, Style = style });
        var lines = scenario.BankLines();
        var evaluation = MatchEvaluation.Evaluate(scenario, lines, new MatchingEngine().Match(lines, scenario.Ledger));

        evaluation.Precision.Should().BeGreaterThan(0.98);
        evaluation.Recall.Should().BeGreaterThan(0.90);
    }
}
