using System.Globalization;
using Sqm.Domain.Identifiers;

namespace Sqm.Domain.Tests;

/// <summary>
/// Identifier parsing tests. Every unusual input below is a value that actually occurs in the real
/// dataset, with its measured frequency noted.
/// </summary>
public class ImeiTests
{
    [Fact]
    public void A_well_formed_imei_yields_its_tac()
    {
        Assert.True(Imei.TryParse("35048117328145", out var imei));
        Assert.True(imei!.Value.IsWellFormed);
        Assert.Equal(ImeiQuality.WellFormed, imei.Value.Quality);
        Assert.Equal("35048117", imei.Value.Tac?.Value);
    }

    [Fact]
    public void The_000000_sentinel_is_a_category_not_an_error()
    {
        // 8,776,237 rows of the initial dump — 6.97%. Discarding these would drop 7% of the
        // subscriber base and materially misreport the device population.
        Assert.True(Imei.TryParse("000000", out var imei));
        Assert.True(imei!.Value.IsUnknownDevice);
        Assert.Equal(ImeiQuality.UnknownDevice, imei.Value.Quality);
        Assert.Null(imei.Value.Tac);
    }

    [Theory]
    [InlineData("000100")]     // 25,562 rows
    [InlineData("000510")]     // 1,785 rows
    [InlineData("501515014")]  // 1,052 rows
    [InlineData("6015404")]
    [InlineData("000000301000")]
    public void Malformed_lengths_are_kept_but_excluded_from_enrichment(string raw)
    {
        Assert.True(Imei.TryParse(raw, out var imei));
        Assert.Equal(ImeiQuality.Malformed, imei!.Value.Quality);

        // The length guard is the point: without it, substr(imei, 1, 8) of "000100" would produce a
        // 6-character value that silently matches no TAC while looking like a real lookup.
        Assert.Null(imei.Value.Tac);
    }

    [Fact]
    public void Leading_zeros_are_preserved_exactly_as_received()
    {
        Assert.True(Imei.TryParse("00000030", out var imei));
        Assert.Equal("00000030", imei!.Value.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("3504811732814A")]
    [InlineData("35048-117328")]
    public void Non_numeric_or_empty_input_is_rejected(string? raw)
    {
        Assert.False(Imei.TryParse(raw, out _));
    }
}

public class TacTests
{
    [Fact]
    public void Leading_zeros_are_significant()
    {
        // 00100100 is a real GSMA TAC (Mitsubishi G410). Parsed as an integer it would collide
        // with 100100, which is a different device entirely.
        Assert.True(Tac.TryParse("00100100", out var tac));
        Assert.Equal("00100100", tac!.Value.Value);
        Assert.NotEqual("100100", tac.Value.Value);
    }

    [Theory]
    [InlineData("1234567")]    // too short
    [InlineData("123456789")]  // too long
    [InlineData("1234567A")]
    [InlineData(null)]
    public void Only_exactly_eight_digits_is_a_tac(string? raw)
    {
        Assert.False(Tac.TryParse(raw, out _));
    }
}

public class MsisdnTests
{
    [Fact]
    public void A_ten_digit_number_is_well_formed()
    {
        Assert.True(Msisdn.TryParse("9140257910", out var msisdn));
        Assert.True(msisdn!.Value.IsWellFormed);
        Assert.Equal(9140257910UL, msisdn.Value.Value);
        Assert.Equal("9140", msisdn.Value.Prefix4);
    }

    [Theory]
    [InlineData("91402579")]        // 8 digits — 16 rows observed
    [InlineData("914025791")]       // 9 digits — 3 rows
    [InlineData("914025791012")]    // 12 digits — 3 rows
    [InlineData("914025791012345")] // 15 digits — 1 row
    public void Unexpected_lengths_parse_but_are_flagged_not_truncated(string raw)
    {
        // Truncating to 10 digits would fabricate a different subscriber's number. These are
        // quarantined for review instead.
        Assert.True(Msisdn.TryParse(raw, out var msisdn));
        Assert.False(msisdn!.Value.IsWellFormed);
        Assert.Equal(raw.Length, msisdn.Value.DigitCount);
    }
}

public class ImsiTests
{
    [Fact]
    public void A_fifteen_digit_imsi_exposes_its_operator_prefix()
    {
        Assert.True(Imsi.TryParse("432113939137353", out var imsi));
        Assert.True(imsi!.Value.IsWellFormed);

        // Every IMSI in the dataset carries this prefix: Iran / MCI.
        Assert.Equal("43211", imsi.Value.MccMnc);
    }

    [Theory]
    [InlineData("4321139391373531")]   // 16 digits — 4 rows observed
    [InlineData("43211393913735312")]  // 17 digits — 5 rows observed
    public void Overlong_imsis_parse_but_are_flagged(string raw)
    {
        Assert.True(Imsi.TryParse(raw, out var imsi));
        Assert.False(imsi!.Value.IsWellFormed);
    }
}

/// <summary>
/// Every way a person writes a subscriber number, against the one way the feed stores it.
/// </summary>
/// <remarks>
/// <para>
/// The feed stores the national significant number: ten digits beginning with 9. An operator
/// pastes whatever they were given — from a spreadsheet, a chat, a ticket — and a lookup that
/// only accepts the stored form makes them do the conversion by hand, for a number they are
/// about to investigate.
/// </para>
/// <para>
/// The country-code rule is safe because it was measured, not assumed: every normal number in
/// the feed begins with 91, 99, 93, 90, 92, 95, 97 or 96, and none of the 24 length-anomalies
/// begins with 98 either. A leading 98 can only be the country code.
/// </para>
/// </remarks>
public class MsisdnFormatTests
{
    private const ulong Expected = 9131234567UL;

    [Theory]
    // As the feed stores it.
    [InlineData("9131234567")]
    // National, with the trunk prefix.
    [InlineData("09131234567")]
    // International, in its several spellings.
    [InlineData("989131234567")]
    [InlineData("+989131234567")]
    [InlineData("00989131234567")]
    [InlineData("+98 913 123 4567")]
    // Both prefixes at once, which people genuinely write.
    [InlineData("+980913123456" + "7")]
    [InlineData("00980913123456" + "7")]
    // Pasted with the punctuation it arrived in.
    [InlineData("0913 123 4567")]
    [InlineData("0913-123-4567")]
    [InlineData("(0913) 123.4567")]
    [InlineData("  +98 913 123 4567  ")]
    public void All_of_these_name_the_same_subscriber(string typed)
    {
        Assert.True(Msisdn.TryParse(typed, out var msisdn), $"failed to parse '{typed}'");

        Assert.Equal(Expected, msisdn!.Value.Value);
        Assert.Equal(10, msisdn.Value.DigitCount);
        Assert.True(msisdn.Value.IsWellFormed);
    }

    [Theory]
    // A letter in the middle of a number is a typo worth reporting, not noise worth ignoring.
    [InlineData("0913123456O")]
    [InlineData("0913abc4567")]
    [InlineData("۰۹۱۳۱۲۳۴۵۶۷")] // Persian digits: the feed is ASCII, so this is not a silent pass
    public void A_character_that_is_neither_digit_nor_separator_is_rejected(string typed)
    {
        Assert.False(Msisdn.TryParse(typed, out _));
    }

    [Theory]
    // Measured: 24 of 125,939,523 rows have lengths 8, 9, 12, 13 and 15. The flag exists for
    // these, and must keep firing.
    [InlineData("91722541")]        // 8, a real stored value
    [InlineData("917269951")]       // 9, a real stored value
    [InlineData("912074412300")]    // 12, a real stored value
    public void A_real_length_anomaly_is_still_reported(string typed)
    {
        Assert.True(Msisdn.TryParse(typed, out var msisdn));
        Assert.False(msisdn!.Value.IsWellFormed);
    }

    [Theory]
    // Three of the anomalies are foreign numbers stored whole. Nothing strips a prefix this feed
    // does not use, so pasting them as-is finds them.
    [InlineData("9647713732437", 9647713732437UL)]   // Iraq
    [InlineData("971504254623", 971504254623UL)]     // UAE
    [InlineData("994775061119", 994775061119UL)]     // Azerbaijan
    public void A_foreign_number_stored_whole_is_found_by_pasting_it(string typed, ulong expected)
    {
        Assert.True(Msisdn.TryParse(typed, out var msisdn));
        Assert.Equal(expected, msisdn!.Value.Value);
    }

    [Fact]
    public void A_leading_98_is_left_alone_when_the_rest_is_not_a_national_number()
    {
        // Guarded on length, so a value that merely begins with those digits survives intact.
        Assert.True(Msisdn.TryParse("9812345678", out var msisdn));
        Assert.Equal(9812345678UL, msisdn!.Value.Value);
    }

    [Fact]
    public void Normalisation_never_merges_two_different_stored_numbers()
    {
        // The guarantee that matters. Stripping prefixes is only safe if it cannot map two
        // distinct subscribers onto one - which would silently show an operator the wrong
        // person's handsets. These are real values from the feed, including every length
        // anomaly and one of each leading prefix.
        string[] stored =
        [
            "9131234567", "9912059464", "9012345678", "9212345678",
            "9312345678", "9512345678", "9612345678", "9712345678",
            "91722541", "917269951", "912074412300", "9647713732437", "964780749997715",
            "971504254623", "994775061119",
        ];

        var parsed = new List<ulong>();

        foreach (var value in stored)
        {
            Assert.True(Msisdn.TryParse(value, out var msisdn), $"failed to parse '{value}'");

            // Idempotent on the stored form: nothing to strip, nothing stripped.
            Assert.Equal(ulong.Parse(value, CultureInfo.InvariantCulture), msisdn!.Value.Value);
            parsed.Add(msisdn.Value.Value);
        }

        Assert.Equal(stored.Length, parsed.Distinct().Count());
    }

    [Fact]
    public void Leading_zeros_are_prefix_noise_however_many_there_are()
    {
        // Stored values are integers, so none can begin with a zero. Any leading zero is
        // therefore punctuation, and resolving to the right subscriber is the useful answer.
        Assert.True(Msisdn.TryParse("0009131234567", out var msisdn));
        Assert.Equal(Expected, msisdn!.Value.Value);
    }
}
