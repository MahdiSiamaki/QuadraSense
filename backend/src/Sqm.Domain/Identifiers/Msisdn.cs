using System.Diagnostics.CodeAnalysis;

namespace Sqm.Domain.Identifiers;

/// <summary>
/// A subscriber phone number, as delivered by the source (national format, no country code).
/// </summary>
/// <remarks>
/// Measured over 125.9M initial rows: 125,939,499 are exactly 10 digits. The remaining 24 rows have
/// lengths 8, 9, 12, 13 and 15 and are quarantined rather than truncated — silently trimming them would
/// fabricate a different subscriber's number.
/// <para>
/// All values are numeric (zero non-numeric characters observed), so a 64-bit integer is a safe storage
/// type and there are no significant leading zeros — unlike <see cref="Imei"/> and <see cref="Tac"/>.
/// </para>
/// 342 distinct 4-digit prefixes appear; the head is standard Iranian MCI (9149, 9148, 9144, 9147, …).
/// </remarks>
public readonly record struct Msisdn
{
    /// <summary>The only length considered well formed in this feed.</summary>
    public const int ExpectedLength = 10;

    private Msisdn(ulong value, int digitCount)
    {
        Value = value;
        DigitCount = digitCount;
    }

    /// <summary>Numeric value, suitable for storage as a 64-bit unsigned integer.</summary>
    public ulong Value { get; }

    /// <summary>Digit count as received, retained so anomalies can be reported accurately.</summary>
    public int DigitCount { get; }

    /// <summary>True when the number has the expected 10 digits.</summary>
    public bool IsWellFormed => DigitCount == ExpectedLength;

    /// <summary>The leading four digits, used for operator-prefix analysis.</summary>
    public string Prefix4 => Value.ToString(System.Globalization.CultureInfo.InvariantCulture) is { Length: >= 4 } s
        ? s[..4]
        : string.Empty;

    /// <summary>
    /// Parses a subscriber number, tolerating the national trunk prefix.
    /// </summary>
    /// <remarks>
    /// A single leading zero is stripped before the digits are counted. Iranian mobile numbers are
    /// written and dialled as <c>0991…</c>, the feed stores them as <c>991…</c>, and both name the
    /// same subscriber.
    ///
    /// Counting the raw string instead reported every number typed the way a person writes it as
    /// "unusual length, shown for review" — telling the operator that a perfectly ordinary number
    /// was suspicious, on the one screen whose job is to flag numbers that genuinely are.
    ///
    /// Only one zero, and only a leading one. This feed has no significant leading zeros in the
    /// MSISDN column (measured: all 125.9M values are numeric with none), unlike IMEI and TAC
    /// where a leading zero is part of the identifier and stripping it would name a different
    /// device.
    /// </remarks>
    public static bool TryParse(string? raw, [NotNullWhen(true)] out Msisdn? msisdn)
    {
        msisdn = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var value = raw.Trim();

        // The trunk prefix, not a digit of the number.
        if (value.Length > 1 && value[0] == '0')
        {
            value = value[1..];
        }

        if (value.Length is 0 or > 19) return false;

        ulong parsed = 0;
        foreach (var c in value)
        {
            if (c is < '0' or > '9') return false;
            parsed = (parsed * 10) + (ulong)(c - '0');
        }

        msisdn = new Msisdn(parsed, value.Length);
        return true;
    }

    public override string ToString() =>
        Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
