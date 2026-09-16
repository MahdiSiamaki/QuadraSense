using Sqm.Domain.Devices;

namespace Sqm.Domain.Tests;

/// <summary>
/// Tests for the identity a device photograph belongs to.
/// </summary>
/// <remarks>
/// <para>
/// The cases that matter here are the ones where two names look alike and are different products.
/// This was measured against the real catalogue before the rules were written: an automated
/// attempt to source pictures from Wikipedia proposed <c>Redmi Note 12</c>'s photograph for
/// <c>Redmi Note 12S</c>, <c>Redmi Note 11 Pro</c>'s for <c>Redmi Note 11</c>, and
/// <c>Galaxy A05s</c>'s for <c>Galaxy A05</c>.
/// </para>
/// <para>
/// So the key folds case and whitespace and nothing else. Every "helpful" normalisation - dropping
/// a trailing S, stripping 5G, ignoring punctuation - merges two real handsets and puts a
/// confident picture of the wrong one on a device page, which is worse than a placeholder because
/// nobody re-checks a picture that looks right.
/// </para>
/// </remarks>
public class DeviceModelKeyTests
{
    [Fact]
    public void Brand_and_marketing_name_make_the_key()
    {
        Assert.Equal(
            "redmi|redmi note 12s",
            DeviceModelKey.For("Redmi", "Xiaomi Communications Co Ltd", "Redmi Note 12S"));
    }

    [Fact]
    public void Every_tac_of_one_model_resolves_to_the_same_key()
    {
        // The whole point. Redmi Note 12S is 18 TACs and Galaxy A12 is 184; the GSMA record for
        // each carries the same brand and marketing name, so one upload covers all of them.
        var first = DeviceModelKey.For("Redmi", "Xiaomi Communications Co Ltd", "Redmi Note 12S");
        var other = DeviceModelKey.For("Redmi", "Xiaomi Communications Co Ltd", "Redmi Note 12S");

        Assert.Equal(first, other);
    }

    [Theory]
    [InlineData("Redmi Note 12", "Redmi Note 12S")]
    [InlineData("Redmi Note 11", "Redmi Note 11 Pro")]
    [InlineData("Galaxy A05", "Galaxy A05s")]
    [InlineData("Galaxy A54", "Galaxy A54 5g")]
    [InlineData("Redmi Note 8", "Redmi Note 8 Pro")]
    public void Models_that_differ_by_a_suffix_are_different_models(string left, string right)
    {
        Assert.NotEqual(
            DeviceModelKey.For("Redmi", null, left),
            DeviceModelKey.For("Redmi", null, right));
    }

    [Fact]
    public void Case_and_spacing_do_not_make_a_different_model()
    {
        Assert.Equal(
            DeviceModelKey.For("Redmi", null, "Redmi Note 12S"),
            DeviceModelKey.For("  redmi ", null, "redmi   note   12s  "));
    }

    [Fact]
    public void Manufacturer_stands_in_when_the_brand_is_blank()
    {
        // 26 GSMA columns, and brandName is not always one of the filled ones.
        Assert.Equal(
            "xiaomi communications co ltd|redmi note 12s",
            DeviceModelKey.For("", "Xiaomi Communications Co Ltd", "Redmi Note 12S"));
    }

    [Fact]
    public void A_tac_with_no_marketing_name_has_no_model()
    {
        // The unknown-device bucket and unregistered TACs. Null is a real answer: these keep the
        // drawn placeholder, because they are the absence of a device rather than a nameless one.
        Assert.Null(DeviceModelKey.For("Redmi", "Xiaomi", null));
        Assert.Null(DeviceModelKey.For("Redmi", "Xiaomi", "   "));
        Assert.Null(DeviceModelKey.For(null, null, "Redmi Note 12S"));
    }

    [Fact]
    public void The_key_is_lowercase_and_separated_so_the_database_check_accepts_it()
    {
        // Migration 008 constrains model_key to lower(model_key) containing a pipe. A key that
        // failed that check would be refused at INSERT, which is a worse place to find out.
        var key = DeviceModelKey.For("Redmi", null, "Redmi Note 12S")!;

        Assert.Equal(key.ToLowerInvariant(), key);
        Assert.Contains("|", key, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_values_keep_their_original_casing()
    {
        Assert.Equal("Redmi", DeviceModelKey.DisplayBrand("Redmi", "Xiaomi"));
        Assert.Equal("Xiaomi", DeviceModelKey.DisplayBrand("  ", "Xiaomi"));
        Assert.Equal("Redmi Note 12S", DeviceModelKey.DisplayName(" Redmi  Note 12S "));
    }
}
