using System.Diagnostics.CodeAnalysis;

namespace Sqm.Domain.Identifiers;

/// <summary>What a string typed into the Devices search box turned out to be.</summary>
public enum DeviceSearchKind
{
    /// <summary>Not an identifier. Matched against manufacturer, brand, model and marketing name.</summary>
    Text,

    /// <summary>Eight digits: a device model, which is the thing being searched for.</summary>
    Tac,

    /// <summary>Ten digits: a subscriber number. Resolves to the handsets bound to it.</summary>
    Msisdn,

    /// <summary>Fourteen digits: one handset. Resolves to its model, and to its SIMs and numbers.</summary>
    Imei,

    /// <summary>Fifteen digits: one SIM. Resolves to the handsets it has been in.</summary>
    Imsi,

    /// <summary>The <c>000000</c> sentinel. Resolves to the "(unknown device)" bucket, not to nothing.</summary>
    UnknownDeviceSentinel,
}

/// <summary>
/// Classifies one search term so the Devices search box can accept any identifier.
/// </summary>
/// <remarks>
/// <para>
/// <b>Length alone decides, and that works because the four lengths are disjoint in this feed.</b>
/// It is worth writing down why, because in the general GSM world they are not:
/// </para>
/// <list type="bullet">
/// <item><b>TAC — 8.</b> All 270,166 rows of the GSMA export are exactly 8 characters.</item>
/// <item><b>MSISDN — 10.</b> 125,939,499 of 125,939,523 initial rows. The 24 exceptions have
/// lengths 8, 9, 12, 13 and 15 and are quarantined, never truncated.</item>
/// <item><b>IMEI — 14.</b> TAC(8) + serial(6), <i>with the check digit already stripped by the
/// source</i>. Measured: <b>no IMEI in this feed is 15 or 16 digits.</b> That is the fact that
/// makes 15 unambiguous - elsewhere a 15-digit string could be an IMEI with its check digit, and
/// this classifier would be wrong.</item>
/// <item><b>IMSI — 15.</b> Every one begins 43211: MCC 432, MNC 11, Iran / MCI.</item>
/// </list>
/// <para>
/// The two collisions that remain are handled rather than ignored. A 15-digit string that does
/// <i>not</i> begin 43211 is reported as <see cref="DeviceSearchKind.Imsi"/> anyway, because
/// nothing else in this feed is 15 digits and the lookup will simply return nothing - which is a
/// truthful answer. And <c>000000</c> is six digits, matching no identifier, but it is not junk:
/// it is the source's sentinel for "device unknown", carried by millions of rows, and it gets its
/// own kind so the search can take the reader to that bucket instead of shrugging.
/// </para>
/// <para>
/// Anything else - a length that matches nothing, or any non-digit - is
/// <see cref="DeviceSearchKind.Text"/>. There is no error state: a person typing "galaxy a54"
/// into a search box has not made a mistake.
/// </para>
/// </remarks>
public readonly record struct DeviceSearchTerm
{
    /// <summary>Characters people put in identifiers that carry no meaning.</summary>
    private static readonly char[] Noise = [' ', '-', '.', '_', '(', ')', '/', '\t'];

    private DeviceSearchTerm(DeviceSearchKind kind, string digits, string text)
    {
        Kind = kind;
        Digits = digits;
        Text = text;
    }

    /// <summary>What the term was taken to be.</summary>
    public DeviceSearchKind Kind { get; }

    /// <summary>The digits, punctuation removed. Empty for <see cref="DeviceSearchKind.Text"/>.</summary>
    public string Digits { get; }

    /// <summary>The term as typed, trimmed. This is what a text search matches against.</summary>
    public string Text { get; }

    /// <summary>True when the term names one identifier rather than describing a model.</summary>
    public bool IsIdentifier => Kind is not DeviceSearchKind.Text;

    /// <summary>
    /// Classifies a term. Never fails: an unrecognised shape is a text search.
    /// </summary>
    /// <param name="raw">What the user typed.</param>
    /// <returns>The classification, or <see langword="null"/> when the term is blank.</returns>
    public static DeviceSearchTerm? Classify(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return null;
        }

        // A phone number written the way people write one - 09121234567, +98 912 123 4567,
        // 0098... - is a number, not text. Classified by length alone these were eleven, twelve
        // or thirteen digits (or not digits at all, for the plus), so they fell through to a name
        // search that found nothing, and the relationship explorer refused them outright - while
        // Subscriber Lookup, which normalises through Msisdn, accepted the same input. Only the
        // lengths no other identifier has are tried: 0098 plus ten digits is fourteen, which is an
        // IMEI's length, and stays one.
        var dialled = text.Contains('+', StringComparison.Ordinal)
                      || StripLength(text) is 11 or 12 or 13;
        if (dialled && Msisdn.TryParse(text, out var number) && number.Value.IsWellFormed)
        {
            return new DeviceSearchTerm(DeviceSearchKind.Msisdn, number.Value.ToString(), text);
        }

        var digits = Strip(text);

        // Not all digits, so there is nothing to resolve - it is a name.
        if (digits.Length == 0)
        {
            return new DeviceSearchTerm(DeviceSearchKind.Text, string.Empty, text);
        }

        var kind = digits.Length switch
        {
            Tac.Length => DeviceSearchKind.Tac,
            Msisdn.ExpectedLength => DeviceSearchKind.Msisdn,
            Imei.WellFormedLength => DeviceSearchKind.Imei,
            Imsi.ExpectedLength => DeviceSearchKind.Imsi,
            _ when digits == Imei.UnknownDeviceSentinel => DeviceSearchKind.UnknownDeviceSentinel,
            _ => DeviceSearchKind.Text,
        };

        return new DeviceSearchTerm(kind, kind is DeviceSearchKind.Text ? string.Empty : digits, text);
    }

    /// <summary>
    /// The device model this term points at, when it points at one directly.
    /// </summary>
    /// <remarks>
    /// Only a TAC and an IMEI answer without touching the binding tables: a TAC <i>is</i> the
    /// model, and an IMEI's first eight digits are its model - verified against every one of the
    /// 284,341,927 well-formed rows of current state, with zero mismatches. A SIM or a number
    /// reaches a model only through a binding, which is a query and a permission check, so those
    /// return <see langword="false"/> here.
    /// </remarks>
    public bool TryGetTac([NotNullWhen(true)] out string? tac)
    {
        tac = Kind switch
        {
            DeviceSearchKind.Tac => Digits,
            DeviceSearchKind.Imei => Digits[..Tac.Length],
            _ => null,
        };

        return tac is not null;
    }

    private static int StripLength(string value) => Strip(value).Length;

    private static string Strip(string value)
    {
        Span<char> buffer = value.Length <= 64 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;

        foreach (var c in value)
        {
            if (c is >= '0' and <= '9')
            {
                buffer[length++] = c;
            }
            else if (Array.IndexOf(Noise, c) < 0)
            {
                // A character that is neither a digit nor punctuation people write in numbers.
                // The term is a name, and picking the digits out of "Galaxy A54" would be worse
                // than useless - it would search for the model whose TAC is 54.
                return string.Empty;
            }
        }

        return new string(buffer[..length]);
    }
}
