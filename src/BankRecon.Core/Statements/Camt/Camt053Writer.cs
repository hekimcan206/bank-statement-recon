using System.Globalization;
using System.Text;
using System.Xml;

namespace BankRecon.Core.Statements.Camt;

/// <summary>
/// Writes camt.053 documents in version 001.02 or 001.08 element order. Used for synthetic data and the round-trip
/// property tests. The two versions differ where this writer touches them in two places: the entry status
/// (a code in 02, a Cd element in 08) and party names (Dbtr/Nm in 02, Dbtr/Pty/Nm in 08) plus the TxDtls amount.
/// </summary>
public static class Camt053Writer
{
    public const int MaxUnstructuredLength = 140;

    public static void Write(Stream output, IReadOnlyList<Statement> statements, int version = 8, DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(statements);
        if (version is not (2 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Only 001.02 and 001.08 are written.");
        }

        var ns = Camt053Reader.NamespacePrefix + version.ToString("D2", CultureInfo.InvariantCulture);
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false };
        using var w = XmlWriter.Create(output, settings);
        w.WriteStartDocument();
        w.WriteStartElement("Document", ns);
        w.WriteStartElement("BkToCstmrStmt", ns);
        w.WriteStartElement("GrpHdr", ns);
        w.WriteElementString("MsgId", ns, "SYNTH-" + (statements.Count > 0 ? statements[0].Header.StatementId : "EMPTY"));
        w.WriteElementString("CreDtTm", ns, (createdAt ?? new DateTimeOffset(2026, 11, 13, 6, 0, 0, TimeSpan.FromHours(3)))
            .ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
        w.WriteEndElement();

        foreach (var statement in statements)
        {
            WriteStatement(w, ns, version, statement);
        }

        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndDocument();
    }

    public static string WriteToString(IReadOnlyList<Statement> statements, int version = 8)
    {
        using var stream = new MemoryStream();
        Write(stream, statements, version);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteStatement(XmlWriter w, string ns, int version, Statement statement)
    {
        var header = statement.Header;
        w.WriteStartElement("Stmt", ns);
        w.WriteElementString("Id", ns, header.StatementId);
        if (header.SequenceNumber is not null && header.SequenceNumber.All(char.IsAsciiDigit))
        {
            w.WriteElementString("ElctrncSeqNb", ns, header.SequenceNumber);
        }

        if (header.CreatedAt is { } created)
        {
            w.WriteElementString("CreDtTm", ns, created.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
        }

        w.WriteStartElement("Acct", ns);
        w.WriteStartElement("Id", ns);
        if (Iban.IsValid(header.Account))
        {
            w.WriteElementString("IBAN", ns, header.Account);
        }
        else
        {
            w.WriteStartElement("Othr", ns);
            w.WriteElementString("Id", ns, header.Account);
            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteElementString("Ccy", ns, header.Currency);
        w.WriteEndElement();

        WriteBalance(w, ns, "OPBD", header.Opening);
        WriteBalance(w, ns, "CLBD", statement.Closing);
        if (statement.ClosingAvailable is not null)
        {
            WriteBalance(w, ns, "CLAV", statement.ClosingAvailable);
        }

        foreach (var entry in statement.Entries)
        {
            WriteEntry(w, ns, version, header.Currency, entry);
        }

        w.WriteEndElement();
    }

    private static void WriteBalance(XmlWriter w, string ns, string code, Balance balance)
    {
        w.WriteStartElement("Bal", ns);
        w.WriteStartElement("Tp", ns);
        w.WriteStartElement("CdOrPrtry", ns);
        w.WriteElementString("Cd", ns, code);
        w.WriteEndElement();
        w.WriteEndElement();
        WriteAmount(w, ns, balance.Amount, balance.Currency);
        w.WriteElementString("CdtDbtInd", ns, Mark(balance.Mark));
        WriteDate(w, ns, "Dt", balance.Date);
        w.WriteEndElement();
    }

    private static void WriteEntry(XmlWriter w, string ns, int version, string currency, StatementEntry entry)
    {
        w.WriteStartElement("Ntry", ns);
        if (entry.CustomerReference is not null)
        {
            w.WriteElementString("NtryRef", ns, entry.CustomerReference);
        }

        WriteAmount(w, ns, entry.Amount, currency);
        w.WriteElementString("CdtDbtInd", ns, Mark(entry.Mark));
        if (entry.IsReversal)
        {
            w.WriteElementString("RvslInd", ns, "true");
        }

        if (version >= 8)
        {
            w.WriteStartElement("Sts", ns);
            w.WriteElementString("Cd", ns, "BOOK");
            w.WriteEndElement();
        }
        else
        {
            w.WriteElementString("Sts", ns, "BOOK");
        }

        if (entry.BookingDate is { } booked)
        {
            WriteDate(w, ns, "BookgDt", booked);
        }

        WriteDate(w, ns, "ValDt", entry.ValueDate);
        if (entry.BankReference is not null)
        {
            w.WriteElementString("AcctSvcrRef", ns, entry.BankReference);
        }

        WriteTransactionCode(w, ns, entry.TransactionType);

        var details = entry.Details.Count > 1
            ? entry.Details
            : HasDetail(entry)
                ? [new TransactionDetail
                {
                    EndToEndId = entry.EndToEndId,
                    Remittance = entry.Remittance,
                    CounterpartyName = entry.CounterpartyName,
                    CounterpartyIban = entry.CounterpartyIban,
                }]
                : [];
        if (details.Count > 0)
        {
            w.WriteStartElement("NtryDtls", ns);
            foreach (var detail in details)
            {
                WriteDetail(w, ns, version, currency, entry.Mark, detail);
            }

            w.WriteEndElement();
        }

        if (entry.Narrative is not null)
        {
            w.WriteElementString("AddtlNtryInf", ns, entry.Narrative);
        }

        w.WriteEndElement();
    }

    private static bool HasDetail(StatementEntry entry) =>
        entry.EndToEndId is not null || entry.Remittance is not null || entry.CounterpartyName is not null ||
        entry.CounterpartyIban is not null;

    private static void WriteDetail(XmlWriter w, string ns, int version, string currency, CreditDebit mark, TransactionDetail detail)
    {
        w.WriteStartElement("TxDtls", ns);
        if (detail.EndToEndId is not null || detail.BankReference is not null)
        {
            w.WriteStartElement("Refs", ns);
            if (detail.BankReference is not null)
            {
                w.WriteElementString("AcctSvcrRef", ns, detail.BankReference);
            }

            if (detail.EndToEndId is not null)
            {
                w.WriteElementString("EndToEndId", ns, detail.EndToEndId);
            }

            w.WriteEndElement();
        }

        if (detail.Amount is { } amount)
        {
            if (version >= 8)
            {
                WriteAmount(w, ns, amount, currency);
                w.WriteElementString("CdtDbtInd", ns, Mark(mark));
            }
            else
            {
                w.WriteStartElement("AmtDtls", ns);
                w.WriteStartElement("TxAmt", ns);
                WriteAmount(w, ns, amount, currency);
                w.WriteEndElement();
                w.WriteEndElement();
            }
        }

        if (detail.CounterpartyName is not null || detail.CounterpartyIban is not null)
        {
            var role = mark == CreditDebit.Credit ? "Dbtr" : "Cdtr";
            w.WriteStartElement("RltdPties", ns);
            if (detail.CounterpartyName is not null)
            {
                w.WriteStartElement(role, ns);
                if (version >= 8)
                {
                    w.WriteStartElement("Pty", ns);
                    w.WriteElementString("Nm", ns, detail.CounterpartyName);
                    w.WriteEndElement();
                }
                else
                {
                    w.WriteElementString("Nm", ns, detail.CounterpartyName);
                }

                w.WriteEndElement();
            }

            if (detail.CounterpartyIban is not null)
            {
                w.WriteStartElement(role + "Acct", ns);
                w.WriteStartElement("Id", ns);
                w.WriteElementString("IBAN", ns, detail.CounterpartyIban);
                w.WriteEndElement();
                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        if (detail.Remittance is not null)
        {
            if (detail.Remittance.Length > MaxUnstructuredLength)
            {
                throw new ArgumentException($"Remittance longer than {MaxUnstructuredLength} characters.", nameof(detail));
            }

            w.WriteStartElement("RmtInf", ns);
            w.WriteElementString("Ustrd", ns, detail.Remittance);
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    private static void WriteTransactionCode(XmlWriter w, string ns, string code)
    {
        w.WriteStartElement("BkTxCd", ns);
        var parts = code.Split('/');
        if (parts.Length == 3)
        {
            w.WriteStartElement("Domn", ns);
            w.WriteElementString("Cd", ns, parts[0]);
            w.WriteStartElement("Fmly", ns);
            w.WriteElementString("Cd", ns, parts[1]);
            w.WriteElementString("SubFmlyCd", ns, parts[2]);
            w.WriteEndElement();
            w.WriteEndElement();
        }
        else
        {
            w.WriteStartElement("Prtry", ns);
            w.WriteElementString("Cd", ns, code);
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    private static void WriteAmount(XmlWriter w, string ns, decimal amount, string currency)
    {
        w.WriteStartElement("Amt", ns);
        w.WriteAttributeString("Ccy", currency);
        w.WriteString(amount.ToString("0.00#", CultureInfo.InvariantCulture));
        w.WriteEndElement();
    }

    private static void WriteDate(XmlWriter w, string ns, string element, DateOnly date)
    {
        w.WriteStartElement(element, ns);
        w.WriteElementString("Dt", ns, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        w.WriteEndElement();
    }

    private static string Mark(CreditDebit mark) => mark == CreditDebit.Credit ? "CRDT" : "DBIT";
}
