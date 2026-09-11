using System.Diagnostics.CodeAnalysis;

namespace Sqm.Domain.Identifiers;

/// <summary>
/// A SIM's International Mobile Subscriber Identity: MCC(3) + MNC(2) + MSIN(10).
/// </summary>
/// <remarks>
/// Measured over 125.9M initial rows: 125,939,514 are exactly 15 digits; 9 rows are 16 or 17 digits and
/// are quarantined. Every IMSI observed carries MCC+MNC <c>43211</c> — Iran / MCI — which is why the
/// system is single-tenant today (see <c>docs/architecture/01-overview.md</c>).
/// <para>
/// IMSI and MSISDN are effectively 1:1 (99.97%). The 0.03% that are not are exactly the SIM-swap and
/// number-change population the product needs to surface, so the model must not collapse them.
/// </para>
/// </remarks>
public readonly record struct Imsi
{
    /// <summary>The only length considered well formed in this feed.</summary>
    public const int ExpectedLength = 15;

    private Imsi(ulong value, int digitCount, string mccMnc)
    {
        Value = value;
        DigitCount = digitCount;
        MccMnc = mccMnc;
    }

    /// <summary>Numeric value, suitable for storage as a 64-bit unsigned integer.</summary>
    public ulong Value { get; }

    /// <summary>Digit count as received, retained so anomalies can be reported accurately.</summary>
    public int DigitCount { get; }

    /// <summary>The 5-character MCC+MNC prefix, e.g. <c>43211</c>.</summary>
    public string MccMnc { get; }

    /// <summary>True when the IMSI has the expected 15 digits.</summary>
    public bool IsWellFormed => DigitCount == ExpectedLength;

    public static bool TryParse(string? raw, [NotNullWhen(true)] out Imsi? imsi)
    {
        imsi = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var value = raw.Trim();
        if (value.Length is < 5 or > 19) return false;

        ulong parsed = 0;
        foreach (var c in value)
        {
            if (c is < '0' or > '9') return false;
            parsed = (parsed * 10) + (ulong)(c - '0');
        }

        imsi = new Imsi(parsed, value.Length, value[..5]);
        return true;
    }

    public override string ToString() =>
        Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
