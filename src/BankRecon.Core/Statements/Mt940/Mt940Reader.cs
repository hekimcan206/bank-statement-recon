using System.Text;

namespace BankRecon.Core.Statements.Mt940;

/// <summary>Limits that keep a hostile or broken upload from exhausting memory.</summary>
public sealed record Mt940ReadOptions
{
    public int MaxLineLength { get; init; } = 2048;

    /// <summary>An :86: field is at most 6 x 65 characters in the standard; banks exceed it, but not by kilobytes.</summary>
    public int MaxFieldLength { get; init; } = 8192;

    public int MaxEntriesPerStatement { get; init; } = 10_000_000;
}

/// <summary>
/// Streaming MT940 reader. Accepts plain files (one message after another, separated by "-" lines or simply by the
/// next :20:) and FIN-wrapped messages ({1:...}{2:...}{4: ... -}). Each <see cref="EntryRead"/> is produced as soon
/// as its :86: field (or the next field) has been read, so memory use does not grow with the statement.
/// </summary>
public static class Mt940Reader
{
    public static IEnumerable<StatementPart> Read(TextReader reader, Mt940ReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return ReadCore(reader, options ?? new Mt940ReadOptions());
    }

    private static IEnumerable<StatementPart> ReadCore(TextReader textReader, Mt940ReadOptions options)
    {
        var lines = new BoundedLineReader(textReader, options.MaxLineLength);
        var state = new MessageState(options);
        string? tag = null;
        var value = new StringBuilder();
        long fieldLine = 0;
        var output = new List<StatementPart>(2);

        string? line;
        while ((line = lines.ReadLine()) is not null)
        {
            var content = StripBlockHeader(line);
            if (content is null)
            {
                continue;
            }

            if (IsMessageEnd(content))
            {
                FlushField();
                state.EndMessage(output, lines.LineNumber);
                foreach (var part in Drain())
                {
                    yield return part;
                }

                continue;
            }

            if (TryFieldStart(content, out var newTag, out var rest))
            {
                FlushField();
                foreach (var part in Drain())
                {
                    yield return part;
                }

                tag = newTag;
                fieldLine = lines.LineNumber;
                value.Clear().Append(rest);
                continue;
            }

            if (content.Trim().Length == 0)
            {
                continue;
            }

            if (tag is null)
            {
                throw new StatementFormatException("Text outside of any field", lines.LineNumber);
            }

            if (value.Length + content.Length + 1 > options.MaxFieldLength)
            {
                throw new StatementFormatException($"Field :{tag}: longer than {options.MaxFieldLength} characters", fieldLine);
            }

            value.Append('\n').Append(content);
        }

        FlushField();
        state.EndMessage(output, lines.LineNumber);
        foreach (var part in Drain())
        {
            yield return part;
        }

        void FlushField()
        {
            if (tag is not null)
            {
                state.OnField(tag, value.ToString(), fieldLine, output);
                tag = null;
                value.Clear();
            }
        }

        List<StatementPart> Drain()
        {
            if (output.Count == 0)
            {
                return [];
            }

            var drained = new List<StatementPart>(output);
            output.Clear();
            return drained;
        }
    }

    /// <summary>Removes "{1:...}{2:...}{4:" so the remainder of the line is ordinary block-4 text.</summary>
    private static string? StripBlockHeader(string line)
    {
        if (!line.StartsWith('{'))
        {
            return line;
        }

        var block4 = line.IndexOf("{4:", StringComparison.Ordinal);
        return block4 < 0 ? null : line[(block4 + 3)..];
    }

    private static bool IsMessageEnd(string content)
    {
        var trimmed = content.Trim();
        return trimmed is "-" or "-}" or "$" || trimmed.StartsWith("-}", StringComparison.Ordinal);
    }

    private static bool TryFieldStart(string content, out string tag, out string rest)
    {
        tag = string.Empty;
        rest = string.Empty;
        if (content.Length < 4 || content[0] != ':' || !char.IsAsciiDigit(content[1]) || !char.IsAsciiDigit(content[2]))
        {
            return false;
        }

        var end = content[3] == ':' ? 3 : content.Length > 4 && char.IsAsciiLetterUpper(content[3]) && content[4] == ':' ? 4 : -1;
        if (end < 0)
        {
            return false;
        }

        tag = content[1..end];
        rest = content[(end + 1)..];
        return true;
    }

    private sealed class MessageState(Mt940ReadOptions options)
    {
        private string? _statementId;
        private string? _account;
        private string? _sequence;
        private StatementHeader? _header;
        private StatementEntry? _pendingEntry;
        private bool _pendingHasNarrative;
        private Balance? _closing;
        private Balance? _closingAvailable;
        private int _entryCount;
        private decimal _running;
        private long _messageStartLine;

        public void OnField(string tag, string value, long line, List<StatementPart> output)
        {
            switch (tag)
            {
                case "20":
                    if (_statementId is not null)
                    {
                        // No "-" separator between messages: the next :20: closes the previous statement.
                        EndMessage(output, line);
                    }

                    _statementId = RequireText(value, tag, line, 35);
                    _messageStartLine = line;
                    break;
                case "21":
                    break;
                case "25":
                    RequireStatement(tag, line);
                    _account = RequireText(value, tag, line, 35);
                    break;
                case "28" or "28C":
                    RequireStatement(tag, line);
                    _sequence = RequireText(value, tag, line, 16);
                    break;
                case "60F" or "60M":
                    RequireStatement(tag, line);
                    if (_account is null)
                    {
                        throw new StatementFormatException("Opening balance before :25: account", line);
                    }

                    if (_header is not null)
                    {
                        throw new StatementFormatException("Second opening balance in one statement", line);
                    }

                    var opening = ParseBalance(value, tag, line);
                    _header = new StatementHeader
                    {
                        Format = "MT940",
                        StatementId = _statementId!,
                        SequenceNumber = _sequence,
                        Account = _account,
                        Iban = ParseIban(_account),
                        Currency = opening.Currency,
                        Opening = opening,
                    };
                    _running = opening.SignedAmount;
                    output.Add(new StatementStarted(_header));
                    break;
                case "61":
                    RequireOpened(tag, line);
                    if (_closing is not null)
                    {
                        throw new StatementFormatException("Statement line after the closing balance", line);
                    }

                    FlushEntry(output);
                    if (_entryCount >= options.MaxEntriesPerStatement)
                    {
                        throw new StatementFormatException($"More than {options.MaxEntriesPerStatement} entries in one statement", line);
                    }

                    _pendingEntry = ParseStatementLine(value, line);
                    _pendingHasNarrative = false;
                    break;
                case "86":
                    RequireStatement(tag, line);
                    if (_pendingEntry is not null && !_pendingHasNarrative)
                    {
                        _pendingEntry = AttachNarrative(_pendingEntry, value);
                        _pendingHasNarrative = true;
                    }

                    // An :86: after :62F: is statement-level information; it carries nothing the matcher uses.
                    break;
                case "62F" or "62M":
                    RequireOpened(tag, line);
                    FlushEntry(output);
                    _closing = ParseBalance(value, tag, line);
                    CheckCurrency(_closing, line);
                    break;
                case "64":
                    RequireOpened(tag, line);
                    FlushEntry(output);
                    _closingAvailable = ParseBalance(value, tag, line);
                    CheckCurrency(_closingAvailable, line);
                    break;
                default:
                    // :13D:, :34F:, :65:, :90C: ... are legal in MT940/MT942 and irrelevant here.
                    RequireStatement(tag, line);
                    break;
            }
        }

        public void EndMessage(List<StatementPart> output, long line)
        {
            if (_statementId is null)
            {
                return;
            }

            FlushEntry(output);
            if (_header is null)
            {
                throw new StatementFormatException($"Statement '{_statementId}' has no opening balance", _messageStartLine);
            }

            if (_closing is null)
            {
                throw new StatementFormatException($"Statement '{_statementId}' has no closing balance", line);
            }

            output.Add(new StatementEnded(_header, _closing, _closingAvailable, _entryCount, _running));
            _statementId = null;
            _account = null;
            _sequence = null;
            _header = null;
            _closing = null;
            _closingAvailable = null;
            _entryCount = 0;
            _running = 0;
        }

        private void FlushEntry(List<StatementPart> output)
        {
            if (_pendingEntry is null)
            {
                return;
            }

            _running += _pendingEntry.SignedAmount;
            output.Add(new EntryRead(_header!, _pendingEntry, _entryCount));
            _entryCount++;
            _pendingEntry = null;
        }

        private void RequireStatement(string tag, long line)
        {
            if (_statementId is null)
            {
                throw new StatementFormatException($"Field :{tag}: before :20:", line);
            }
        }

        private void RequireOpened(string tag, long line)
        {
            RequireStatement(tag, line);
            if (_header is null)
            {
                throw new StatementFormatException($"Field :{tag}: before the opening balance", line);
            }
        }

        private void CheckCurrency(Balance balance, long line)
        {
            if (!string.Equals(balance.Currency, _header!.Currency, StringComparison.Ordinal))
            {
                throw new StatementFormatException(
                    $"Balance currency {balance.Currency} differs from opening currency {_header.Currency}", line);
            }
        }

        private static string RequireText(string value, string tag, long line, int max)
        {
            var text = value.Trim();
            return text.Length == 0 || text.Length > max || text.Contains('\n', StringComparison.Ordinal)
                ? throw new StatementFormatException($"Field :{tag}: must be 1-{max} characters on one line", line)
                : text;
        }

        private static string? ParseIban(string account)
        {
            // "TR330006100519786457841326", "BANKTRISXXX/TR33...", "00061/0519786457841326"
            var slash = account.LastIndexOf('/');
            return Iban.Normalize(slash >= 0 ? account[(slash + 1)..] : account);
        }

        private static Balance ParseBalance(string value, string tag, long line)
        {
            // 1!a6!n3!a15d: C261112TRY12345,67
            var text = value.Trim();
            if (text.Length < 12 || text.Contains('\n', StringComparison.Ordinal))
            {
                throw new StatementFormatException($"Field :{tag}: is not a balance", line);
            }

            var mark = text[0] switch
            {
                'C' => CreditDebit.Credit,
                'D' => CreditDebit.Debit,
                _ => throw new StatementFormatException($"Field :{tag}: has an invalid debit/credit mark", line),
            };

            if (!SwiftText.TryParseDate(text.AsSpan(1, 6), out var date))
            {
                throw new StatementFormatException($"Field :{tag}: has an invalid date", line);
            }

            var currency = text.Substring(7, 3);
            if (!IsCurrency(currency))
            {
                throw new StatementFormatException($"Field :{tag}: has an invalid currency", line);
            }

            return SwiftText.TryParseAmount(text.AsSpan(10), out var amount)
                ? new Balance(mark, date, currency, amount)
                : throw new StatementFormatException($"Field :{tag}: has an invalid amount", line);
        }

        private static bool IsCurrency(string code) =>
            code.Length == 3 && char.IsAsciiLetterUpper(code[0]) && char.IsAsciiLetterUpper(code[1]) &&
            char.IsAsciiLetterUpper(code[2]);

        /// <summary>
        /// :61: is 6!n[4!n]2a[1!a]15d1!a3!c16x[//16x] on the first line and an optional [34x] supplementary line.
        /// </summary>
        internal static StatementEntry ParseStatementLine(string value, long line)
        {
            var newline = value.IndexOf('\n', StringComparison.Ordinal);
            var first = (newline < 0 ? value : value[..newline]).TrimEnd();
            var supplementary = newline < 0 ? null : value[(newline + 1)..].Replace("\n", string.Empty, StringComparison.Ordinal).Trim();
            var s = first.AsSpan();

            if (s.Length < 6 || !SwiftText.TryParseDate(s[..6], out var valueDate))
            {
                throw new StatementFormatException("Statement line :61: has an invalid value date", line);
            }

            var i = 6;
            DateOnly? bookingDate = null;
            if (s.Length >= 10 && SwiftText.IsDigits(s.Slice(6, 4)))
            {
                if (!SwiftText.TryParseEntryDate(s.Slice(6, 4), valueDate, out var entryDate))
                {
                    throw new StatementFormatException("Statement line :61: has an invalid entry date", line);
                }

                bookingDate = entryDate;
                i = 10;
            }

            var reversal = false;
            if (i < s.Length && s[i] == 'R')
            {
                reversal = true;
                i++;
            }

            if (i >= s.Length || (s[i] != 'C' && s[i] != 'D'))
            {
                throw new StatementFormatException("Statement line :61: has an invalid debit/credit mark", line);
            }

            // RC (reversal of credit) moves money out, RD (reversal of debit) moves it back in.
            var booked = s[i] == 'C' ? CreditDebit.Credit : CreditDebit.Debit;
            var mark = reversal ? (booked == CreditDebit.Credit ? CreditDebit.Debit : CreditDebit.Credit) : booked;
            i++;

            string? fundsCode = null;
            if (i < s.Length && char.IsAsciiLetter(s[i]))
            {
                fundsCode = s[i].ToString();
                i++;
            }

            var amountStart = i;
            while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == ','))
            {
                i++;
            }

            if (!SwiftText.TryParseAmount(s[amountStart..i], out var amount))
            {
                throw new StatementFormatException("Statement line :61: has an invalid amount", line);
            }

            if (i + 4 > s.Length || (s[i] != 'N' && s[i] != 'F' && s[i] != 'S'))
            {
                throw new StatementFormatException("Statement line :61: has an invalid transaction type", line);
            }

            var transactionType = s.Slice(i, 4).ToString();
            i += 4;

            var references = s[i..];
            var separator = references.IndexOf("//", StringComparison.Ordinal);
            var customer = (separator < 0 ? references : references[..separator]).Trim().ToString();
            var bank = separator < 0 ? null : references[(separator + 2)..].Trim().ToString();
            if (customer.Length == 0 || customer.Length > 35 || bank?.Length > 35)
            {
                throw new StatementFormatException("Statement line :61: has an invalid reference", line);
            }

            return new StatementEntry
            {
                ValueDate = valueDate,
                BookingDate = bookingDate,
                Mark = mark,
                IsReversal = reversal,
                Amount = amount,
                FundsCode = fundsCode,
                TransactionType = transactionType,
                CustomerReference = string.Equals(customer, "NONREF", StringComparison.Ordinal) ? null : customer,
                BankReference = string.IsNullOrEmpty(bank) ? null : bank,
                SupplementaryDetails = string.IsNullOrEmpty(supplementary) ? null : supplementary,
            };
        }

        private static StatementEntry AttachNarrative(StatementEntry entry, string value)
        {
            // Lines are hard-wrapped at 65 characters, so they are joined without a separator.
            var raw = value.Replace("\n", string.Empty, StringComparison.Ordinal);
            var parsed = Narrative86.Parse(raw);
            return entry with
            {
                Narrative = raw.Length == 0 ? null : raw,
                Remittance = parsed.Remittance,
                CounterpartyName = parsed.CounterpartyName,
                CounterpartyIban = parsed.CounterpartyIban,
                EndToEndId = parsed.EndToEndId,
            };
        }
    }
}
