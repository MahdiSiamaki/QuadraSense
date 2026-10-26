using Sqm.Domain.Identifiers;

namespace Sqm.Domain.Tests;

/// <summary>
/// The ICCID the operator's numbering gives an IMSI, and the IMSI an ICCID came from.
/// </summary>
/// <remarks>
/// The rule is the product owner's spreadsheet formula (2026-10-26):
/// <c>CONCATENATE("8998", MID(imsi, 4, 4), "000", RIGHT(imsi, 8))</c>. The identifiers here are
/// made up: this repository is public.
/// </remarks>
public sealed class IccidTests
{
    [Fact]
    public void An_imsi_gives_8998_its_digits_four_to_seven_000_and_its_last_eight()
    {
        // 432 11 01 23456789: MID(4, 4) is "1101", RIGHT(8) is "23456789".
        Assert.Equal("8998110100023456789", Iccid.FromImsi("432110123456789"));
        Assert.Equal("8998110100023456789", Iccid.FromImsi(432_110_123_456_789UL));
    }

    [Fact]
    public void An_iccid_gives_back_the_imsi_it_came_from()
    {
        Assert.Equal("432110123456789", Iccid.ToImsi("8998110100023456789"));
    }

    [Theory]
    [InlineData("432110000000001")]
    [InlineData("432119999999999")]
    [InlineData("432112701600000")]
    public void Every_well_formed_imsi_survives_the_round_trip(string imsi)
    {
        Assert.Equal(imsi, Iccid.ToImsi(Iccid.FromImsi(imsi)));
    }

    [Theory]
    [InlineData("4321101234567890")] // 16 digits: the anomaly the feed quality page lists
    [InlineData("43211012345678")] // 14
    [InlineData("43211012345678x")]
    [InlineData("")]
    [InlineData(null)]
    public void Only_a_well_formed_imsi_has_one(string? imsi)
    {
        Assert.Null(Iccid.FromImsi(imsi));
    }

    [Theory]
    [InlineData("8998110100023456789x")] // 20
    [InlineData("899811010002345678")] // 18
    [InlineData("8997110100023456789")] // not 8998
    [InlineData("8998110112323456789")] // positions 9-11 are not 000
    [InlineData("89981101000234567a9")]
    [InlineData(null)]
    public void Anything_else_is_not_an_iccid_of_this_numbering(string? iccid)
    {
        Assert.Null(Iccid.ToImsi(iccid));
    }

    [Fact]
    public void A_masked_iccid_hides_what_the_masked_imsi_hides()
    {
        // The IMSI's mask keeps 43211 and the last four; the ICCID's keeps 8998, the MNC and the
        // same last four. The six IMSI digits hidden in one are hidden in the other.
        Assert.Equal("43211******6789", IdentifierMask.Imsi("432110123456789"));
        Assert.Equal("899811*********6789", IdentifierMask.Iccid("8998110100023456789"));
    }
}
