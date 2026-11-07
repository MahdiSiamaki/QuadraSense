using System.Text;
using Sqm.Application.Identity;

namespace Sqm.Application.Tests;

/// <summary>
/// The arithmetic of a second step, against the published test vectors, because an authenticator
/// app is someone else's implementation and only the standard connects the two.
/// </summary>
public sealed class TwoStepAlgorithmTests
{
    /// <summary>The secret of RFC 6238's appendix B for SHA-1.</summary>
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    /// <summary>
    /// RFC 6238 appendix B gives eight digits. Six digits are the same value modulo a million,
    /// which is the last six.
    /// </summary>
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Codes_match_the_vectors_of_RFC_6238(long unixSeconds, string expected)
    {
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Assert.Equal(expected, Totp.Code(RfcSecret, step));
    }

    [Fact]
    public void A_code_is_accepted_one_step_either_side_and_no_further()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_010);
        var step = Totp.StepAt(now);

        Assert.Equal(step, Totp.Match(RfcSecret, Totp.Code(RfcSecret, step), now));
        Assert.Equal(step - 1, Totp.Match(RfcSecret, Totp.Code(RfcSecret, step - 1), now));
        Assert.Equal(step + 1, Totp.Match(RfcSecret, Totp.Code(RfcSecret, step + 1), now));
        Assert.Null(Totp.Match(RfcSecret, Totp.Code(RfcSecret, step - 2), now));
        Assert.Null(Totp.Match(RfcSecret, Totp.Code(RfcSecret, step + 2), now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void Something_that_is_not_a_code_matches_nothing(string typed) =>
        Assert.Null(Totp.Match(RfcSecret, typed, DateTimeOffset.UtcNow));

    [Fact]
    public void The_address_for_a_QR_code_names_the_account_and_the_parameters()
    {
        var uri = Totp.Uri("Quadrasense", "a.user@mci", "ABC234");

        Assert.Equal(
            "otpauth://totp/Quadrasense%3Aa.user%40mci?secret=ABC234&issuer=Quadrasense"
            + "&algorithm=SHA1&digits=6&period=30",
            uri);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_matches_the_vectors_of_RFC_4648(string text, string expected) =>
        Assert.Equal(expected, Base32.Encode(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void A_recovery_code_is_sixteen_characters_in_four_groups()
    {
        var code = RecoveryCodes.Generate();

        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){3}$", code);
    }

    [Fact]
    public void Recovery_codes_differ()
    {
        var codes = Enumerable.Range(0, 1000).Select(_ => RecoveryCodes.Generate()).ToHashSet();

        Assert.Equal(1000, codes.Count);
    }

    [Theory]
    [InlineData("7k2m-9qxt-4hnp-w3rd", "7K2M9QXT4HNPW3RD")]
    [InlineData(" 7K2M 9QXT 4HNP W3RD ", "7K2M9QXT4HNPW3RD")]
    [InlineData("OIL0-0000-0000-0000", "0110000000000000")]
    public void A_recovery_code_is_read_however_it_was_typed(string typed, string expected) =>
        Assert.Equal(expected, RecoveryCodes.Normalise(typed));

    [Theory]
    [InlineData("")]
    [InlineData("123456")]
    [InlineData("7K2M-9QXT-4HNP-W3R")]
    [InlineData("7K2M-9QXT-4HNP-W3RU")]
    public void What_cannot_be_a_recovery_code_is_not_taken_for_one(string typed) =>
        Assert.Null(RecoveryCodes.Normalise(typed));

    [Theory]
    [InlineData("09123456789")]
    [InlineData("0912 345 6789")]
    [InlineData("0912-345-6789")]
    [InlineData("+98 912 345 6789")]
    [InlineData("+989123456789")]
    [InlineData("00989123456789")]
    [InlineData("989123456789")]
    [InlineData("9123456789")]
    [InlineData("۰۹۱۲۳۴۵۶۷۸۹")]
    [InlineData("٠٩١٢٣٤٥٦٧٨٩")]
    public void A_number_comes_to_one_form_however_it_was_written(string written) =>
        Assert.Equal("+989123456789", PhoneNumber.Normalise(written, "98"));

    [Fact]
    public void A_number_from_elsewhere_keeps_its_own_country() =>
        Assert.Equal("+4915112345678", PhoneNumber.Normalise("+49 151 1234 5678", "98"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a number")]
    [InlineData("0912x456789")]
    [InlineData("12345")]
    [InlineData("+1234567890123456")]
    public void What_is_not_a_number_has_no_form(string? written) =>
        Assert.Null(PhoneNumber.Normalise(written, "98"));

    [Theory]
    [InlineData("۱۲۳۴۵۶", "123456")]
    [InlineData("١٢٣٤٥٦", "123456")]
    [InlineData("12۳4٥6", "123456")]
    [InlineData("abc 123", "abc 123")]
    public void Digits_of_any_script_become_ascii(string typed, string expected) =>
        Assert.Equal(expected, Digits.ToAscii(typed));
}
