using System.Diagnostics;
using System.Text;
using BankRecon.Core.Statements;

namespace BankRecon.UnitTests;

/// <summary>
/// Mutation fuzzing of the parsers with the sample files as seeds. Whatever the bytes, a parser either returns
/// statements or throws <see cref="StatementFormatException"/>: no NullReferenceException, no
/// IndexOutOfRangeException, no hang. Seeded, so a failure replays.
/// </summary>
public sealed class FuzzTests
{
    private const int IterationsPerSample = 3000;

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
    public void Mutated_files_only_ever_fail_with_a_format_exception(string sample)
    {
        var original = File.ReadAllBytes(TestFiles.Sample(sample));
        var random = new Random(sample.GetHashCode(StringComparison.Ordinal) & 0x7FFFFFFF);
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        var slowest = TimeSpan.Zero;

        for (var i = 0; i < IterationsPerSample; i++)
        {
            var mutated = Mutate(original, random);
            var stopwatch = Stopwatch.StartNew();
            string outcome;
            try
            {
                var statements = TestFiles.Parse(mutated);
                outcome = "parsed";
                _ = statements.Sum(s => s.Entries.Count);
            }
            catch (StatementFormatException)
            {
                outcome = "rejected";
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Iteration {i}: {ex.GetType().Name} escaped the parser for input:\n{Encoding.UTF8.GetString(mutated)}", ex);
            }

            stopwatch.Stop();
            slowest = stopwatch.Elapsed > slowest ? stopwatch.Elapsed : slowest;
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
        }

        outcomes.GetValueOrDefault("rejected").Should().BeGreaterThan(0);
        outcomes.GetValueOrDefault("parsed").Should().BeGreaterThan(0, "some mutations (inside free text) leave the file valid");
        slowest.Should().BeLessThan(TimeSpan.FromSeconds(2), "no input may make the parser crawl");
    }

    [Fact]
    public void Random_bytes_are_rejected()
    {
        var random = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var bytes = new byte[random.Next(1, 600)];
            random.NextBytes(bytes);
            var act = () => TestFiles.Parse(bytes);
            act.Should().Throw<StatementFormatException>();
        }
    }

    private static byte[] Mutate(byte[] input, Random random)
    {
        var data = new List<byte>(input);
        var mutations = random.Next(1, 5);
        for (var m = 0; m < mutations && data.Count > 0; m++)
        {
            var at = random.Next(data.Count);
            switch (random.Next(7))
            {
                case 0:
                    data[at] = (byte)random.Next(256);
                    break;
                case 1:
                    data[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 2:
                    data.RemoveRange(at, Math.Min(data.Count - at, random.Next(1, 40)));
                    break;
                case 3:
                    var length = Math.Min(data.Count - at, random.Next(1, 80));
                    data.InsertRange(random.Next(data.Count), data.GetRange(at, length));
                    break;
                case 4:
                    data.Insert(at, Interesting[random.Next(Interesting.Length)]);
                    break;
                case 5:
                    data.RemoveRange(at, data.Count - at);
                    break;
                default:
                    var token = Encoding.UTF8.GetBytes(Tokens[random.Next(Tokens.Length)]);
                    data.InsertRange(at, token);
                    break;
            }
        }

        return [.. data];
    }

    private static readonly byte[] Interesting = [(byte)':', (byte)'\n', (byte)'\r', (byte)'-', (byte)'/', (byte)'?', (byte)'<', (byte)'>', (byte)',', 0, 0xFF, 0xC3];

    private static readonly string[] Tokens =
    [
        ":20:", ":61:", ":86:", ":62F:", ":60F:", "-}", "{4:", "//", "?20", "/NAME/", "</Ntry>", "<Ntry>", "<Amt Ccy=\"TRY\">",
        "&amp;", "<!DOCTYPE x>", "99999999999999999999", "RC", "İ",
    ];
}
