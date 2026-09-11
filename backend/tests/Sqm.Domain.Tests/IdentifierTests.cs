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
