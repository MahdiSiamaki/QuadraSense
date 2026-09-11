using System.Diagnostics.CodeAnalysis;

namespace Sqm.Domain.Identifiers;

/// <summary>
/// An IMEI as it appears in the source feed.
/// </summary>
/// <remarks>
/// Measured characteristics of the real data (125.9M initial rows + 677.6M delta rows):
/// <list type="bullet">
///   <item>93.0% / 97.0% are exactly 14 digits — TAC(8) + serial(6), check digit already stripped.</item>
///   <item>6.97% / 2.96% are the literal <c>000000</c>. This is a <b>sentinel meaning "device unknown"</b>,
///         not a parse error, and must be preserved as a category rather than discarded.</item>
///   <item>~0.02% are other short lengths (6-13): <c>000100</c>, <c>000510</c>, <c>501515014</c>, etc.</item>
///   <item>Zero non-numeric characters were observed in any IMEI.</item>
///   <item>No IMEI is 15 or 16 digits, so no IMEI/IMEISV normalisation is required.</item>
/// </list>
/// Stored as text, never as an integer: short values carry significant leading zeros.
/// </remarks>
public readonly record struct Imei
{
    /// <summary>The sentinel the source uses for "device not known".</summary>
    public const string UnknownDeviceSentinel = "000000";

    /// <summary>Length of a well-formed IMEI in this feed: TAC(8) + serial(6).</summary>
    public const int WellFormedLength = 14;

    private Imei(string value) => Value = value;

    /// <summary>The raw value exactly as received. Never reformatted.</summary>
    public string Value { get; }

    /// <summary>
    /// True when this is the <c>000000</c> sentinel. Such rows can never be TAC-enriched and are
    /// reported as an explicit "Unknown device" bucket.
    /// </summary>
    public bool IsUnknownDevice => Value == UnknownDeviceSentinel;

    /// <summary>True when the value is exactly 14 digits and can yield a TAC.</summary>
    public bool IsWellFormed => Value.Length == WellFormedLength;

    /// <summary>
    /// The 8-digit TAC, or <see langword="null"/> when the IMEI is not well formed.
    /// The length guard is mandatory: taking <c>substr(imei, 1, 8)</c> of <c>000000</c> would
    /// silently produce a bogus 6-character TAC that matches nothing.
    /// </summary>
    public Tac? Tac => IsWellFormed ? Identifiers.Tac.FromImei(Value) : null;

    /// <summary>
    /// Parses an IMEI. Only non-numeric or empty input is rejected; unusual lengths are accepted and
    /// classified, because they are a real and meaningful part of this feed.
    /// </summary>
    public static bool TryParse(string? raw, [NotNullWhen(true)] out Imei? imei)
    {
        imei = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var value = raw.Trim();
        if (!IsAllDigits(value)) return false;

        imei = new Imei(value);
        return true;
    }

    /// <summary>Classifies the IMEI for data-quality reporting.</summary>
    public ImeiQuality Quality => this switch
    {
        { IsWellFormed: true } => ImeiQuality.WellFormed,
        { IsUnknownDevice: true } => ImeiQuality.UnknownDevice,
        _ => ImeiQuality.Malformed,
    };

    private static bool IsAllDigits(string s)
    {
        foreach (var c in s)
        {
            if (c is < '0' or > '9') return false;
        }
        return true;
    }

    public override string ToString() => Value;
}

/// <summary>How an IMEI is categorised for data-quality reporting.</summary>
public enum ImeiQuality
{
    /// <summary>Exactly 14 digits; a TAC can be derived and enrichment attempted.</summary>
    WellFormed,

    /// <summary>The <c>000000</c> sentinel — the source does not know the device. A valid business category.</summary>
    UnknownDevice,

    /// <summary>Numeric but an unexpected length. Kept and flagged; excluded from the TAC join.</summary>
    Malformed,
}
