using System.Text;
using BankRecon.Core.Statements;
using BankRecon.Core.Statements.Camt;

namespace BankRecon.UnitTests;

public sealed class Camt053ReaderTests
{
    private static List<Statement> Read(string xml) =>
        Camt053Reader.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml))).ToStatements().ToList();

    [Fact]
    public void Version_08_sample_reads_batch_reversal_and_skips_pending_entries()
    {
        var statement = TestFiles.Parse("camt053-v08-batch.xml").Single();

        statement.Header.Format.Should().Be("camt.053.001.08");
        statement.Header.SequenceNumber.Should().Be("314");
        statement.Entries.Should().HaveCount(3, "the PDNG entry is not booked");
        statement.Entries[0].EndToEndId.Should().Be("INV-2026-000412");
        statement.Entries[0].CounterpartyName.Should().Be("DEFNE MOBİLYA TİC LTD ŞTİ");
        statement.Entries[1].Details.Should().HaveCount(2);
        statement.Entries[1].Details[0].EndToEndId.Should().BeNull("NOTPROVIDED is not a reference");
        statement.Entries[1].Details[1].Remittance.Should().Be("INV-2026-000416 KISMI", "Ustrd lines are joined with a space");
        statement.Entries[1].BookingDate.Should().Be(new DateOnly(2026, 11, 10), "DtTm is reduced to its date");
        statement.Entries[2].IsReversal.Should().BeTrue();
        statement.Entries[2].TransactionType.Should().Be("RVSL");
        statement.ClosingAvailable!.Amount.Should().Be(61980.25m);
    }

    [Fact]
    public void Version_02_sample_reads_text_status_direct_names_and_previously_closed_balance()
    {
        var statement = TestFiles.Parse("camt053-v02.xml").Single();

        statement.Header.Format.Should().Be("camt.053.001.02");
        statement.Header.Account.Should().Be("99990000-0001234567");
        statement.Header.Iban.Should().BeNull();
        statement.Header.Opening.SignedAmount.Should().Be(-2000m, "PRCD stands in for a missing OPBD");
        statement.Entries[0].CounterpartyName.Should().Be("Synth Handel GmbH");
        statement.Entries[0].Remittance.Should().Be("RF18539007547034", "a structured creditor reference is used when there is no Ustrd");
        statement.Entries[1].CounterpartyName.Should().Be("Synth Leasing BV", "for a debit the counterparty is the creditor");
        statement.Header.CreatedAt.Should().Be(new DateTimeOffset(2026, 11, 13, 5, 30, 0, TimeSpan.Zero), "group header time is the fallback");
    }

    [Fact]
    public void A_document_type_definition_is_refused_so_entity_expansion_and_external_entities_cannot_run()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE Document [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]>
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.08"><BkToCstmrStmt>&b;</BkToCstmrStmt></Document>
            """;
        FluentActions.Invoking(() => Read(xml)).Should().Throw<StatementFormatException>().WithMessage("*DTD*");
    }

    [Fact]
    public void External_entity_references_are_refused()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE d [<!ENTITY x SYSTEM "file:///etc/passwd">]>
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.08">&x;</Document>
            """;
        FluentActions.Invoking(() => Read(xml)).Should().Throw<StatementFormatException>();
    }

    [Theory]
    [InlineData("urn:iso:std:iso:20022:tech:xsd:camt.052.001.08", "Not a camt.053")]
    [InlineData("urn:iso:std:iso:20022:tech:xsd:camt.053.001.99", "Unsupported camt.053 version")]
    public void Other_messages_and_versions_are_rejected(string ns, string message) =>
        FluentActions.Invoking(() => Read($"<Document xmlns=\"{ns}\"><BkToCstmrStmt/></Document>"))
            .Should().Throw<StatementFormatException>().WithMessage($"*{message}*");

    [Fact]
    public void A_statement_without_closing_balance_is_rejected()
    {
        const string xml = """
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.08"><BkToCstmrStmt><Stmt>
              <Id>S</Id><Acct><Id><IBAN>TR919990100000001000000001</IBAN></Id></Acct>
              <Bal><Tp><CdOrPrtry><Cd>OPBD</Cd></CdOrPrtry></Tp><Amt Ccy="TRY">1.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><Dt><Dt>2026-11-01</Dt></Dt></Bal>
            </Stmt></BkToCstmrStmt></Document>
            """;
        FluentActions.Invoking(() => Read(xml)).Should().Throw<StatementFormatException>().WithMessage("*CLBD*");
    }

    [Fact]
    public void Truncated_documents_are_reported_as_format_errors()
    {
        var xml = File.ReadAllText(TestFiles.Sample("camt053-v08-batch.xml"));
        FluentActions.Invoking(() => Read(xml[..(xml.Length / 2)])).Should().Throw<StatementFormatException>();
    }

    [Fact]
    public void Negative_or_signed_amounts_are_rejected_because_the_direction_is_in_CdtDbtInd()
    {
        var xml = File.ReadAllText(TestFiles.Sample("camt053-v08-batch.xml")).Replace(">4250.00<", ">-4250.00<", StringComparison.Ordinal);
        FluentActions.Invoking(() => Read(xml)).Should().Throw<StatementFormatException>().WithMessage("*Invalid amount*");
    }
}
