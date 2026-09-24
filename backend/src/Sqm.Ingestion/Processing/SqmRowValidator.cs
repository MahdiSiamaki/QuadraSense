using System.Globalization;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion.Processing;

/// <summary>Rule codes emitted by <see cref="SqmRowValidator"/>.</summary>
/// <remarks>
/// Stable strings, not an enum, because they are stored in the database and shown in the UI. An
/// enum's numeric value would silently change meaning if someone reordered the members.
/// </remarks>
internal static class SqmRules
{
    public const string FieldCount = "SQM_FIELD_COUNT";
    public const string MsisdnNotNumeric = "SQM_MSISDN_NOT_NUMERIC";
    public const string ImsiNotNumeric = "SQM_IMSI_NOT_NUMERIC";
    public const string LabelUnknown = "SQM_LABEL_UNKNOWN";
    public const string ImeiEmpty = "SQM_IMEI_EMPTY";
    public const string ImeiNotNumeric = "SQM_IMEI_NOT_NUMERIC";
    public const string ImeiLength = "SQM_IMEI_UNEXPECTED_LENGTH";
}

/// <summary>How a row should be treated.</summary>
internal enum RowVerdict
{
    /// <summary>Import it.</summary>
    Accept,

    /// <summary>Import it, and record why it looked odd.</summary>
    Warn,

    /// <summary>Do not import it; record it in quarantine.</summary>
    Reject,
}

/// <summary>One rule that a row broke.</summary>
internal sealed record RowFinding(string RuleCode, string? Column, RowVerdict Verdict, string? Value);

/// <summary>
/// Validates one line of an SQM daily delta file.
/// </summary>
/// <remarks>
/// <para>
/// The accept/warn/reject split follows what the data actually looks like, measured over
/// 126 million rows rather than assumed:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>000000</c> appears as the IMEI of 8,776,237 bindings. It is the source system's
///     "device unknown" sentinel, not corruption, and rejecting it would silently drop 7% of the
///     population.
///   </description></item>
///   <item><description>
///     31,209 bindings carry an IMEI that is neither 14 digits nor the sentinel. These are real
///     bindings with an unusable device identifier, so they are imported with a warning: dropping
///     them would make the totals disagree with the source, and accepting them silently would
///     hide a data-quality signal worth watching.
///   </description></item>
///   <item><description>
///     A non-numeric MSISDN or IMSI is rejected. Those columns are UInt64 in the analytics store,
///     so such a row cannot be represented at all - this is the one class the pipeline genuinely
///     cannot carry.
///   </description></item>
/// </list>
/// </remarks>
internal static class SqmRowValidator
{
    /// <summary>The column contract a daily file must match, in order.</summary>
    public static readonly string[] ExpectedColumns = ["msisdn", "imsi", "imei", "label"];

    /// <summary>The IMEI the source uses when it does not know the device.</summary>
    public const string UnknownDeviceSentinel = "000000";

    private const int WellFormedImeiLength = 14;

    /// <summary>
    /// Checks one line, appending anything it breaks to <paramref name="findings"/>.
    /// </summary>
    /// <param name="line">One data row.</param>
    /// <param name="findings">Where broken rules are appended.</param>
    /// <param name="columns">
    /// How many columns the header declared. A header the schema check accepted because it only
    /// appends columns has more than four, and every row carries them; checking rows against a
    /// fixed four rejected all of them, so an "accepted" schema change quarantined the whole file.
    /// The first four are the ones read; ClickHouse skips the rest by name.
    /// </param>
    /// <returns>The strongest verdict any rule produced.</returns>
    public static RowVerdict Validate(ReadOnlySpan<char> line, List<RowFinding> findings, int columns = 4)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 4);

        // One slot more than expected, so a row with too many fields is counted as such.
        Span<Range> fields = columns < 32 ? stackalloc Range[columns + 1] : new Range[columns + 1];
        var count = SplitFields(line, fields);

        if (count != columns)
        {
            findings.Add(new RowFinding(
                SqmRules.FieldCount, null, RowVerdict.Reject, count.ToString()));
            return RowVerdict.Reject;
        }

        var msisdn = line[fields[0]].Trim();
        var imsi = line[fields[1]].Trim();
        var imei = line[fields[2]].Trim();
        var label = line[fields[3]].Trim();

        var verdict = RowVerdict.Accept;

        // Parsed, not measured. Twenty digits was the only bound, and a UInt64 holds nineteen and
        // a bit: 99999999999999999999 passed, and ClickHouse stored it wrapped, as
        // 7766279631452241919 - a number that exists nowhere, without an error.
        if (msisdn.IsEmpty || !IsAllDigits(msisdn) || !FitsUInt64(msisdn))
        {
            findings.Add(new RowFinding(
                SqmRules.MsisdnNotNumeric, "msisdn", RowVerdict.Reject, msisdn.ToString()));
            verdict = RowVerdict.Reject;
        }

        if (imsi.IsEmpty || !IsAllDigits(imsi) || !FitsUInt64(imsi))
        {
            findings.Add(new RowFinding(
                SqmRules.ImsiNotNumeric, "imsi", RowVerdict.Reject, imsi.ToString()));
            verdict = RowVerdict.Reject;
        }

        if (!label.Equals("add", StringComparison.Ordinal)
            && !label.Equals("remove", StringComparison.Ordinal))
        {
            findings.Add(new RowFinding(
                SqmRules.LabelUnknown, "label", RowVerdict.Reject, label.ToString()));
            verdict = RowVerdict.Reject;
        }

        if (imei.IsEmpty)
        {
            findings.Add(new RowFinding(SqmRules.ImeiEmpty, "imei", RowVerdict.Warn, ""));
            verdict = Strongest(verdict, RowVerdict.Warn);
        }
        else if (!IsAllDigits(imei))
        {
            findings.Add(new RowFinding(
                SqmRules.ImeiNotNumeric, "imei", RowVerdict.Warn, imei.ToString()));
            verdict = Strongest(verdict, RowVerdict.Warn);
        }
        else if (imei.Length != WellFormedImeiLength
                 && !imei.Equals(UnknownDeviceSentinel, StringComparison.Ordinal))
        {
            findings.Add(new RowFinding(
                SqmRules.ImeiLength, "imei", RowVerdict.Warn, imei.ToString()));
            verdict = Strongest(verdict, RowVerdict.Warn);
        }

        return verdict;
    }

    /// <summary>Human-readable text for a rule, shown next to the count in the UI.</summary>
    public static string DescribeRule(string ruleCode) => ruleCode switch
    {
        SqmRules.FieldCount => "The row did not have exactly four comma-separated fields.",
        SqmRules.MsisdnNotNumeric => "The subscriber number was empty or not a plain number.",
        SqmRules.ImsiNotNumeric => "The SIM identifier was empty or not a plain number.",
        SqmRules.LabelUnknown => "The change label was neither 'add' nor 'remove'.",
        SqmRules.ImeiEmpty => "The device identifier was empty. The row was imported.",
        SqmRules.ImeiNotNumeric => "The device identifier contained non-digits. The row was imported.",
        SqmRules.ImeiLength =>
            "The device identifier was neither 14 digits nor the '000000' unknown-device "
            + "sentinel. The row was imported.",
        _ => ruleCode,
    };

    /// <summary>
    /// Splits a line on commas.
    /// </summary>
    /// <remarks>
    /// Deliberately not a general CSV parser. These files carry four numeric-ish fields with no
    /// quoting, no embedded commas and no escapes - verified across every delivered file - so a
    /// quoted-field parser would add cost per row to handle cases the format does not contain.
    /// A row that does have extra commas fails the field-count rule and lands in quarantine,
    /// which is the correct outcome for a file that broke its own contract.
    /// </remarks>
    private static int SplitFields(ReadOnlySpan<char> line, Span<Range> fields)
    {
        var count = 0;
        var start = 0;

        for (var i = 0; i < line.Length && count < fields.Length; i++)
        {
            if (line[i] == ',')
            {
                fields[count++] = new Range(start, i);
                start = i + 1;
            }
        }

        if (count < fields.Length)
        {
            fields[count++] = new Range(start, line.Length);
        }

        return count;
    }

    private static bool FitsUInt64(ReadOnlySpan<char> digits) =>
        ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static bool IsAllDigits(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static RowVerdict Strongest(RowVerdict a, RowVerdict b) => (RowVerdict)Math.Max((int)a, (int)b);

    /// <summary>Severity label stored alongside a quarantine group.</summary>
    public static string SeverityFor(RowVerdict verdict) =>
        verdict == RowVerdict.Reject ? "error" : "warning";
}
