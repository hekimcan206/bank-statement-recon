using System.Globalization;
using BankRecon.Core.Statements;
using BankRecon.Core.Statements.Mt940;
using BankRecon.Core.Text;
using FsCheck.Xunit;

namespace BankRecon.UnitTests;

public sealed class TextTests
{
    [Theory]
    [InlineData("MARTHA", "MARHTA", 0.961)]
    [InlineData("DWAYNE", "DUANE", 0.840)]
    [InlineData("DIXON", "DICKSONX", 0.813)]
    [InlineData("ABC", "XYZ", 0.0)]
    public void Jaro_winkler_matches_the_published_reference_values(string a, string b, double expected) =>
        Similarity.JaroWinkler(a, b).Should().BeApproximately(expected, 0.001);

    [Property(MaxTest = 500)]
    public bool Jaro_winkler_is_symmetric_bounded_and_one_for_identical_strings(string? a, string? b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        var ab = Similarity.JaroWinkler(a, b);
        return Math.Abs(ab - Similarity.JaroWinkler(b, a)) < 1e-12 && ab is >= 0 and <= 1 + 1e-12 &&
               Math.Abs(Similarity.JaroWinkler(a, a) - 1) < 1e-12;
    }

    [Theory]
    [InlineData("İSTANBUL")]
    [InlineData("ISTANBUL")]
    [InlineData("istanbul")]
    [InlineData("ıstanbul")]
    [InlineData("i̇stanbul")]
    public void Every_spelling_of_the_turkish_i_folds_to_the_same_key(string spelling) =>
        TextNormalizer.Key(spelling).Should().Be("istanbul");

    [Fact]
    public void Folding_does_not_depend_on_the_current_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var turkish = TextNormalizer.Fold("ŞİMŞEK GIDA LTD. ŞTİ. INV-2026/001");
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            TextNormalizer.Fold("ŞİMŞEK GIDA LTD. ŞTİ. INV-2026/001").Should().Be(turkish).And.Be("simsek gida ltd sti inv 2026 001");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Token_set_ignores_order_and_tolerates_one_misspelt_word()
    {
        string[] a = ["anadolu", "tekstil"];
        string[] b = ["tekstill", "anadolu"];
        Similarity.TokenSet(a, b).Should().Be(1.0);
        Similarity.TokenSet(a, ["ege", "lojistik"]).Should().Be(0);
    }

    [Fact]
    public void Swift_x_charset_transliterates_turkish_letters_instead_of_dropping_them() =>
        SwiftText.ToXCharset("ÇAĞLAYAN İNŞAAT ŞTİ. ödeme #12").Should().Be("CAGLAYAN INSAAT STI. odeme  12");

    [Theory]
    [InlineData("TR919990100000001000000001", true)]
    [InlineData("DE96999900000001234567", true)]
    [InlineData("TR919990100000001000000002", false)]
    [InlineData("TR91999010000000100000000", false)]
    [InlineData("tr91 9990 1000 0000 1000 0000 01", false)]
    public void Iban_validation_checks_length_and_mod97(string iban, bool valid) => Iban.IsValid(iban).Should().Be(valid);

    [Fact]
    public void Iban_normalisation_accepts_spaces_and_lower_case() =>
        Iban.Normalize("tr91 9990 1000 0000 1000 0000 01").Should().Be("TR919990100000001000000001");

    [Property(MaxTest = 300)]
    public bool Synthetic_turkish_ibans_are_always_valid(ushort bank, uint account) =>
        Iban.IsValid(Iban.CreateTurkish(bank % 100_000, account));

    [Theory]
    [InlineData("1234,56", 1234.56)]
    [InlineData("1234,", 1234)]
    [InlineData("0,5", 0.5)]
    public void Swift_amounts_parse_with_a_decimal_comma(string text, double expected)
    {
        SwiftText.TryParseAmount(text, out var amount).Should().BeTrue();
        amount.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("1234.56")]
    [InlineData(",5")]
    [InlineData("1,2,3")]
    [InlineData("12,3456")]
    [InlineData("1234567890123456,")]
    public void Malformed_swift_amounts_are_rejected(string text) => SwiftText.TryParseAmount(text, out _).Should().BeFalse();
}
