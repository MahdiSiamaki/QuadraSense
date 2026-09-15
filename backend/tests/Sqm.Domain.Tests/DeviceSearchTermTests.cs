using Sqm.Domain.Identifiers;

namespace Sqm.Domain.Tests;

/// <summary>
/// The Devices search box accepts five kinds of input and decides between them by length.
/// </summary>
/// <remarks>
/// These tests are really about one claim: <b>TAC, MSISDN, IMEI and IMSI have four distinct
/// lengths in this feed</b>, so length alone is enough. That is a property of the measured data -
/// no IMEI here is 15 or 16 digits - and not of GSM in general, where a 15-digit string could be
/// an IMEI carrying its check digit. If the feed ever changes, these tests are where it should
/// hurt first.
/// </remarks>
public sealed class DeviceSearchTermTests
{
    [Theory]
    [InlineData("35004012", DeviceSearchKind.Tac)]
    [InlineData("9149000001", DeviceSearchKind.Msisdn)]
    [InlineData("35004012345678", DeviceSearchKind.Imei)]
    [InlineData("432113900974194", DeviceSearchKind.Imsi)]
    public void ClassifiesEachIdentifierByItsLength(string input, DeviceSearchKind expected)
    {
        var term = DeviceSearchTerm.Classify(input);

        Assert.NotNull(term);
        Assert.Equal(expected, term!.Value.Kind);
        Assert.Equal(input, term.Value.Digits);
        Assert.True(term.Value.IsIdentifier);
    }

    [Theory]
    [InlineData("Galaxy A54")]
    [InlineData("samsung")]
    [InlineData("iPhone 15 Pro")]
    public void TreatsANameAsText(string input)
    {
        var term = DeviceSearchTerm.Classify(input);

        Assert.NotNull(term);
        Assert.Equal(DeviceSearchKind.Text, term!.Value.Kind);
        Assert.False(term.Value.IsIdentifier);
        Assert.Equal(input, term.Value.Text);
    }

    /// <summary>
    /// A name containing digits must not be mined for them.
    /// </summary>
    /// <remarks>
    /// "Galaxy A54" holds the digits 54. Stripping non-digits and classifying what is left would
    /// turn a model-name search into a search for a two-digit code, which matches nothing and
    /// looks like the catalogue is missing the device. Any non-digit that is not number
    /// punctuation makes the whole term text.
    /// </remarks>
    [Fact]
    public void DoesNotMineDigitsOutOfANameThatContainsThem()
    {
        var term = DeviceSearchTerm.Classify("Galaxy A54");

        Assert.NotNull(term);
        Assert.Equal(DeviceSearchKind.Text, term!.Value.Kind);
        Assert.Equal(string.Empty, term.Value.Digits);
    }

    /// <summary>People write numbers with punctuation, and it carries no meaning.</summary>
    [Theory]
    [InlineData("3500-4012")]
    [InlineData("3500 4012")]
    [InlineData("(3500) 4012")]
    [InlineData("  35004012  ")]
    public void IgnoresThePunctuationPeopleWriteNumbersWith(string input)
    {
        var term = DeviceSearchTerm.Classify(input);

        Assert.NotNull(term);
        Assert.Equal(DeviceSearchKind.Tac, term!.Value.Kind);
        Assert.Equal("35004012", term.Value.Digits);
    }

    /// <summary>
    /// The unknown-device sentinel is data, not junk.
    /// </summary>
    /// <remarks>
    /// <c>000000</c> is what the source writes when it does not know the device, and it is carried
    /// by millions of bindings - 6.97% of the initial dump. Six digits matches no identifier
    /// length, so without this it would fall through to a text search and find nothing, when the
    /// honest answer is "those bindings have no device model".
    /// </remarks>
    [Fact]
    public void RecognisesTheUnknownDeviceSentinel()
    {
        var term = DeviceSearchTerm.Classify("000000");

        Assert.NotNull(term);
        Assert.Equal(DeviceSearchKind.UnknownDeviceSentinel, term!.Value.Kind);
        Assert.True(term.Value.IsIdentifier);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("123456789012345678")]
    [InlineData("1234567890123")]
    public void FallsBackToTextForALengthThatMatchesNoIdentifier(string input)
    {
        var term = DeviceSearchTerm.Classify(input);

        Assert.NotNull(term);
        Assert.Equal(DeviceSearchKind.Text, term!.Value.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsNothingForABlankTerm(string? input)
    {
        Assert.Null(DeviceSearchTerm.Classify(input));
    }

    /// <summary>
    /// A TAC and an IMEI resolve to a model without touching a binding table.
    /// </summary>
    /// <remarks>
    /// Verified against the real data: of 284,341,927 well-formed rows of current state,
    /// <c>substring(imei, 1, 8)</c> differed from the stored <c>tac</c> in <b>zero</b> of them.
    /// That is what lets the Devices module treat a model as a contiguous range of an
    /// IMEI-ordered table rather than a filter over 295 million rows.
    /// </remarks>
    [Theory]
    [InlineData("35004012", "35004012")]
    [InlineData("35004012345678", "35004012")]
    public void ResolvesATacOrAnImeiToItsModelWithoutAQuery(string input, string expected)
    {
        var term = DeviceSearchTerm.Classify(input);

        Assert.NotNull(term);
        Assert.True(term!.Value.TryGetTac(out var tac));
        Assert.Equal(expected, tac);
    }

    /// <summary>
    /// A SIM and a number do not.
    /// </summary>
    /// <remarks>
    /// They reach a device only through a binding, which is a query and a permission check. Saying
    /// so here rather than guessing is what keeps those checks from being skipped.
    /// </remarks>
    [Theory]
    [InlineData("432113900974194")]
    [InlineData("9149000001")]
    [InlineData("Galaxy A54")]
    public void DoesNotGuessAModelForASimOrANumber(string input)
    {
        var term = DeviceSearchTerm.Classify(input);

        Assert.NotNull(term);
        Assert.False(term!.Value.TryGetTac(out var tac));
        Assert.Null(tac);
    }
}
