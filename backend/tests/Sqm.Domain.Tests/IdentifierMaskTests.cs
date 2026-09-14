using Sqm.Domain.Identifiers;

namespace Sqm.Domain.Tests;

/// <summary>
/// What a masked identifier keeps, and what it must never keep.
/// </summary>
/// <remarks>
/// Masking is applied server-side, so these are the assertions that decide what actually leaves
/// the process for a caller without <c>identifier.reveal</c>. Every case states the property it
/// is protecting rather than pinning a string for its own sake.
/// </remarks>
public sealed class IdentifierMaskTests
{
    [Fact]
    public void A_masked_imsi_keeps_mcc_mnc_and_the_last_four()
    {
        // The first five digits are 43211 on all 295,013,916 rows, so keeping them discloses
        // nothing at all. Six digits are hidden: a million candidates.
        Assert.Equal("43211******1332", IdentifierMask.Imsi(432_113_991_761_332UL));
    }

    [Fact]
    public void A_masked_msisdn_keeps_the_operator_prefix_and_the_last_two()
    {
        // Stored without the leading zero, so the prefix is three digits, not four. Five hidden
        // digits leave a hundred thousand candidates.
        Assert.Equal("912*****02", IdentifierMask.Msisdn(9_121_000_002UL));
    }

    [Fact]
    public void A_masked_imei_keeps_the_tac_and_hides_the_serial()
    {
        // The TAC identifies a device MODEL, which is the product's entire purpose and is not
        // personal. The serial identifies one physical handset, which is.
        Assert.Equal("35586412******", IdentifierMask.Imei("35586412216447"));
    }

    [Fact]
    public void The_unknown_device_sentinel_is_left_alone()
    {
        // 5,522,495 bindings carry it. It is not an identifier, and masking it would turn a
        // meaningful value into a meaningless one.
        Assert.Equal("000000", IdentifierMask.Imei("000000"));
    }

    [Fact]
    public void Masking_preserves_length_so_an_anomaly_stays_visibly_anomalous()
    {
        var normal = IdentifierMask.Imsi("432113991761332");
        var odd = IdentifierMask.Imsi("4321121129462907");

        Assert.Equal(15, normal.Length);
        Assert.Equal(16, odd.Length);
    }

    [Fact]
    public void A_value_too_short_to_mask_partially_is_masked_entirely()
    {
        // 342,254 IMSIs in the data are only nine digits. Keeping five and the last four would
        // reveal all nine, so nothing is kept.
        Assert.Equal("*********", IdentifierMask.Imsi("432113926"));
    }

    [Fact]
    public void An_exact_search_echoes_the_term_the_caller_already_had()
    {
        // Redacting an identifier back to the person who just typed it protects nothing and makes
        // the row unreadable. What needs masking is what the search REVEALED.
        Assert.Equal(
            "432113991761332",
            IdentifierMask.EchoOfInput("432113991761332", "432113991761332"));
    }

    [Fact]
    public void A_prefix_search_masks_everything_past_the_digits_that_were_typed()
    {
        Assert.Equal(
            "432113991761***",
            IdentifierMask.EchoOfInput("432113991761", "432113991761332"));
    }

    [Fact]
    public void A_row_that_does_not_start_with_the_term_falls_back_to_the_ordinary_rule()
    {
        // Reachable when a 15-digit term matches one of the 16-digit anomalies. Echoing the term
        // over a different value would show digits the row does not have.
        Assert.Equal(
            "43211*******2907",
            IdentifierMask.EchoOfInput("999999999999999", "4321121129462907"));
    }

    [Theory]
    [InlineData("432113991761332")]
    [InlineData("432112901471597")]
    public void A_masked_imsi_never_contains_the_digits_it_is_meant_to_hide(string imsi)
    {
        var masked = IdentifierMask.Imsi(imsi);
        var hidden = imsi[5..^4];

        // The property, not the format: whatever the mask looks like, the middle must be gone.
        Assert.DoesNotContain(hidden, masked, StringComparison.Ordinal);
        Assert.Equal(6, masked.Count(c => c == '*'));
    }

    [Fact]
    public void Masking_is_not_reversible_by_comparing_two_masked_values()
    {
        // Two different SIMs on the same MCC+MNC whose last four digits differ must not become
        // distinguishable by anything except those four - which is the point of hiding the middle.
        var a = IdentifierMask.Imsi("432113991761332");
        var b = IdentifierMask.Imsi("432113000001332");

        Assert.Equal(a, b);
    }
}
