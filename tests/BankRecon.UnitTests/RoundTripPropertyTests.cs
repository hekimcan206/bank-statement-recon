using System.Text;
using BankRecon.Core.Statements;
using BankRecon.Core.Statements.Camt;
using BankRecon.Core.Statements.Mt940;
using FsCheck.Xunit;

namespace BankRecon.UnitTests;

/// <summary>
/// parse(format(s)) == s for every statement the format can carry. FsCheck supplies the seeds and reports the
/// failing one, which replays the same statement deterministically.
/// </summary>
public sealed class RoundTripPropertyTests
{
    [Property(MaxTest = 300)]
    public void Mt940_round_trips(int seed)
    {
        var statement = new RandomStatements(seed).Mt940();
        var text = Mt940Writer.WriteToString([statement]);

        var parsed = Mt940Reader.Read(new StringReader(text)).ToStatements().Single();

        parsed.Should().BeEquivalentTo(statement, o => o.WithStrictOrdering(), text);
    }

    [Property(MaxTest = 100)]
    public void Mt940_round_trips_with_fin_blocks_and_several_messages(int seed)
    {
        var random = new RandomStatements(seed);
        var statements = new[] { random.Mt940(), random.Mt940(), random.Mt940() };
        var text = Mt940Writer.WriteToString(statements, new Mt940WriteOptions { FinBlocks = seed % 2 == 0 });

        var parsed = Mt940Reader.Read(new StringReader(text)).ToStatements().ToList();

        parsed.Should().BeEquivalentTo(statements, o => o.WithStrictOrdering());
    }

    [Property(MaxTest = 200)]
    public void Camt053_v08_round_trips(int seed) => CamtRoundTrip(seed, 8);

    [Property(MaxTest = 200)]
    public void Camt053_v02_round_trips(int seed) => CamtRoundTrip(seed, 2);

    [Property(MaxTest = 200)]
    public void Formatting_a_parsed_file_again_is_stable(int seed)
    {
        // format(parse(format(s))) == format(s): the writer is a fixed point after one pass.
        var text = Mt940Writer.WriteToString([new RandomStatements(seed).Mt940()]);
        var again = Mt940Writer.WriteToString(Mt940Reader.Read(new StringReader(text)).ToStatements().ToList());
        again.Should().Be(text);
    }

    [Property(MaxTest = 200)]
    public void Streaming_running_balance_equals_buffered_sum(int seed)
    {
        var statement = new RandomStatements(seed).Mt940();
        var text = Mt940Writer.WriteToString([statement]);
        var ended = Mt940Reader.Read(new StringReader(text)).OfType<StatementEnded>().Single();
        ended.ComputedClosing.Should().Be(statement.ComputedClosing);
        ended.EntryCount.Should().Be(statement.Entries.Count);
    }

    private static void CamtRoundTrip(int seed, int version)
    {
        var statement = new RandomStatements(seed).Camt(version);
        using var stream = new MemoryStream();
        Camt053Writer.Write(stream, [statement], version);
        stream.Position = 0;

        var parsed = Camt053Reader.Read(stream).ToStatements().Single();

        parsed.Should().BeEquivalentTo(statement, o => o.WithStrictOrdering(), Encoding.UTF8.GetString(stream.ToArray()));
    }
}
