using System.Globalization;
using System.Text;
using BankRecon.Core.Statements;
using BankRecon.Core.Statements.Mt940;

namespace BankRecon.UnitTests;

/// <summary>
/// Random statements restricted to what each format can carry, for round-trip properties. Anything the format
/// cannot represent (a 17-character MT940 reference, a camt funds code) is never generated, because the writers
/// reject it by design.
/// </summary>
internal sealed class RandomStatements(int seed)
{
    private const string Alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const string XText = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789/-?:().,'+";
    private const string XmlText = "ABCÇĞİÖŞÜabcçğıöşü0123456789-/.,&<>'\" ";

    private readonly Random _r = new(seed);

    public Statement Mt940()
    {
        var currency = Currency();
        var account = _r.Next(2) == 0 ? Iban.CreateTurkish(99901, _r.NextInt64(0, 9_999_999_999)) : $"{Word(Alnum, 5)}/{Word("0123456789", 10)}";
        var slash = account.LastIndexOf('/');
        var opening = Balance(currency);
        var entries = Enumerable.Range(0, _r.Next(0, 12)).Select(_ => Mt940Entry()).ToList();
        return new Statement
        {
            Header = new StatementHeader
            {
                Format = "MT940",
                StatementId = Word(Alnum + "-", _r.Next(1, 17)),
                SequenceNumber = _r.Next(3) == 0 ? null : $"{_r.Next(1, 999)}/{_r.Next(1, 9)}",
                Account = account,
                Iban = Iban.Normalize(slash >= 0 ? account[(slash + 1)..] : account),
                Currency = currency,
                Opening = opening,
            },
            Entries = entries,
            Closing = Balance(currency),
            ClosingAvailable = _r.Next(2) == 0 ? null : Balance(currency),
        };
    }

    public Statement Camt(int version)
    {
        var currency = Currency();
        var account = _r.Next(2) == 0 ? Iban.CreateTurkish(99901, _r.NextInt64(0, 9_999_999_999)) : Word(Alnum + "-", _r.Next(5, 30));
        var closing = Balance(currency);
        return new Statement
        {
            Header = new StatementHeader
            {
                Format = "camt.053.001." + version.ToString("D2", CultureInfo.InvariantCulture),
                StatementId = Word(Alnum + "-", _r.Next(1, 30)),
                SequenceNumber = _r.Next(2) == 0 ? null : _r.Next(1, 100_000).ToString(CultureInfo.InvariantCulture),
                Account = account,
                Iban = Iban.Normalize(account),
                Currency = currency,
                Opening = Balance(currency),
                Closing = closing,
                CreatedAt = _r.Next(2) == 0
                    ? new DateTimeOffset(2026, 11, 13, 6, 0, 0, TimeSpan.FromHours(3))
                    : new DateTimeOffset(2026, _r.Next(1, 13), _r.Next(1, 29), _r.Next(24), _r.Next(60), _r.Next(60), TimeSpan.FromHours(_r.Next(-5, 6))),
            },
            Entries = Enumerable.Range(0, _r.Next(0, 10)).Select(_ => CamtEntry()).ToList(),
            Closing = closing,
            ClosingAvailable = _r.Next(2) == 0 ? null : Balance(currency),
        };
    }

    private StatementEntry Mt940Entry()
    {
        var valueDate = new DateOnly(_r.Next(2000, 2080), _r.Next(1, 13), _r.Next(1, 29));
        var narrative = _r.Next(4) == 0 ? null : Sentence(_r.Next(1, 300));
        var parsed = narrative is null ? null : Narrative86.Parse(narrative);
        return new StatementEntry
        {
            ValueDate = valueDate,
            BookingDate = _r.Next(3) == 0 ? null : valueDate.AddDays(_r.Next(-20, 21)),
            Mark = _r.Next(2) == 0 ? CreditDebit.Credit : CreditDebit.Debit,
            IsReversal = _r.Next(7) == 0,
            Amount = Amount(maxIntegerDigits: 11),
            FundsCode = _r.Next(4) == 0 ? ((char)('A' + _r.Next(26))).ToString() : null,
            TransactionType = Pick("N", "F", "S") + Word(Alnum, 3),
            CustomerReference = _r.Next(4) == 0 ? null : Reference(16),
            BankReference = _r.Next(3) == 0 ? null : Reference(16),
            SupplementaryDetails = _r.Next(3) == 0 ? Word(Alnum, 1) + Word(Alnum + " -.", _r.Next(0, 32)).TrimEnd() : null,
            Narrative = narrative,
            Remittance = parsed?.Remittance,
            CounterpartyName = parsed?.CounterpartyName,
            CounterpartyIban = parsed?.CounterpartyIban,
            EndToEndId = parsed?.EndToEndId,
        };
    }

    private StatementEntry CamtEntry()
    {
        var valueDate = new DateOnly(_r.Next(2000, 2100), _r.Next(1, 13), _r.Next(1, 29));
        var mark = _r.Next(2) == 0 ? CreditDebit.Credit : CreditDebit.Debit;
        var batch = _r.Next(4) == 0;
        var details = batch
            ? Enumerable.Range(0, _r.Next(2, 5)).Select(_ => new TransactionDetail
            {
                Amount = _r.Next(3) == 0 ? null : Amount(9),
                EndToEndId = Maybe(() => Reference(35)),
                Remittance = Maybe(() => XmlWords(140)),
                CounterpartyName = Maybe(() => XmlWords(70)),
                CounterpartyIban = Maybe(() => Iban.CreateTurkish(99901, _r.NextInt64(0, 9_999_999_999))),
            }).ToList()
            : [];
        return new StatementEntry
        {
            ValueDate = valueDate,
            BookingDate = _r.Next(3) == 0 ? null : valueDate.AddDays(_r.Next(-3, 4)),
            Mark = mark,
            IsReversal = _r.Next(7) == 0,
            Amount = Amount(12),
            TransactionType = _r.Next(2) == 0 ? $"{Word(Alnum, 4)}/{Word(Alnum, 4)}/{Word(Alnum, 4)}" : Word(Alnum, _r.Next(1, 8)),
            CustomerReference = Maybe(() => Reference(35)),
            BankReference = Maybe(() => Reference(35)),
            Narrative = Maybe(() => XmlWords(400)),
            Remittance = batch ? null : Maybe(() => XmlWords(140)),
            CounterpartyName = batch ? null : Maybe(() => XmlWords(70)),
            CounterpartyIban = batch ? null : Maybe(() => Iban.CreateTurkish(99901, _r.NextInt64(0, 9_999_999_999))),
            EndToEndId = batch ? null : Maybe(() => Reference(35)),
            Details = details,
        };
    }

    private string? Maybe(Func<string> value) => _r.Next(3) == 0 ? null : value();

    private Balance Balance(string currency) =>
        new(_r.Next(2) == 0 ? CreditDebit.Credit : CreditDebit.Debit, new DateOnly(_r.Next(2000, 2080), _r.Next(1, 13), _r.Next(1, 29)), currency, Amount(11));

    private decimal Amount(int maxIntegerDigits)
    {
        var integer = _r.NextInt64(0, (long)Math.Pow(10, _r.Next(1, maxIntegerDigits + 1)));
        var decimals = _r.Next(3);
        var fraction = decimals == 0 ? 0 : _r.Next((int)Math.Pow(10, decimals));
        return integer + (fraction / (decimal)Math.Pow(10, decimals));
    }

    private string Currency() => Pick("TRY", "EUR", "USD", "GBP");

    /// <summary>Letters, digits, '-' and single inner slashes: never "//", never a slash at either end.</summary>
    private string Reference(int max)
    {
        var length = _r.Next(1, max + 1);
        var builder = new StringBuilder(Word(Alnum, 1));
        while (builder.Length < length)
        {
            var c = builder[^1] != '/' && builder.Length < length - 1 && _r.Next(8) == 0 ? '/' : Alnum[_r.Next(Alnum.Length)];
            builder.Append(c);
        }

        var text = builder.ToString();
        return text == "NONREF" ? "NONREF1"[..Math.Min(max, 7)] : text;
    }

    private string Sentence(int maxLength)
    {
        var builder = new StringBuilder();
        while (builder.Length < maxLength)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(Word(XText, _r.Next(1, 12)));
        }

        return builder.ToString()[..Math.Min(builder.Length, maxLength)].Trim();
    }

    private string XmlWords(int maxLength)
    {
        var text = Word(XmlText, _r.Next(1, maxLength + 1)).Trim();
        return text.Length == 0 ? "X" : text;
    }

    private string Word(string alphabet, int length) =>
        new(Enumerable.Range(0, length).Select(_ => alphabet[_r.Next(alphabet.Length)]).ToArray());

    private string Pick(params string[] values) => values[_r.Next(values.Length)];
}
