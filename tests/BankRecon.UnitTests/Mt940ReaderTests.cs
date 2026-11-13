using System.Text;
using BankRecon.Core.Statements;
using BankRecon.Core.Statements.Mt940;

namespace BankRecon.UnitTests;

public sealed class Mt940ReaderTests
{
    private static List<Statement> Read(string text, Mt940ReadOptions? options = null) =>
        Mt940Reader.Read(new StringReader(text), options).ToStatements().ToList();

    private static string Message(params string[] body) =>
        string.Join("\r\n", [":20:REF1", ":25:TR919990100000001000000001", ":60F:C261103TRY100,00", .. body]) + "\r\n";

    [Fact]
    public void Turkish_sample_keeps_turkish_letters_and_extracts_the_counterparty_iban_from_the_account()
    {
        var statements = TestFiles.Parse("mt940-turkish-free-text.sta");

        statements.Should().HaveCount(2);
        statements[0].Header.Iban.Should().Be("TR919990100000001000000001");
        statements[0].Entries[0].Narrative.Should().Be("EFT GELEN ŞİMŞEK GIDA LTD ŞTİ FATURA INV-2026-000123");
        statements[0].Entries[0].CustomerReference.Should().Be("INV-2026-000123");
        statements[0].Entries[1].CustomerReference.Should().BeNull("NONREF means no reference");
        statements[0].Entries[1].Amount.Should().Be(2500m, "\"2500,\" is a valid SWIFT amount");
    }

    [Fact]
    public void Narrative_lines_are_joined_without_a_separator_because_banks_hard_wrap_at_65()
    {
        var entry = TestFiles.Parse("mt940-turkish-free-text.sta")[1].Entries[0];
        entry.Narrative.Should().Be("FAST GELEN ANADOLU TEKSTİL SAN. VE TİC. A.Ş. SO/2026/TR/0012345/01 ODEMESI");
    }

    [Fact]
    public void Reversal_of_a_credit_is_a_debit_and_supplementary_details_are_kept()
    {
        var entry = TestFiles.Parse("mt940-turkish-free-text.sta")[1].Entries[1];
        entry.IsReversal.Should().BeTrue();
        entry.Mark.Should().Be(CreditDebit.Debit);
        entry.SignedAmount.Should().Be(-1200m);
        entry.SupplementaryDetails.Should().Be("HATALI ALACAK DUZELTMESI");
    }

    [Fact]
    public void Entry_date_takes_the_year_closest_to_the_value_date_across_new_year()
    {
        var entry = TestFiles.Parse("mt940-gvc-subfields.sta")[0].Entries[0];
        entry.ValueDate.Should().Be(new DateOnly(2026, 12, 31));
        entry.BookingDate.Should().Be(new DateOnly(2027, 1, 2));
        entry.FundsCode.Should().Be("R");
    }

    [Fact]
    public void Gvc_subfields_yield_end_to_end_id_remittance_name_and_iban()
    {
        var entry = TestFiles.Parse("mt940-gvc-subfields.sta")[0].Entries[0];
        entry.EndToEndId.Should().Be("INV-2026-000777");
        entry.Remittance.Should().Be("RECHNUNG INV-2026-000777 DANKE");
        entry.CounterpartyName.Should().Be("MUSTERFIRMA SYNTH GMBH");
        entry.CounterpartyIban.Should().Be("DE15999900000007654321");
    }

    [Fact]
    public void Fin_wrapped_messages_and_slash_keywords_are_understood()
    {
        var statements = TestFiles.Parse("mt940-sepa-keywords-fin.sta");
        statements.Should().HaveCount(2);
        statements[0].Header.Iban.Should().Be("TR649990100000001000000002", "the IBAN after BIC/ is recognised");
        var entry = statements[0].Entries[0];
        entry.CounterpartyName.Should().Be("EGE MAKINE AS");
        entry.CounterpartyIban.Should().Be("TR299990100000005000000011");
        entry.Remittance.Should().Be("INV-2026-000901 KASIM");
        entry.EndToEndId.Should().Be("E2E-901");
        statements[1].Entries.Should().BeEmpty("a statement without movements is legal");
    }

    [Fact]
    public void Messages_without_a_dash_separator_are_split_at_the_next_20()
    {
        var text = Message(":61:261104C10,00NTRFA//B", ":62F:C261104TRY110,00") +
                   Message(":62F:C261103TRY100,00");
        Read(text).Should().HaveCount(2);
    }

    [Fact]
    public void Statements_that_do_not_add_up_are_reported_not_hidden()
    {
        var statement = Read(Message(":61:261104C10,00NTRFA//B", ":62F:C261104TRY999,00")).Single();
        statement.IsBalanced.Should().BeFalse();
        statement.ComputedClosing.Should().Be(110m);
    }

    [Theory]
    [InlineData(":61:261104C10,00NTRFA//B", "no closing balance")]
    [InlineData(":61:269904C10,00NTRFA//B\r\n:62F:C261104TRY110,00", "invalid value date")]
    [InlineData(":61:261104X10,00NTRFA//B\r\n:62F:C261104TRY110,00", "invalid debit/credit mark")]
    [InlineData(":61:261104C10.00NTRFA//B\r\n:62F:C261104TRY110,00", "invalid amount")]
    [InlineData(":61:261104C10,00XTRFA//B\r\n:62F:C261104TRY110,00", "invalid transaction type")]
    [InlineData(":62F:C261104EUR100,00", "differs from opening currency")]
    [InlineData(":62F:C261104TRY100,00\r\n:61:261104C10,00NTRFA//B", "after the closing balance")]
    public void Malformed_fields_raise_a_format_exception_with_the_line(string body, string message)
    {
        var act = () => Read(Message(body));
        act.Should().Throw<StatementFormatException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void Text_before_any_field_is_rejected()
    {
        var act = () => Read("hello\r\n" + Message(":62F:C261103TRY100,00"));
        act.Should().Throw<StatementFormatException>().Which.Line.Should().Be(1);
    }

    [Fact]
    public void An_overlong_line_is_rejected_without_buffering_it()
    {
        var text = ":20:" + new string('A', 100_000);
        var act = () => Read(text, new Mt940ReadOptions { MaxLineLength = 1024 });
        act.Should().Throw<StatementFormatException>().WithMessage("*longer than 1024*");
    }

    [Fact]
    public void An_overlong_field_is_rejected()
    {
        var lines = Enumerable.Repeat("ABCDEFGHIJ", 100);
        var act = () => Read(Message(":61:261104C10,00NTRFA//B", ":86:X\r\n" + string.Join("\r\n", lines), ":62F:C261104TRY110,00"),
            new Mt940ReadOptions { MaxFieldLength = 500 });
        act.Should().Throw<StatementFormatException>().WithMessage("*longer than 500*");
    }

    [Fact]
    public void Entries_stream_before_the_statement_ends()
    {
        // The reader yields an entry as soon as the following field arrives: a consumer can process a huge
        // statement without the parser holding all entries.
        var parts = Mt940Reader.Read(new StringReader(Message(":61:261104C10,00NTRFA//B", ":61:261104C1,00NTRFC//D", ":62F:C261104TRY111,00")));
        parts.Select(p => p.GetType().Name).Should().Equal("StatementStarted", "EntryRead", "EntryRead", "StatementEnded");
    }

    [Fact]
    public void Windows_1254_encoded_turkish_text_is_detected_and_decoded()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = Message(":61:261104C10,00NTRFA//B", ":86:HAVALE GELEN ÇAĞLAYAN İNŞAAT ŞTİ", ":62F:C261104TRY110,00");
        var bytes = Encoding.GetEncoding(1254).GetBytes(text);
        StatementFile.DetectMt940Encoding(bytes).CodePage.Should().Be(1254);

        var entry = TestFiles.Parse(bytes).Single().Entries.Single();
        entry.Narrative.Should().Be("HAVALE GELEN ÇAĞLAYAN İNŞAAT ŞTİ");
    }

    [Fact]
    public void Utf8_is_preferred_when_the_bytes_are_valid_utf8() =>
        StatementFile.DetectMt940Encoding(Encoding.UTF8.GetBytes(":86:ŞİŞLİ")).WebName.Should().Be("utf-8");

    [Theory]
    [InlineData("   \r\n<Document/>", StatementFormat.Camt053)]
    [InlineData("﻿:20:X", StatementFormat.Mt940)]
    [InlineData("{1:F01}", StatementFormat.Mt940)]
    public void Format_is_detected_from_the_first_meaningful_character(string head, StatementFormat expected) =>
        StatementFile.DetectFormat(Encoding.UTF8.GetBytes(head)).Should().Be(expected);

    [Fact]
    public void Unknown_formats_are_rejected() =>
        FluentActions.Invoking(() => StatementFile.DetectFormat("PK\u0003\u0004"u8)).Should().Throw<StatementFormatException>();
}
