using System.Text;
using BankRecon.Core.Statements;
using BankRecon.Core.Statements.Camt;
using BankRecon.Core.Statements.Mt940;
using BankRecon.Synthetic;

namespace BankRecon.Lab;

/// <summary>Writes a scenario to the file format its style implies and reads it back, as a real import would.</summary>
internal static class Formats
{
    public static string Label(NarrativeStyle style) => style switch
    {
        NarrativeStyle.TurkishFreeText => "MT940, Turkish free-text :86:",
        NarrativeStyle.QuestionMarkSubfields => "MT940, GVC ?NN subfields",
        NarrativeStyle.SlashKeywords => "MT940, SEPA /KEY/ :86:",
        NarrativeStyle.Camt => "camt.053.001.08",
        _ => style.ToString(),
    };

    public static byte[] Write(Scenario scenario)
    {
        if (scenario.Options.Style == NarrativeStyle.Camt)
        {
            using var stream = new MemoryStream();
            Camt053Writer.Write(stream, [scenario.Statement]);
            return stream.ToArray();
        }

        return Encoding.UTF8.GetBytes(Mt940Writer.WriteToString([scenario.Statement]));
    }

    public static Statement ReadBack(byte[] file)
    {
        using var stream = new MemoryStream(file);
        return StatementFile.Read(stream, out _).ToStatements().Single();
    }
}
