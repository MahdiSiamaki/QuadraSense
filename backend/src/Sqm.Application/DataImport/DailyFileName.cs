using System.Globalization;
using System.Text.RegularExpressions;

namespace Sqm.Application.DataImport;

/// <summary>
/// Reads the business date out of a daily file's name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two callers, one rule, and they must not drift.</b> The processor needs the date to decide
/// which day it is importing. The upload endpoint needs it earlier, at enqueue time, for two
/// things that happen before any worker touches the job: the queue will not run a day until every
/// earlier day for its source is in, and that ordering can only be enforced against a date the
/// job already carries.
/// </para>
/// <para>
/// It is a <b>hint</b>, not the authority. The processor re-derives the date and
/// <c>CompleteAsync</c> records what it actually imported, so a file whose name disagrees with
/// its contents is corrected rather than believed. What the hint buys is that the job is in the
/// right place in the queue before anyone has read a byte of it.
/// </para>
/// <para>
/// The date lives in the name because that is where the source puts it - the daily deliveries
/// arrive as <c>daily_subs_device_sim_info_2026-06-15.csv</c>. The initial dump carries a range
/// rather than a day (<c>dump_subs_device_sim_info_20251227_20260125.csv</c>) and deliberately
/// yields nothing here: it is not one day, and a job with no business date does not take part in
/// the ordering at all.
/// </para>
/// </remarks>
public static partial class DailyFileName
{
    /// <summary>
    /// An ISO date anywhere in the name.
    /// </summary>
    /// <remarks>
    /// A timeout, because this runs on a name that arrives from outside. 200 ms is thousands of
    /// times what this pattern needs and still bounds a pathological input.
    /// </remarks>
    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2})", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex IsoDate { get; }

    /// <summary>
    /// The day a file name claims to describe, or <see langword="null"/> when it claims none.
    /// </summary>
    /// <param name="fileName">The name as uploaded.</param>
    /// <returns>The date, or null.</returns>
    public static DateOnly? BusinessDateOf(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var match = IsoDate.Match(fileName);

        return match.Success
            && DateOnly.TryParseExact(
                match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}
