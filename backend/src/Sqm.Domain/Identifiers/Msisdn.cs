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

    /// <summary>
    /// Keeps the digits and discards the punctuation people write numbers with.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when a character appears that is neither a digit nor a known
    /// separator — a letter in the middle of a number is a typo worth reporting, not noise worth
    /// ignoring.
    /// </returns>
    private static bool TryKeepDigits(string raw, out string digits)
    {
        digits = string.Empty;
        var kept = new System.Text.StringBuilder(raw.Length);

        foreach (var c in raw)
        {
            if (char.IsAsciiDigit(c))
            {
                kept.Append(c);
            }
            else if (c is not ('+' or ' ' or '-' or '.' or '(' or ')' or ' ' or '	'))
            {
                return false;
            }
        }

        digits = kept.ToString();
        return true;
    }

    /// <summary>
    /// Reduces an international or national form to the national significant number the feed stores.
    /// </summary>
    /// <remarks>
    /// Order matters: the access code comes off before the country code, and the country code
    /// before the trunk prefix, because <c>+98 0913…</c> carries both and people do write it.
    /// Each strip is guarded on the resulting length, so a number that merely happens to begin
    /// with those digits is left alone.
    /// </remarks>
    private static string ToNationalSignificant(string digits)
    {
        // 00 — the international access code, when someone dials rather than types a plus.
        if (digits.Length > 2 && digits.StartsWith("00", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }

        // 98 — the country code. Only when what remains is a national number, with or without
        // its trunk prefix. No number this feed stores begins with 98, at any length.
        if (digits.StartsWith(CountryCode, StringComparison.Ordinal)
            && (digits.Length == CountryCode.Length + ExpectedLength
                || (digits.Length == CountryCode.Length + ExpectedLength + 1
                    && digits[CountryCode.Length] == '0')))
        {
            digits = digits[CountryCode.Length..];
        }

        // 0 — the national trunk prefix. Never a digit of the number: the stored values are
        // integers, so none of them can begin with a zero.
        if (digits.Length > 1 && digits[0] == '0')
        {
            digits = digits[1..];
        }

        return digits;
    }

    /// <summary>The leading four digits, used for operator-prefix analysis.</summary>
    public string Prefix4 => Value.ToString(System.Globalization.CultureInfo.InvariantCulture) is { Length: >= 4 } s
        ? s[..4]
        : string.Empty;

    /// <summary>The Iran country code, as it appears without a plus.</summary>
    private const string CountryCode = "98";

    /// <summary>
    /// Parses a subscriber number written in any of the ways a person writes one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The feed stores the national significant number: ten digits beginning with 9. People write
    /// and paste the same subscriber a dozen ways, and a lookup that only accepts the stored form
    /// makes the operator do the conversion — badly, under time pressure, for a number they are
    /// about to investigate.
    /// </para>
    /// <para>All of these name the same subscriber:</para>
    /// <code>
    /// 9131234567            as the feed stores it
    /// 0913 123 4567         national, with the trunk prefix and separators
    /// +98 913 123 4567      international
    /// 00989131234567        international with the 00 access code
    /// +98 0913 123 4567     both prefixes, which people do write
    /// </code>
    /// <para>
    /// <b>Why stripping the country code is safe here, measured rather than assumed.</b> Every
    /// normal number in the feed begins with 91, 99, 93, 90, 92, 95, 97 or 96 — never 98 — and
    /// none of the 24 length-anomalies begins with 98 either. So a leading 98 can only be the
    /// country code, and only when what remains is the right length. Three of those anomalies are
    /// foreign numbers stored whole (964 Iraq, 971 UAE, 994 Azerbaijan); they are found by pasting
    /// them as-is, because nothing strips a prefix this feed does not use.
    /// </para>
    /// <para>
    /// Separators are discarded, not rejected. A number copied out of a spreadsheet or a chat
    /// arrives with spaces, dashes or brackets, and refusing it teaches the operator to hand-clean
    /// input rather than paste it.
    /// </para>
    /// </remarks>
    public static bool TryParse(string? raw, [NotNullWhen(true)] out Msisdn? msisdn)
    {
        msisdn = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (!TryKeepDigits(raw, out var digits))
        {
            return false;
        }

        var value = ToNationalSignificant(digits);
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
