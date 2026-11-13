using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace BankRecon.Core.Statements.Camt;

public sealed record Camt053ReadOptions
{
    /// <summary>Entries whose status is not BOOK (PDNG, INFO) are skipped: they are not on the account yet.</summary>
    public bool BookedOnly { get; init; } = true;

    /// <summary>Upper bound for one Ntry element; an entry is small, so a megabyte-sized one is an attack or garbage.</summary>
    public long MaxCharactersPerEntry { get; init; } = 256 * 1024;
}

/// <summary>
/// Streaming ISO 20022 camt.053 (BankToCustomerStatement) reader for message versions 001.02 to 001.13. The document
/// is read with an <see cref="XmlReader"/>; only one Ntry at a time is materialised as an <see cref="XElement"/>, so
/// memory stays flat for statements of any length. DTDs are prohibited and no external resource is ever resolved.
/// </summary>
public static class Camt053Reader
{
    public const string NamespacePrefix = "urn:iso:std:iso:20022:tech:xsd:camt.053.001.";

    public static IEnumerable<StatementPart> Read(Stream input, Camt053ReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Iterate(() => XmlReader.Create(input, Settings()), options ?? new Camt053ReadOptions());
    }

    public static IEnumerable<StatementPart> Read(TextReader input, Camt053ReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Iterate(() => XmlReader.Create(input, Settings()), options ?? new Camt053ReadOptions());
    }

    internal static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        IgnoreWhitespace = true,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = false,
    };

    private static IEnumerable<StatementPart> Iterate(Func<XmlReader> open, Camt053ReadOptions options)
    {
        using var reader = open();
        var parser = new Parser(reader, options);
        while (true)
        {
            StatementPart? part;
            try
            {
                part = parser.Next();
            }
            catch (XmlException ex)
            {
                throw new StatementFormatException($"Malformed camt.053 XML: {ex.Message}", ex);
            }
            catch (FormatException ex) when (ex is not StatementFormatException)
            {
                throw new StatementFormatException($"Invalid value in camt.053: {ex.Message}", ex);
            }
            catch (OverflowException ex)
            {
                throw new StatementFormatException($"Value out of range in camt.053: {ex.Message}", ex);
            }

            if (part is null)
            {
                yield break;
            }

            yield return part;
        }
    }

    private sealed class Parser(XmlReader reader, Camt053ReadOptions options)
    {
        private readonly Queue<StatementPart> _queue = new();
        private XNamespace _ns = XNamespace.None;
        private string _version = string.Empty;
        private bool _initialised;
        private bool _done;
        private int _containerDepth;
        private DateTimeOffset? _groupCreatedAt;
        private StatementState? _stmt;

        public StatementPart? Next()
        {
            while (_queue.Count == 0 && !_done)
            {
                Step();
            }

            return _queue.Count > 0 ? _queue.Dequeue() : null;
        }

        private void Step()
        {
            if (!_initialised)
            {
                Initialise();
                return;
            }

            if (_stmt is null)
            {
                SeekStatement();
            }
            else
            {
                StepStatement();
            }
        }

        private void Initialise()
        {
            _initialised = true;
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Document" ||
                !reader.NamespaceURI.StartsWith(NamespacePrefix, StringComparison.Ordinal))
            {
                throw new StatementFormatException("Not a camt.053 document (expected Document in a camt.053.001.xx namespace)");
            }

            var suffix = reader.NamespaceURI[NamespacePrefix.Length..];
            if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version is < 2 or > 13)
            {
                throw new StatementFormatException($"Unsupported camt.053 version '{suffix}' (supported: 001.02 to 001.13)");
            }

            _ns = reader.NamespaceURI;
            _version = "camt.053.001." + suffix;
            if (reader.IsEmptyElement || !reader.Read())
            {
                throw new StatementFormatException("Empty camt.053 document");
            }

            while (!(reader.NodeType == XmlNodeType.Element && reader.LocalName == "BkToCstmrStmt" && reader.Depth == 1))
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0)
                {
                    throw new StatementFormatException("camt.053 document without BkToCstmrStmt");
                }

                if (reader.NodeType == XmlNodeType.Element)
                {
                    reader.Skip();
                }
                else if (!reader.Read())
                {
                    throw new StatementFormatException("Unexpected end of camt.053 document");
                }
            }

            _containerDepth = reader.Depth;
            if (reader.IsEmptyElement)
            {
                Finish();
                return;
            }

            reader.Read();
        }

        private void SeekStatement()
        {
            while (true)
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == _containerDepth)
                {
                    Finish();
                    return;
                }

                if (reader.NodeType == XmlNodeType.Element && reader.Depth == _containerDepth + 1)
                {
                    if (reader.LocalName == "GrpHdr" && reader.NamespaceURI == _ns.NamespaceName)
                    {
                        var header = ReadElement();
                        _groupCreatedAt = ParseDateTime(Value(header, "CreDtTm"));
                        continue;
                    }

                    if (reader.LocalName == "Stmt" && reader.NamespaceURI == _ns.NamespaceName)
                    {
                        if (reader.IsEmptyElement)
                        {
                            throw new StatementFormatException("Empty Stmt element");
                        }

                        _stmt = new StatementState(reader.Depth);
                        reader.Read();
                        return;
                    }

                    reader.Skip();
                    continue;
                }

                if (!reader.Read())
                {
                    throw new StatementFormatException("Unexpected end of camt.053 document");
                }
            }
        }

        private void Finish()
        {
            // Read to the end so trailing garbage or an unclosed root is reported instead of ignored.
            while (reader.Read())
            {
            }

            _done = true;
        }

        private void StepStatement()
        {
            var stmt = _stmt!;
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == stmt.Depth)
            {
                EnsureStarted(stmt);
                var closing = stmt.Balance("CLBD") ?? throw new StatementFormatException($"Statement '{stmt.Id}' has no CLBD balance");
                _queue.Enqueue(new StatementEnded(stmt.Header!, closing, stmt.Balance("CLAV"), stmt.EntryCount, stmt.Running));
                _stmt = null;
                reader.Read();
                return;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                if (!reader.Read())
                {
                    throw new StatementFormatException("Unexpected end inside Stmt");
                }

                return;
            }

            if (reader.NamespaceURI != _ns.NamespaceName)
            {
                reader.Skip();
                return;
            }

            switch (reader.LocalName)
            {
                case "Id":
                    stmt.Id = reader.ReadElementContentAsString();
                    break;
                case "ElctrncSeqNb" or "LglSeqNb":
                    var sequence = reader.ReadElementContentAsString();
                    stmt.Sequence ??= sequence;

                    break;
                case "CreDtTm":
                    stmt.CreatedAt = ParseDateTime(reader.ReadElementContentAsString());
                    break;
                case "Acct":
                    ReadAccount(stmt, ReadElement());
                    break;
                case "Bal":
                    stmt.Balances.Add(ReadBalance(ReadElement()));
                    break;
                case "Ntry":
                    EnsureStarted(stmt);
                    var entry = ReadEntry(ReadElement(options.MaxCharactersPerEntry), out var booked);
                    if (booked || !options.BookedOnly)
                    {
                        stmt.Running += entry.SignedAmount;
                        _queue.Enqueue(new EntryRead(stmt.Header!, entry, stmt.EntryCount));
                        stmt.EntryCount++;
                    }

                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        private void EnsureStarted(StatementState stmt)
        {
            if (stmt.Header is not null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(stmt.Id) || stmt.Account is null)
            {
                throw new StatementFormatException("Stmt without Id or Acct before its first entry");
            }

            var opening = stmt.Balance("OPBD") ?? stmt.Balance("PRCD")
                ?? throw new StatementFormatException($"Statement '{stmt.Id}' has no OPBD or PRCD balance");
            var currency = stmt.Currency ?? opening.Currency;
            stmt.Header = new StatementHeader
            {
                Format = _version,
                StatementId = stmt.Id,
                SequenceNumber = stmt.Sequence,
                Account = stmt.Account,
                Iban = Iban.Normalize(stmt.Account),
                Currency = currency,
                Opening = opening,
                Closing = stmt.Balance("CLBD"),
                CreatedAt = stmt.CreatedAt ?? _groupCreatedAt,
            };
            stmt.Running = opening.SignedAmount;
            _queue.Enqueue(new StatementStarted(stmt.Header));
        }

        private XElement ReadElement(long maxCharacters = 1024 * 1024)
        {
            var element = (XElement)XNode.ReadFrom(reader);
            if (CountCharacters(element) > maxCharacters)
            {
                throw new StatementFormatException($"Element {element.Name.LocalName} exceeds {maxCharacters} characters");
            }

            return element;
        }

        private static long CountCharacters(XElement element)
        {
            long total = 0;
            foreach (var node in element.DescendantNodesAndSelf())
            {
                if (node is XText text)
                {
                    total += text.Value.Length;
                }
            }

            return total;
        }

        private void ReadAccount(StatementState stmt, XElement acct)
        {
            var id = acct.Element(_ns + "Id");
            stmt.Account = Value(id, "IBAN") ?? Value(id?.Element(_ns + "Othr"), "Id")
                ?? throw new StatementFormatException("Acct without IBAN or Othr/Id");
            stmt.Currency = Value(acct, "Ccy");
        }

        private TypedBalance ReadBalance(XElement bal)
        {
            var type = bal.Element(_ns + "Tp")?.Element(_ns + "CdOrPrtry");
            var code = Value(type, "Cd") ?? Value(type, "Prtry") ?? throw new StatementFormatException("Bal without type code");
            var (amount, currency) = ReadAmount(bal.Element(_ns + "Amt") ?? throw new StatementFormatException("Bal without Amt"));
            var mark = ReadMark(bal);
            var date = ReadDate(bal.Element(_ns + "Dt")) ?? throw new StatementFormatException("Bal without Dt");
            return new TypedBalance(code, new Balance(mark, date, currency, amount));
        }

        private StatementEntry ReadEntry(XElement ntry, out bool booked)
        {
            var (amount, _) = ReadAmount(ntry.Element(_ns + "Amt") ?? throw new StatementFormatException("Ntry without Amt"));
            var mark = ReadMark(ntry);
            var reversal = Value(ntry, "RvslInd") is "true" or "1";
            var status = ntry.Element(_ns + "Sts");
            var statusCode = status is null ? null : Value(status, "Cd") ?? status.Value.Trim();
            booked = statusCode is null or "BOOK";

            var valueDate = ReadDate(ntry.Element(_ns + "ValDt"));
            var bookingDate = ReadDate(ntry.Element(_ns + "BookgDt"));
            var details = ntry.Elements(_ns + "NtryDtls").SelectMany(d => d.Elements(_ns + "TxDtls"))
                .Select(tx => ReadDetail(tx, mark)).ToList();
            var single = details.Count == 1 ? details[0] : null;

            return new StatementEntry
            {
                ValueDate = valueDate ?? bookingDate ?? throw new StatementFormatException("Ntry without ValDt or BookgDt"),
                BookingDate = bookingDate,
                Mark = mark,
                IsReversal = reversal,
                Amount = amount,
                TransactionType = ReadTransactionCode(ntry.Element(_ns + "BkTxCd")) ?? "NTRF",
                CustomerReference = Value(ntry, "NtryRef"),
                BankReference = Value(ntry, "AcctSvcrRef"),
                Narrative = Value(ntry, "AddtlNtryInf"),
                Remittance = single?.Remittance,
                CounterpartyName = single?.CounterpartyName,
                CounterpartyIban = single?.CounterpartyIban,
                EndToEndId = single?.EndToEndId,
                Details = details.Count > 1 ? details : [],
            };
        }

        private TransactionDetail ReadDetail(XElement tx, CreditDebit entryMark)
        {
            var refs = tx.Element(_ns + "Refs");
            var amountElement = tx.Element(_ns + "Amt")
                ?? tx.Element(_ns + "AmtDtls")?.Element(_ns + "TxAmt")?.Element(_ns + "Amt")
                ?? tx.Element(_ns + "AmtDtls")?.Element(_ns + "InstdAmt")?.Element(_ns + "Amt");
            var remittance = tx.Element(_ns + "RmtInf");
            var unstructured = remittance?.Elements(_ns + "Ustrd").Select(u => u.Value.Trim()).Where(u => u.Length > 0).ToList();
            var structuredRef = remittance?.Elements(_ns + "Strd").Select(s => s.Element(_ns + "CdtrRefInf")).Select(c => Value(c, "Ref"))
                .FirstOrDefault(r => r is not null);

            // For money coming in the counterparty is the debtor; for money going out it is the creditor.
            var parties = tx.Element(_ns + "RltdPties");
            var role = entryMark == CreditDebit.Credit ? "Dbtr" : "Cdtr";
            var party = parties?.Element(_ns + role);
            var name = Value(party?.Element(_ns + "Pty"), "Nm") ?? Value(party, "Nm");
            var account = parties?.Element(_ns + role + "Acct")?.Element(_ns + "Id");
            var endToEnd = Value(refs, "EndToEndId");

            return new TransactionDetail
            {
                Amount = amountElement is null ? null : ReadAmount(amountElement).Amount,
                EndToEndId = endToEnd is "NOTPROVIDED" ? null : endToEnd,
                BankReference = Value(refs, "AcctSvcrRef"),
                Remittance = unstructured is { Count: > 0 } ? string.Join(' ', unstructured) : structuredRef,
                CounterpartyName = name,
                CounterpartyIban = Iban.Normalize(Value(account, "IBAN")),
            };
        }

        private string? ReadTransactionCode(XElement? code)
        {
            if (code is null)
            {
                return null;
            }

            var domain = code.Element(_ns + "Domn");
            var family = domain?.Element(_ns + "Fmly");
            var domainCode = Value(domain, "Cd");
            if (domainCode is not null && family is not null)
            {
                return $"{domainCode}/{Value(family, "Cd")}/{Value(family, "SubFmlyCd")}";
            }

            return Value(code.Element(_ns + "Prtry"), "Cd");
        }

        private static (decimal Amount, string Currency) ReadAmount(XElement amt)
        {
            var currency = (string?)amt.Attribute("Ccy");
            if (currency is null || currency.Length != 3)
            {
                throw new StatementFormatException("Amt without a 3-letter Ccy attribute");
            }

            return decimal.TryParse(amt.Value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                ? (amount, currency)
                : throw new StatementFormatException($"Invalid amount '{Truncate(amt.Value)}'");
        }

        private CreditDebit ReadMark(XElement parent) => Value(parent, "CdtDbtInd") switch
        {
            "CRDT" => CreditDebit.Credit,
            "DBIT" => CreditDebit.Debit,
            var other => throw new StatementFormatException($"Invalid CdtDbtInd '{Truncate(other)}'"),
        };

        private DateOnly? ReadDate(XElement? container)
        {
            if (container is null)
            {
                return null;
            }

            var date = Value(container, "Dt");
            if (date is not null)
            {
                return DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? d
                    : throw new StatementFormatException($"Invalid date '{Truncate(date)}'");
            }

            var dateTime = ParseDateTime(Value(container, "DtTm"));
            return dateTime is null ? null : DateOnly.FromDateTime(dateTime.Value.DateTime);
        }

        private static DateTimeOffset? ParseDateTime(string? value)
        {
            if (value is null)
            {
                return null;
            }

            // ISO 8601 with or without offset; a local time without offset is taken as written.
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : throw new StatementFormatException($"Invalid date-time '{Truncate(value)}'");
        }

        private string? Value(XElement? parent, string name)
        {
            var value = parent?.Element(_ns + name)?.Value.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static string Truncate(string? value) => value is null ? "(missing)" : value.Length <= 40 ? value : value[..40] + "...";
    }

    private sealed record TypedBalance(string Code, Balance Value);

    private sealed class StatementState(int depth)
    {
        public int Depth { get; } = depth;
        public string? Id { get; set; }
        public string? Sequence { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public string? Account { get; set; }
        public string? Currency { get; set; }
        public List<TypedBalance> Balances { get; } = [];
        public StatementHeader? Header { get; set; }
        public int EntryCount { get; set; }
        public decimal Running { get; set; }

        public Balance? Balance(string code) => Balances.FirstOrDefault(b => b.Code == code)?.Value;
    }
}
