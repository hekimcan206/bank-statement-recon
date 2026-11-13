using System.Text.Json;

namespace BankRecon.UnitTests;

/// <summary>
/// Approval tests: each sample file is parsed and the result compared with a reviewed JSON file. A parser change
/// that alters any field shows up as a diff. Regenerate with UPDATE_GOLDEN=1 and review the diff like code.
/// </summary>
public sealed class GoldenParseTests
{
    public static TheoryData<string> Samples =>
    [
        "mt940-turkish-free-text.sta",
        "mt940-gvc-subfields.sta",
        "mt940-sepa-keywords-fin.sta",
        "camt053-v08-batch.xml",
        "camt053-v02.xml",
    ];

    [Theory]
    [MemberData(nameof(Samples))]
    public void Parsed_sample_matches_the_approved_output(string sample)
    {
        var statements = TestFiles.Parse(sample);
        var actual = JsonSerializer.Serialize(statements, TestFiles.Json).ReplaceLineEndings("\n") + "\n";
        var approvedName = sample + ".approved.json";

        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(Path.Combine(TestFiles.GoldenSourceDirectory(), approvedName), actual);
            return;
        }

        var approvedPath = Path.Combine(AppContext.BaseDirectory, "Golden", approvedName);
        File.Exists(approvedPath).Should().BeTrue($"an approved file for {sample} must exist (run with UPDATE_GOLDEN=1)");
        actual.Should().Be(File.ReadAllText(approvedPath).ReplaceLineEndings("\n"));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_sample_statement_balances(string sample) =>
        TestFiles.Parse(sample).Should().OnlyContain(s => s.IsBalanced);
}
