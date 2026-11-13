namespace BankRecon.Core.Statements;

public static class StatementStreamExtensions
{
    /// <summary>Buffers a streamed file into whole statements.</summary>
    public static IEnumerable<Statement> ToStatements(this IEnumerable<StatementPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return Iterate(parts);

        static IEnumerable<Statement> Iterate(IEnumerable<StatementPart> parts)
        {
            List<StatementEntry>? entries = null;
            foreach (var part in parts)
            {
                switch (part)
                {
                    case StatementStarted:
                        entries = [];
                        break;
                    case EntryRead read:
                        (entries ??= []).Add(read.Entry);
                        break;
                    case StatementEnded ended:
                        yield return new Statement
                        {
                            Header = ended.Header,
                            Entries = entries ?? [],
                            Closing = ended.Closing,
                            ClosingAvailable = ended.ClosingAvailable,
                        };
                        entries = null;
                        break;
                }
            }
        }
    }
}
