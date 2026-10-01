using System.Globalization;

namespace Sqm.Domain.Quality;

/// <summary>What one day's file looked like: counts, measured when the day was imported.</summary>
/// <param name="Date">The business date.</param>
/// <param name="Rows">Rows in the day.</param>
/// <param name="Sims">Distinct IMSIs in the day.</param>
/// <param name="UnknownDeviceRows">Rows with the <c>000000</c> sentinel.</param>
/// <param name="MalformedImeiRows">Rows whose IMEI is neither 14 digits nor the sentinel.</param>
/// <param name="UnknownTacRows">14-digit IMEIs whose TAC is not in the active GSMA version.</param>
/// <param name="ShiftedImeiRows">
/// Of those, IMEIs ending in 0 that one leading digit turns back into a GSMA TAC.
/// </param>
/// <param name="MultiNumberSims">SIMs with two or more phone numbers on the day.</param>
/// <param name="MultiNumberSimRows">Rows those SIMs carry.</param>
/// <param name="TacVersionId">The GSMA version the TAC counts were judged against.</param>
public sealed record FeedQualityDay(
    DateOnly Date,
    long Rows,
    long Sims,
    long UnknownDeviceRows,
    long MalformedImeiRows,
    long UnknownTacRows,
    long ShiftedImeiRows,
    long MultiNumberSims,
    long MultiNumberSimRows,
    int TacVersionId);

/// <summary>A property of a day's file that is compared with other days.</summary>
public enum FeedQualityCheck
{
    /// <summary>IMEIs with the first digit dropped and a 0 added: <c>5004012…0</c> for <c>35004012…</c>.</summary>
    ShiftedImei,

    /// <summary>SIMs carrying more than one phone number on the same day.</summary>
    MultiNumberSim,

    /// <summary>IMEIs that are neither 14 digits nor the unknown-device sentinel.</summary>
    MalformedImei,

    /// <summary>The <c>000000</c> sentinel: the network did not report the device.</summary>
    UnknownDevice,
}

/// <summary>What the reference days say is ordinary: the median of each check's daily rate.</summary>
/// <param name="Days">How many reference days there were.</param>
/// <param name="Median">Each check's median daily rate over those days.</param>
public sealed record FeedQualityReference(int Days, IReadOnlyDictionary<FeedQualityCheck, double> Median);

/// <summary>One check of one day, and whether it is out of line.</summary>
/// <param name="Check">Which property.</param>
/// <param name="Rate">The day's rate, 0 to 1.</param>
/// <param name="Threshold">The rate above which the day is flagged; null when there was nothing to judge by.</param>
/// <param name="ReferenceMedian">The reference days' median rate, null without reference days.</param>
/// <param name="Flagged">True when the rate is above the threshold.</param>
/// <param name="Explanation">A sentence saying what was measured, against what.</param>
public sealed record FeedQualityFinding(
    FeedQualityCheck Check,
    double Rate,
    double? Threshold,
    double? ReferenceMedian,
    bool Flagged,
    string Explanation);

/// <summary>
/// How a day's file is judged: against the days known to be ordinary, never against a fixed number.
/// </summary>
/// <remarks>
/// <para>
/// A day is flagged on a check when its rate is above <c>multiplier</c> times the reference days'
/// MEDIAN rate, and above <c>minimumRate</c> - the floor that keeps a check whose ordinary rate is
/// all but zero from flagging one stray row. Both come from configuration.
/// </para>
/// <para>
/// The median, not a high percentile, and that was measured rather than chosen. The first version
/// used the reference window's 99th percentile, and against the real history it missed most of what
/// it exists for: the window (2026-01-26 to 2026-07-26) turned out to hold episodes of its own -
/// malformed IMEIs at 4.6-4.8% on 2 and 3 May, multi-number SIMs at 0.2-0.39% from 14 April to
/// 7 May, the 000000 code on 10-13% of rows until late February - and the 99th percentile is
/// exactly where those days sit. It missed 27 July to 15 August for multi-number SIMs and
/// 25-26 August for malformed IMEIs. A minority of odd days cannot move a median.
/// </para>
/// <para>
/// At five times the median, over all 233 days: shifted IMEIs flag 15-26 September and nothing
/// else, at three times or ten alike; multi-number SIMs flag 28 July to 26 September and the April-May
/// episode; malformed IMEIs flag 2-3 May and 25-26 August only. At three times, ordinary days in early
/// September start to flag for malformed IMEIs. See docs/architecture/15-feed-quality.md.
/// </para>
/// </remarks>
public static class FeedQualityRules
{
    /// <summary>Every check, in the order they are reported.</summary>
    public static IReadOnlyList<FeedQualityCheck> Checks { get; } =
    [
        FeedQualityCheck.ShiftedImei,
        FeedQualityCheck.MultiNumberSim,
        FeedQualityCheck.MalformedImei,
        FeedQualityCheck.UnknownDevice,
    ];

    /// <summary>A check's rate for one day, 0 to 1. Zero for an empty day rather than a division by zero.</summary>
    public static double RateOf(FeedQualityDay day, FeedQualityCheck check)
    {
        ArgumentNullException.ThrowIfNull(day);

        return check switch
        {
            FeedQualityCheck.ShiftedImei => Ratio(day.ShiftedImeiRows, day.Rows),
            FeedQualityCheck.MultiNumberSim => Ratio(day.MultiNumberSims, day.Sims),
            FeedQualityCheck.MalformedImei => Ratio(day.MalformedImeiRows, day.Rows),
            FeedQualityCheck.UnknownDevice => Ratio(day.UnknownDeviceRows, day.Rows),
            _ => throw new ArgumentOutOfRangeException(nameof(check), check, "Unknown check."),
        };
    }

    /// <summary>What the reference days say is ordinary.</summary>
    public static FeedQualityReference Reference(IEnumerable<FeedQualityDay> referenceDays)
    {
        ArgumentNullException.ThrowIfNull(referenceDays);
        var days = referenceDays.ToList();

        return new FeedQualityReference(
            days.Count,
            Checks.ToDictionary(c => c, c => Median([.. days.Select(d => RateOf(d, c))])));
    }

    /// <summary>Judges one day against the reference.</summary>
    /// <param name="day">The day.</param>
    /// <param name="reference">What is ordinary.</param>
    /// <param name="multiplier">How many times the ordinary rate is out of line.</param>
    /// <param name="minimumRate">The lowest rate that can be out of line at all.</param>
    public static IReadOnlyList<FeedQualityFinding> Assess(
        FeedQualityDay day, FeedQualityReference reference, double multiplier, double minimumRate)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(reference);

        return [.. Checks.Select(check =>
        {
            var rate = RateOf(day, check);

            if (reference.Days == 0)
            {
                return new FeedQualityFinding(check, rate, null, null, false,
                    Describe(day, check, rate) + " There are no reference days to judge it against.");
            }

            var median = reference.Median[check];
            var threshold = Math.Max(median * multiplier, minimumRate);
            var flagged = rate > threshold;

            var ordinary = median > 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $" Ordinary days are around {Percent(median, 3)} (median of {reference.Days} reference days)")
                : string.Create(CultureInfo.InvariantCulture,
                    $" Ordinary days show none (median of {reference.Days} reference days)");

            var verdict = (flagged, median > 0) switch
            {
                (false, _) => "; this is within the ordinary range.",
                (true, true) => string.Create(CultureInfo.InvariantCulture, $"; this is {rate / median:N0} times that."),
                (true, false) => string.Create(CultureInfo.InvariantCulture, $"; this is above the {Percent(minimumRate, 3)} floor."),
            };

            return new FeedQualityFinding(
                check, rate, threshold, median, flagged, Describe(day, check, rate) + ordinary + verdict);
        })];
    }

    private static string Describe(FeedQualityDay day, FeedQualityCheck check, double rate) => check switch
    {
        FeedQualityCheck.ShiftedImei => string.Create(CultureInfo.InvariantCulture,
            $"{Percent(rate, 2)} of rows ({day.ShiftedImeiRows:N0}) carry an IMEI that looks shifted by one digit: first digit dropped, a 0 added at the end."),
        FeedQualityCheck.MultiNumberSim => string.Create(CultureInfo.InvariantCulture,
            $"{Percent(rate, 2)} of SIMs ({day.MultiNumberSims:N0}) carried more than one phone number on this day."),
        FeedQualityCheck.MalformedImei => string.Create(CultureInfo.InvariantCulture,
            $"{Percent(rate, 2)} of rows ({day.MalformedImeiRows:N0}) carry an IMEI that is neither 14 digits nor the unknown-device code."),
        FeedQualityCheck.UnknownDevice => string.Create(CultureInfo.InvariantCulture,
            $"{Percent(rate, 2)} of rows ({day.UnknownDeviceRows:N0}) carry the unknown-device code 000000."),
        _ => throw new ArgumentOutOfRangeException(nameof(check), check, "Unknown check."),
    };

    /// <summary>A rate as a percentage, "24.62%" - no space, whatever the culture.</summary>
    private static string Percent(double rate, int decimals) =>
        (rate * 100).ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + "%";

    private static double Ratio(long part, long whole) => whole <= 0 ? 0 : (double)part / whole;

    /// <summary>The median; zero for no values.</summary>
    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
