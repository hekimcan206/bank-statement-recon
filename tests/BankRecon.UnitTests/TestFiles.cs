using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BankRecon.Core.Statements;

namespace BankRecon.UnitTests;

internal static class TestFiles
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "samples", name);

    public static IReadOnlyList<Statement> Parse(string sample)
    {
        using var stream = File.OpenRead(Sample(sample));
        return StatementFile.Read(stream, out _).ToStatements().ToList();
    }

    public static IReadOnlyList<Statement> Parse(byte[] content)
    {
        using var stream = new MemoryStream(content);
        return StatementFile.Read(stream, out _).ToStatements().ToList();
    }

    /// <summary>The source directory of the golden files, so UPDATE_GOLDEN=1 rewrites the committed copies.</summary>
    public static string GoldenSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BankStatementRecon.slnx")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException("Repository root not found.")
            : Path.Combine(directory.FullName, "tests", "BankRecon.UnitTests", "Golden");
    }
}
