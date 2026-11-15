using System.Globalization;
using BankRecon.Core.Matching;
using BankRecon.Core.Statements;

namespace BankRecon.Synthetic;

/// <summary>How the bank formats the free text of a movement.</summary>
public enum NarrativeStyle
{
    /// <summary>Unstructured upper-case Turkish text, as Turkish banks commonly put in :86: ("EFT GELEN ...").</summary>
    TurkishFreeText,

    /// <summary>German GVC ?NN subfields with SEPA EREF+/SVWZ+ markers.</summary>
    QuestionMarkSubfields,

    /// <summary>SEPA keyword style: /NAME/.../REMI/...</summary>
    SlashKeywords,

    /// <summary>camt.053 structured fields (Ustrd, Dbtr name and IBAN, EndToEndId); batch bookings are itemised.</summary>
    Camt,
}

public sealed record ScenarioOptions
{
    public int Seed { get; init; } = 1;
    public int LedgerItems { get; init; } = 1000;
    public DateOnly Start { get; init; } = new(2026, 11, 2);
    public int Days { get; init; } = 20;
    public string Currency { get; init; } = "TRY";
    public NarrativeStyle Style { get; init; } = NarrativeStyle.TurkishFreeText;

    /// <summary>Keep Turkish letters in narratives (the writer may still transliterate them for MT940).</summary>
    public bool TurkishLetters { get; init; } = true;
}

/// <summary>What a set of bank lines and ledger items really is, so precision and recall can be measured.</summary>
public sealed record TruthGroup(string Kind, IReadOnlyList<string> BankLineIds, IReadOnlyList<string> LedgerItemIds);

public sealed record Scenario(
    Statement Statement,
    IReadOnlyList<LedgerItem> Ledger,
    IReadOnlyList<TruthGroup> Truth,
    ScenarioOptions Options)
{
    /// <summary>Entry ids used throughout: the zero-based entry index, "L000042".</summary>
    public static string EntryId(int index) => string.Create(CultureInfo.InvariantCulture, $"L{index:D6}");

    public IReadOnlyList<BankLine> BankLines() =>
        Statement.Entries.SelectMany((e, i) => BankLineFactory.From(EntryId(i), e, Statement.Header.Currency)).ToList();
}

/// <summary>
/// Generates a receivables ledger and the bank statement that pays it, with the damage real statements show:
/// mangled or truncated references, missing references, deducted charges, bulk payments, instalments, duplicate
/// payments, unexpected receipts, unpaid invoices and colliding round amounts. Seeded and deterministic.
/// </summary>
public sealed class ScenarioGenerator
{
    // Bank code 99901 is used for every synthetic IBAN; the checksums are valid, the accounts are invented.
    private const int SyntheticBankCode = 99901;

    private static readonly string[] NameRoots =
    [
        "Anadolu", "Ege", "Marmara", "Karadeniz", "Toros", "Kapadokya", "Boğaziçi", "Çukurova", "Yıldız", "Şimşek",
        "Güneş", "Doğan", "Özkan", "Işık", "Çınar", "Kuzey", "Akdeniz", "Fırat", "Dicle", "Erciyes", "Uludağ",
        "Pamukkale", "Gökçe", "Ilgaz", "Defne", "Lale", "Ilıca", "Söğüt", "Çağlayan", "Kızılırmak",
    ];

    private static readonly string[] Sectors =
    [
        "Tekstil", "Gıda", "Lojistik", "İnşaat", "Otomotiv", "Kimya", "Yazılım", "Mobilya", "Tarım", "Enerji",
        "Medikal", "Ambalaj", "Turizm", "Elektrik", "Makine", "Kağıt",
    ];

    private static readonly string[] LegalForms = ["A.Ş.", "Ltd. Şti.", "San. ve Tic. A.Ş.", "Tic. Ltd. Şti."];

    private static readonly decimal[] RoundAmounts = [500m, 750m, 1000m, 1250m, 1500m, 2000m, 2500m, 3000m, 5000m, 7500m, 10000m];

    private readonly ScenarioOptions _options;
    private readonly Random _random;
    private readonly List<Customer> _customers = [];
    private readonly List<LedgerItem> _ledger = [];
    private readonly List<Planned> _planned = [];
    private readonly List<(string Kind, List<Planned> Entries, List<int> DetailIndexes, List<string> Ledger)> _truth = [];
    private int _invoiceSeq;
    private int _bankRefSeq;

    public ScenarioGenerator(ScenarioOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _random = new Random(options.Seed);
    }

    public static Scenario Generate(ScenarioOptions options) => new ScenarioGenerator(options).Build();

    private Scenario Build()
    {
        var customerCount = Math.Max(5, _options.LedgerItems / 4);
        for (var i = 0; i < customerCount; i++)
        {
            _customers.Add(NewCustomer(i));
        }

        while (_ledger.Count < _options.LedgerItems)
        {
            var roll = _random.NextDouble();
            switch (roll)
            {
                case < 0.38: Clean(); break;
                case < 0.52: Mangled(); break;
                case < 0.60: NoReference(); break;
                case < 0.67: Charges(); break;
                case < 0.75: Bulk(); break;
                case < 0.80: Instalments(); break;
                default: Unpaid(); break;
            }
        }

        var extras = Math.Max(1, _options.LedgerItems * 6 / 100);
        for (var i = 0; i < extras; i++)
        {
            switch (_random.NextDouble())
            {
                case < 0.45: Unexpected(); break;
                case < 0.65: Duplicate(); break;
                default: BankDebit(); break;
            }
        }

        // Banks list movements by booking date; ties keep the order in which they happened.
        var ordered = _planned.Select((p, seq) => (p, seq)).OrderBy(x => x.p.Entry.ValueDate).ThenBy(x => x.seq)
            .Select(x => x.p).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Index = i;
        }

        var entries = ordered.Select(p => p.Entry).ToList();
        var account = Iban.CreateTurkish(SyntheticBankCode, 100_000_000 + _options.Seed);
        var openingDate = _options.Start.AddDays(-1);
        var opening = new Balance(CreditDebit.Credit, openingDate, _options.Currency, Math.Round((decimal)_random.Next(10_000, 900_000), 2));
        var closingSigned = opening.SignedAmount + entries.Sum(e => e.SignedAmount);
        var lastDate = entries.Count == 0 ? _options.Start : entries.Max(e => e.ValueDate);
        var statement = new Statement
        {
            Header = new StatementHeader
            {
                Format = "synthetic",
                StatementId = string.Create(CultureInfo.InvariantCulture, $"STM{_options.Seed:D4}{_options.Start:MMdd}"),
                SequenceNumber = "1/1",
                Account = account,
                Iban = account,
                Currency = _options.Currency,
                Opening = opening,
            },
            Entries = entries,
            Closing = Balance.FromSigned(closingSigned, lastDate, _options.Currency),
            ClosingAvailable = Balance.FromSigned(closingSigned, lastDate, _options.Currency),
        };

        var truth = _truth.Select(t => new TruthGroup(
            t.Kind,
            t.DetailIndexes.Count > 0
                ? t.DetailIndexes.Select(k => $"{Scenario.EntryId(t.Entries[0].Index)}#{k}").ToList()
                : t.Entries.Select(e => Scenario.EntryId(e.Index)).ToList(),
            t.Ledger)).ToList();
        return new Scenario(statement, _ledger, truth, _options);
    }

    // ---- ledger-side cases -----------------------------------------------------------------------------------

    private void Clean()
    {
        var customer = PickCustomer();
        var item = NewInvoice(customer);
        var entry = Credit(item.Amount, PayDate(item, 0, 2), customer, Remittance(item.Reference!, customer), item.Reference);
        Truth("clean", [entry], [item.Id]);
    }

    private void Mangled()
    {
        var customer = PickCustomer();
        var item = NewInvoice(customer);
        var reference = Mangle(item.Reference!);
        var entry = Credit(item.Amount, PayDate(item, 0, 4), customer, Remittance(reference, customer), reference);
        Truth("mangled-reference", [entry], [item.Id]);
    }

    private void NoReference()
    {
        var customer = PickCustomer();
        var item = NewInvoice(customer);
        var entry = Credit(item.Amount, PayDate(item, 0, 3), customer, Pick(["ODEME", "CARI HESAP ODEMESI", "MAL BEDELI", "HIZMET BEDELI"]), null);
        Truth("no-reference", [entry], [item.Id]);
    }

    private void Charges()
    {
        var customer = PickCustomer();
        var item = NewInvoice(customer, minimum: 2500m);
        var fee = Math.Round((decimal)(2.5 + (_random.NextDouble() * 12.5)), 2);
        var reference = _random.NextDouble() < 0.5 ? item.Reference! : Mangle(item.Reference!);
        var entry = Credit(item.Amount - fee, PayDate(item, 1, 4), customer, Remittance(reference, customer), reference);
        Truth("charges-deducted", [entry], [item.Id]);
    }

    private void Bulk()
    {
        var customer = PickCustomer();
        var count = _random.Next(2, 5);
        var items = Enumerable.Range(0, count).Select(_ => NewInvoice(customer, roundAllowed: false)).ToList();
        var date = PayDate(items[^1], 1, 4);
        var total = items.Sum(i => i.Amount);

        if (_options.Style == NarrativeStyle.Camt && _random.NextDouble() < 0.5)
        {
            // Itemised batch booking: one entry, one TxDtls per invoice.
            var details = items.Select(i => new TransactionDetail
            {
                Amount = i.Amount,
                Remittance = i.Reference,
                CounterpartyName = BankName(customer),
                CounterpartyIban = customer.Iban,
                EndToEndId = _random.NextDouble() < 0.3 ? i.Reference : null,
            }).ToList();
            var batch = new Planned(new StatementEntry
            {
                ValueDate = date,
                BookingDate = date,
                Mark = CreditDebit.Credit,
                Amount = total,
                TransactionType = "PMNT/RCDT/BOOK",
                BankReference = NextBankReference(date),
                Narrative = "TOPLU ODEME",
                Details = details,
            });
            _planned.Add(batch);
            for (var k = 0; k < count; k++)
            {
                _truth.Add(("bulk-itemised", [batch], [k], [items[k].Id]));
            }

            return;
        }

        var text = _random.NextDouble() < 0.6
            ? "FATURALAR " + string.Join(' ', items.Select(i => i.Reference))
            : "TOPLU ODEME " + Pick(["KASIM", "CARI HESAP", "FATURA ODEMELERI"]);
        if (text.Length > 120)
        {
            text = text[..120].TrimEnd();
        }

        var entry = Credit(total, date, customer, text, null);
        Truth("bulk", [entry], items.Select(i => i.Id).ToList());
    }

    private void Instalments()
    {
        var customer = PickCustomer();
        var item = NewInvoice(customer, minimum: 3000m, roundAllowed: false);
        var parts = _random.Next(2, 4);
        var remaining = item.Amount;
        var entries = new List<Planned>();
        for (var p = 0; p < parts; p++)
        {
            var amount = p == parts - 1 ? remaining : Math.Round(item.Amount / parts, 2);
            remaining -= amount;
            var text = _random.NextDouble() < 0.6
                ? Remittance(item.Reference!, customer) + string.Create(CultureInfo.InvariantCulture, $" TAKSIT {p + 1}")
                : string.Create(CultureInfo.InvariantCulture, $"TAKSIT {p + 1}/{parts}");
            entries.Add(Credit(amount, PayDate(item, p * 2, (p * 2) + 2), customer, text, null));
        }

        Truth("instalments", entries, [item.Id]);
    }

    private void Unpaid()
    {
        var item = NewInvoice(PickCustomer());
        Truth("unpaid", [], [item.Id]);
    }

    // ---- bank-only cases -------------------------------------------------------------------------------------

    private void Unexpected()
    {
        var customer = PickCustomer();
        var amount = _random.NextDouble() < 0.5 ? Pick(RoundAmounts) : RandomAmount(100m, 20_000m);
        var date = _options.Start.AddDays(_random.Next(0, _options.Days));
        var entry = Credit(amount, date, customer, Pick(["AVANS", "ODEME", "DEPOZITO", "IADE"]), null);
        Truth("unexpected-receipt", [entry], []);
    }

    private void Duplicate()
    {
        // The customer paid an invoice that is already paid on this statement.
        var original = _truth.Where(t => t.Kind is "clean" or "mangled-reference" && t.Entries.Count == 1).ToList();
        if (original.Count == 0)
        {
            Unexpected();
            return;
        }

        var source = original[_random.Next(original.Count)].Entries[0].Entry;
        var date = source.ValueDate.AddDays(_random.Next(1, 4));
        var entry = new Planned(source with { ValueDate = date, BookingDate = date, BankReference = NextBankReference(date) });
        _planned.Add(entry);
        Truth("duplicate-payment", [entry], []);
    }

    private void BankDebit()
    {
        var date = _options.Start.AddDays(_random.Next(0, _options.Days));
        var (text, amount) = Pick<(string, decimal)>(
        [
            ("HESAP ISLETIM UCRETI", 45m),
            ("EFT MASRAFI", 12.5m),
            ("POS KOMISYONU", RandomAmount(20m, 400m)),
            ("KREDI KARTI ODEMESI", RandomAmount(1_000m, 30_000m)),
        ]);
        var entry = new Planned(new StatementEntry
        {
            ValueDate = date,
            BookingDate = date,
            Mark = CreditDebit.Debit,
            Amount = amount,
            TransactionType = _options.Style == NarrativeStyle.Camt ? "PMNT/CCRD/CHRG" : "NCHG",
            BankReference = NextBankReference(date),
            Narrative = _options.Style == NarrativeStyle.Camt ? text : Narrative86(text, null, null, null),
        });
        _planned.Add(entry);
        Truth("bank-debit", [entry], []);
    }

    // ---- building blocks -------------------------------------------------------------------------------------

    private Customer NewCustomer(int index)
    {
        var name = $"{Pick(NameRoots)} {Pick(Sectors)} {Pick(LegalForms)}";
        return new Customer(name, Iban.CreateTurkish(SyntheticBankCode, 5_000_000_000 + (_options.Seed * 100_000L) + index));
    }

    private Customer PickCustomer() => _customers[_random.Next(_customers.Count)];

    private LedgerItem NewInvoice(Customer customer, decimal minimum = 100m, bool roundAllowed = true)
    {
        _invoiceSeq++;
        var number = (_options.Seed * 1_000_000) + _invoiceSeq;
        var reference = _random.NextDouble() < 0.7
            ? string.Create(CultureInfo.InvariantCulture, $"INV-2026-{number % 1_000_000:D6}")
            : string.Create(CultureInfo.InvariantCulture, $"SO/2026/TR/{number % 10_000_000:D7}/01");
        var amount = roundAllowed && _random.NextDouble() < 0.35
            ? Pick(RoundAmounts.Where(a => a >= minimum).DefaultIfEmpty(minimum).ToArray())
            : RandomAmount(minimum, 50_000m);
        var item = new LedgerItem
        {
            Id = string.Create(CultureInfo.InvariantCulture, $"LI-{_options.Seed:D3}-{_invoiceSeq:D6}"),
            Amount = amount,
            Currency = _options.Currency,
            ExpectedDate = _options.Start.AddDays(_random.Next(0, Math.Max(1, _options.Days - 4))),
            Reference = reference,
            CounterpartyName = customer.Name,
        };
        _ledger.Add(item);
        return item;
    }

    private Planned Credit(decimal amount, DateOnly date, Customer customer, string text, string? reference)
    {
        var booking = _random.NextDouble() < 0.1 ? date.AddDays(1) : date;
        var bankRef = NextBankReference(date);
        StatementEntry entry;
        if (_options.Style == NarrativeStyle.Camt)
        {
            entry = new StatementEntry
            {
                ValueDate = date,
                BookingDate = booking,
                Mark = CreditDebit.Credit,
                Amount = amount,
                TransactionType = "PMNT/RCDT/ESCT",
                BankReference = bankRef,
                Remittance = text,
                CounterpartyName = BankName(customer),
                CounterpartyIban = customer.Iban,
                EndToEndId = reference is not null && _random.NextDouble() < 0.3 ? reference : null,
            };
        }
        else
        {
            // Some payers' references reach :61: as the customer reference, cut at 16 characters.
            var customerRef = reference is not null && _random.NextDouble() < 0.4
                ? CleanForReference(reference)
                : null;
            entry = new StatementEntry
            {
                ValueDate = date,
                BookingDate = booking,
                Mark = CreditDebit.Credit,
                Amount = amount,
                TransactionType = "NTRF",
                CustomerReference = customerRef,
                BankReference = bankRef,
                Narrative = Narrative86(text, BankName(customer), customer.Iban, reference),
            };
        }

        var planned = new Planned(entry);
        _planned.Add(planned);
        return planned;
    }

    private static string? CleanForReference(string reference)
    {
        var cleaned = new string(reference.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '/').ToArray());
        cleaned = cleaned.Replace("//", "/", StringComparison.Ordinal);
        return cleaned.Length == 0 ? null : cleaned.Length > 16 ? cleaned[..16] : cleaned;
    }

    private string Narrative86(string text, string? name, string? iban, string? endToEnd)
    {
        string narrative;
        switch (_options.Style)
        {
            case NarrativeStyle.QuestionMarkSubfields:
                var purpose = (endToEnd is null ? string.Empty : "EREF+" + endToEnd) + "SVWZ+" + text;
                var subfields = new List<string> { "166", "?00GUTSCHRIFT" };
                for (var i = 0; i * 27 < purpose.Length && i < 10; i++)
                {
                    subfields.Add(string.Create(CultureInfo.InvariantCulture, $"?2{i}{purpose.AsSpan(i * 27, Math.Min(27, purpose.Length - (i * 27)))}"));
                }

                if (iban is not null)
                {
                    subfields.Add("?31" + iban);
                }

                if (name is not null)
                {
                    subfields.Add("?32" + (name.Length > 27 ? name[..27] : name));
                }

                narrative = string.Concat(subfields);
                break;
            case NarrativeStyle.SlashKeywords:
                narrative = "/TRTP/SEPA OVERBOEKING" +
                            (iban is null ? string.Empty : "/IBAN/" + iban) +
                            (name is null ? string.Empty : "/NAME/" + name) +
                            "/REMI/" + text +
                            (endToEnd is null ? string.Empty : "/EREF/" + endToEnd);
                narrative = narrative.Replace("//", "/", StringComparison.Ordinal);
                break;
            default:
                narrative = (name is null ? string.Empty : Pick(["EFT GELEN ", "HAVALE GELEN ", "FAST GELEN "]) + name + " ") + text;
                break;
        }

        // The MT940 :86: field holds at most 6 x 65 characters; keep a margin for line-break adjustments.
        return narrative.Length > 380 ? narrative[..380].TrimEnd() : narrative;
    }

    private string Remittance(string reference, Customer customer) => _random.Next(4) switch
    {
        0 => reference,
        1 => "FATURA " + reference,
        2 => reference + " ODEMESI",
        _ => $"{reference} {FirstWord(customer.Name)}",
    };

    /// <summary>Bank-side rendering of a name: upper case, sometimes transliterated or cut at 35 characters.</summary>
    private string BankName(Customer customer)
    {
        var name = TurkishUpper(customer.Name);
        if (!_options.TurkishLetters || _random.NextDouble() < 0.4)
        {
            name = Core.Statements.Mt940.SwiftText.ToXCharset(name);
        }

        name = name.Replace(".", string.Empty, StringComparison.Ordinal);
        return name.Length > 35 ? name[..35].TrimEnd() : name;
    }

    /// <summary>The damage references take on their way through payers' banking apps.</summary>
    private string Mangle(string reference)
    {
        var chars = reference.ToCharArray();
        switch (_random.Next(5))
        {
            case 0:
                // One digit mistyped.
                var digits = Enumerable.Range(0, chars.Length).Where(i => char.IsAsciiDigit(chars[i])).ToArray();
                var at = digits[_random.Next(digits.Length)];
                chars[at] = (char)('0' + ((chars[at] - '0' + 1 + _random.Next(8)) % 10));
                return new string(chars);
            case 1:
                // Two neighbouring characters swapped.
                var i = _random.Next(chars.Length - 1);
                (chars[i], chars[i + 1]) = (chars[i + 1], chars[i]);
                return new string(chars);
            case 2:
                return new string(chars.Where(char.IsAsciiLetterOrDigit).ToArray());
            case 3:
                return reference.ToLowerInvariant().Replace('-', ' ').Replace('/', ' ');
            default:
                // The leading prefix dropped: "2026-000123".
                var dash = reference.IndexOfAny(['-', '/']);
                return dash > 0 ? reference[(dash + 1)..] : reference;
        }
    }

    private DateOnly PayDate(LedgerItem item, int minDays, int maxDays) =>
        item.ExpectedDate.AddDays(_random.Next(minDays, maxDays + 1));

    private decimal RandomAmount(decimal min, decimal max) =>
        Math.Round(min + ((decimal)_random.NextDouble() * (max - min)), 2);

    private string NextBankReference(DateOnly date)
    {
        _bankRefSeq++;
        return string.Create(CultureInfo.InvariantCulture, $"FT{date:yyMMdd}{_options.Seed % 100:D2}{_bankRefSeq:D6}");
    }

    private void Truth(string kind, List<Planned> entries, List<string> ledger) => _truth.Add((kind, entries, [], ledger));

    private T Pick<T>(IReadOnlyList<T> values) => values[_random.Next(values.Count)];

    // Turkish upper-casing without a culture lookup (i -> İ, ı -> I): the API image runs with invariant globalization,
    // where CultureInfo("tr-TR") is not available.
    private static string TurkishUpper(string value) =>
        string.Concat(value.Select(c => c switch { 'i' => 'İ', 'ı' => 'I', _ => char.ToUpperInvariant(c) }));

    private static string FirstWord(string name) => name.Split(' ')[0];

    private sealed record Customer(string Name, string Iban);

    private sealed class Planned(StatementEntry entry)
    {
        public StatementEntry Entry { get; set; } = entry;
        public int Index { get; set; }
    }
}
