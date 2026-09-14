using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Sqm.Domain.Identifiers;

/// <summary>
/// A search term for an IMSI: either a complete one, or a prefix long enough to be a search.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why there is a minimum length, and why it is ten.</b> Every IMSI in this feed begins
/// <c>43211</c> - MCC 432, MNC 11, Iran / MCI - so the first five digits carry no information at
/// all. Measured over the 295,013,916-row current-state table:
/// </para>
/// <list type="table">
/// <item><term>3 digits</term><description>1 distinct value</description></item>
/// <item><term>5 digits</term><description>1 distinct value - the whole table</description></item>
/// <item><term>6 digits</term><description>8</description></item>
/// <item><term>7 digits</term><description>61</description></item>
/// <item><term>8 digits</term><description>86 - worst bucket 20,139,179 rows</description></item>
/// <item><term>10 digits</term><description>3,769 - worst bucket 557,158 rows</description></item>
/// <item><term>12 digits</term><description>350,712 - worst bucket 94,931, average 840</description></item>
/// </list>
/// <para>
/// So a prefix shorter than ten digits is not a search, it is a request to read a large fraction
/// of the table and page through it. Ten is the shortest length at which the result is bounded
/// enough to be useful, and the message the user gets says so rather than reporting a generic
/// validation failure.
/// </para>
/// <para>
/// A prefix becomes a numeric RANGE rather than a string operation, which is what makes it fast:
/// the IMSI is stored as a <see cref="ulong"/>, so <c>4321139917</c> is exactly
/// <c>[432113991700000, 432113991799999]</c> and the primary index of a table ordered by IMSI can
/// seek straight to it. A <c>LIKE '4321139917%'</c> would be a full scan of the same data.
/// </para>
/// </remarks>
public readonly record struct ImsiQuery
{
    /// <summary>Shortest prefix that narrows the table usefully. See the remarks for the measurement.</summary>
    public const int MinimumPrefixLength = 10;

    /// <summary>Digits shared by every IMSI in the feed, and therefore worth nothing as a filter.</summary>
    public const string ConstantPrefix = "43211";

    /// <summary>Longest input accepted, so the 16- and 17-digit anomalies stay investigable.</summary>
    public const int MaximumLength = 17;

    private ImsiQuery(string digits, ulong low, ulong high, bool isExact, bool isWellFormed)
    {
        Digits = digits;
        Low = low;
        High = high;
        IsExact = isExact;
        IsWellFormed = isWellFormed;
    }

    /// <summary>The digits as entered, with separators removed.</summary>
    public string Digits { get; }

    /// <summary>How many digits were entered.</summary>
    public int DigitCount => Digits.Length;

    /// <summary>Inclusive lower bound of the numeric range this term selects.</summary>
    public ulong Low { get; }

    /// <summary>Inclusive upper bound. Equal to <see cref="Low"/> for an exact term.</summary>
    public ulong High { get; }

    /// <summary>Whether this is a complete IMSI rather than a prefix.</summary>
    public bool IsExact { get; }

    /// <summary>
    /// Whether the term has the expected 15 digits.
    /// </summary>
    /// <remarks>
    /// 16- and 17-digit values exist in the data - 28 rows - and are accepted so an operator can
    /// investigate them. The result says the input was unusual rather than silently treating it as
    /// normal, which is the same contract the subscriber lookup already offers.
    /// </remarks>
    public bool IsWellFormed { get; }

    /// <summary>How many IMSIs the range covers. 1 for an exact term.</summary>
    public ulong RangeSize => High - Low + 1;

    /// <summary>Parses a search term, explaining the refusal when there is one.</summary>
    /// <param name="raw">What the user typed. Spaces, dashes and dots are ignored.</param>
    /// <param name="query">The parsed term.</param>
    /// <param name="problem">A message to show the user, or <see langword="null"/> on success.</param>
    public static bool TryParse(
        string? raw, [NotNullWhen(true)] out ImsiQuery? query, out string? problem)
    {
        query = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            problem = "Enter an IMSI.";
            return false;
        }

        if (!TryKeepDigits(raw, out var digits))
        {
            problem = "An IMSI is digits only.";
            return false;
        }

        if (digits.Length == 0)
        {
            problem = "Enter an IMSI.";
            return false;
        }

        if (digits.Length > MaximumLength)
        {
            problem = $"That is {digits.Length} digits. An IMSI is {Imsi.ExpectedLength}.";
            return false;
        }

        if (digits.Length < MinimumPrefixLength)
        {
            // Names the reason rather than the rule. "Minimum 10 digits" invites the reaction
            // that the limit is arbitrary; the fact that 43211 is on every single row does not.
            problem = digits.StartsWith(ConstantPrefix, StringComparison.Ordinal)
                ? $"Enter at least {MinimumPrefixLength} digits. Every IMSI here starts "
                  + $"{ConstantPrefix}, so a shorter prefix matches the whole network."
                : $"Enter at least {MinimumPrefixLength} digits to search by prefix.";
            return false;
        }

        if (!ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            problem = "That number is too large to be an IMSI.";
            return false;
        }

        var isExact = digits.Length >= Imsi.ExpectedLength;

        if (isExact)
        {
            query = new ImsiQuery(digits, value, value, isExact: true,
                isWellFormed: digits.Length == Imsi.ExpectedLength);
            problem = null;
            return true;
        }

        // Prefix to range. `4321139917` covers [432113991700000, 432113991799999]: the same digits
        // followed by every combination of the ones not given.
        var missing = Imsi.ExpectedLength - digits.Length;
        var scale = Pow10(missing);
        var low = value * scale;

        query = new ImsiQuery(digits, low, low + scale - 1, isExact: false, isWellFormed: true);
        problem = null;
        return true;
    }

    /// <summary>Keeps digits, drops the separators people paste, rejects anything else.</summary>
    private static bool TryKeepDigits(string raw, out string digits)
    {
        Span<char> buffer = stackalloc char[raw.Length];
        var length = 0;

        foreach (var c in raw)
        {
            if (c is >= '0' and <= '9')
            {
                buffer[length++] = c;
                continue;
            }

            // Everything people actually paste around an identifier. A letter is a typo, not a
            // separator, and is refused rather than quietly dropped.
            if (c is ' ' or '-' or '.' or '_' or ' ' or '\t' or '(' or ')')
            {
                continue;
            }

            digits = string.Empty;
            return false;
        }

        digits = new string(buffer[..length]);
        return true;
    }

    private static ulong Pow10(int exponent)
    {
        ulong result = 1;
        for (var i = 0; i < exponent; i++)
        {
            result *= 10;
        }

        return result;
    }

    /// <summary>The term as it would be displayed.</summary>
    public override string ToString() => IsExact ? Digits : Digits + "…";
}
