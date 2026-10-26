using System.Globalization;

namespace Sqm.Domain.Identifiers;

/// <summary>
/// A SIM's ICCID, derived from its IMSI by the operator's numbering. The feed carries no ICCID.
/// </summary>
/// <remarks>
/// <para>
/// The rule is the product owner's (2026-10-26): <c>8998</c>, then the IMSI's digits 4-7, then
/// <c>000</c>, then its last eight. As the spreadsheet formula it came as,
/// <c>CONCATENATE("8998", MID(imsi, 4, 4), "000", RIGHT(imsi, 8))</c>. Nineteen digits.
/// </para>
/// <para>
/// It keeps every IMSI digit after the MCC, and every IMSI in the feed carries the MCC <c>432</c>
/// (and the MNC <c>11</c>: 312 million bindings, measured 2026-10-26), so it runs backwards exactly -
/// an ICCID is searched as the IMSI it came from, under the IMSI's permission and audit.
/// </para>
/// <para>
/// Derived, not observed, and the UI and the exports say so. Only a well-formed IMSI has one: the
/// rule's positions mean nothing on the 16- and 17-digit anomalies the feed quality page lists.
/// </para>
/// </remarks>
public static class Iccid
{
    /// <summary>An ICCID's length under this numbering.</summary>
    public const int Length = 19;

    private const string Prefix = "8998";
    private const string Filler = "000";
    private const string Mcc = "432";

    /// <summary>The ICCID of a well-formed IMSI, or <see langword="null"/>.</summary>
    public static string? FromImsi(string? imsi)
    {
        if (imsi is null || imsi.Length != Imsi.ExpectedLength || !AllDigits(imsi))
        {
            return null;
        }

        return string.Concat(Prefix, imsi.AsSpan(3, 4), Filler, imsi.AsSpan(imsi.Length - 8, 8));
    }

    /// <summary>The ICCID of a well-formed IMSI, or <see langword="null"/>.</summary>
    public static string? FromImsi(ulong imsi) => FromImsi(imsi.ToString(CultureInfo.InvariantCulture));

    /// <summary>The IMSI an ICCID of this numbering was derived from, or <see langword="null"/>.</summary>
    /// <remarks>Nineteen digits, beginning <c>8998</c> with <c>000</c> in positions 9-11; anything else is not one.</remarks>
    public static string? ToImsi(string? iccid)
    {
        if (iccid is null || iccid.Length != Length || !AllDigits(iccid)
            || !iccid.StartsWith(Prefix, StringComparison.Ordinal)
            || iccid.AsSpan(8, 3) is not Filler)
        {
            return null;
        }

        return string.Concat(Mcc, iccid.AsSpan(4, 4), iccid.AsSpan(11, 8));
    }

    private static bool AllDigits(string value)
    {
        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
