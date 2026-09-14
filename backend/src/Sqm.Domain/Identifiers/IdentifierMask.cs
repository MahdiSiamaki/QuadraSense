using System.Globalization;

namespace Sqm.Domain.Identifiers;

/// <summary>
/// Redacts identifiers for callers who may see that a record exists but not who it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>This runs on the server.</b> Masking applied in the browser is the same mistake as hiding a
/// button the API would have honoured: the raw value is in the response, one developer-tools panel
/// away, and in every proxy log between here and there. The API sends the masked string and never
/// the original.
/// </para>
/// <para>
/// <b>How much is kept, and why that much.</b> Enough to recognise a row you are already looking
/// at; not enough to write down and take away. What is kept is chosen per identifier from what the
/// digits mean rather than from a uniform rule:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>IMSI</b> keeps the leading <c>43211</c> and the last four. The first five are MCC+MNC and are
/// identical on all 295 million rows, so keeping them discloses nothing. Six digits are hidden:
/// a million candidates.
/// </item>
/// <item>
/// <b>MSISDN</b> keeps the operator prefix and the last two. The prefix is public - everyone knows
/// 0912 is MCI - and the remaining five hidden digits leave a hundred thousand candidates.
/// </item>
/// <item>
/// <b>IMEI</b> keeps the TAC in full and hides the serial entirely. The TAC identifies a device
/// <i>model</i>, which is the whole point of this product and is not personal; the serial
/// identifies one physical handset, which is.
/// </item>
/// </list>
/// <para>
/// The mask character is <c>*</c>, matching the count of hidden digits, so the length of the
/// original is still visible - an IMSI of unusual length stays visibly unusual when masked.
/// </para>
/// </remarks>
public static class IdentifierMask
{
    private const char MaskChar = '*';

    /// <summary>Masks an IMSI, keeping MCC+MNC and the last four digits.</summary>
    public static string Imsi(ulong value) => Imsi(ToDigits(value));

    /// <summary>Masks an IMSI given as digits.</summary>
    public static string Imsi(string digits) => Keep(digits, start: 5, end: 4);

    /// <summary>Masks a subscriber number, keeping the operator prefix and the last two digits.</summary>
    /// <remarks>
    /// Stored MSISDNs have no leading zero - 9121000002, not 09121000002 - so the operator prefix
    /// is the first three digits, not four.
    /// </remarks>
    public static string Msisdn(ulong value) => Msisdn(ToDigits(value));

    /// <summary>Masks a subscriber number given as digits.</summary>
    public static string Msisdn(string digits) => Keep(digits, start: 3, end: 2);

    /// <summary>Masks an IMEI, keeping the TAC and hiding the serial.</summary>
    public static string Imei(string imei)
    {
        ArgumentNullException.ThrowIfNull(imei);

        // The sentinel for "device unknown" is not an identifier and masking it would turn a
        // meaningful value into a meaningless one.
        return imei is "000000" or "" ? imei : Keep(imei, start: Tac.Length, end: 0);
    }

    /// <summary>
    /// Masks everything but the digits the caller supplied themselves.
    /// </summary>
    /// <remarks>
    /// Used for the term a user searched for. Redacting an identifier back to the person who typed
    /// it protects nothing and makes the result unreadable; what needs masking is what the search
    /// <i>revealed</i>.
    /// </remarks>
    public static string EchoOfInput(string typedDigits, string storedDigits)
    {
        ArgumentNullException.ThrowIfNull(typedDigits);
        ArgumentNullException.ThrowIfNull(storedDigits);

        if (!storedDigits.StartsWith(typedDigits, StringComparison.Ordinal))
        {
            // The stored value is not what was asked for - a prefix search whose rows differ past
            // the typed digits cannot happen, but a 16-digit anomaly matched by a 15-digit term
            // can. Fall back to the ordinary rule rather than echoing anything.
            return Imsi(storedDigits);
        }

        // Typed in full: there is nothing left to withhold. Masking an identifier back to the
        // person who just supplied it protects nothing and makes the row unreadable.
        return typedDigits.Length >= storedDigits.Length
            ? storedDigits
            : Keep(storedDigits, start: typedDigits.Length, end: 0);
    }

    private static string Keep(string digits, int start, int end)
    {
        if (digits.Length <= start + end)
        {
            // Too short to mask without revealing everything anyway. Returning it whole would be
            // worse than returning nothing, so it is fully masked.
            return new string(MaskChar, digits.Length);
        }

        var hidden = digits.Length - start - end;
        return string.Concat(digits.AsSpan(0, start), new string(MaskChar, hidden),
            digits.AsSpan(digits.Length - end, end));
    }

    private static string ToDigits(ulong value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
