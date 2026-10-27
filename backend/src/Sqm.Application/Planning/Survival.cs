namespace Sqm.Application.Planning;

/// <summary>
/// Kaplan-Meier estimates over handset spells: of the spells that began together, the share that had
/// not yet ended in the event after so many days.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not a plain share.</b> A spell still open at the run's delivery, or one that ended with no
/// successor, has not changed - but it was not followed to the horizon either. Counting it as "did not
/// change" would understate every rate, the more so for recent cohorts; dropping it would overstate
/// them. Kaplan-Meier keeps each spell at risk for exactly as long as it was observed (censoring).
/// </para>
/// <para>
/// <b>Competing outcomes are censored.</b> For time to an upgrade, a change to an older model ends the
/// spell without the event: it is censored there, so the figure reads "had upgraded, had it not
/// changed otherwise" - the cause-specific estimate, stated as such on the page.
/// </para>
/// </remarks>
public static class Survival
{
    /// <summary>Spells that lasted the same number of days and ended the same way.</summary>
    /// <param name="Days">How long they lasted, or were observed for.</param>
    /// <param name="Event">True when they ended in the event; false when censored there.</param>
    /// <param name="Count">How many.</param>
    public readonly record struct Spells(int Days, bool Event, long Count);

    /// <summary>
    /// The share of spells that had ended in the event by each horizon, 0-1 - or null for a horizon no
    /// spell was followed to, which the data cannot speak for.
    /// </summary>
    public static IReadOnlyList<double?> EventShareBy(IEnumerable<Spells> spells, IReadOnlyList<int> horizons)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(horizons);

        var steps = Steps(spells);
        var followed = steps.Count == 0 ? -1 : steps[^1].Day;
        return [.. horizons.Select(h => h > followed ? (double?)null : 1 - StillAt(steps, h))];
    }

    /// <summary>The first day by which half the spells had ended in the event, or null if they never did.</summary>
    public static int? MedianDays(IEnumerable<Spells> spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        foreach (var step in Steps(spells))
        {
            if (step.Still <= 0.5)
            {
                return step.Day;
            }
        }

        return null;
    }

    private static double StillAt(List<(int Day, double Still)> steps, int horizon)
    {
        var still = 1.0;
        foreach (var (day, value) in steps)
        {
            if (day > horizon)
            {
                break;
            }

            still = value;
        }

        return still;
    }

    /// <summary>The estimate after each day on which a spell ended or was censored, in day order.</summary>
    private static List<(int Day, double Still)> Steps(IEnumerable<Spells> spells)
    {
        var byDay = spells
            .Where(s => s.Count > 0)
            .GroupBy(s => Math.Max(0, s.Days))
            .Select(g => (Day: g.Key, Events: g.Where(s => s.Event).Sum(s => s.Count), Censored: g.Where(s => !s.Event).Sum(s => s.Count)))
            .OrderBy(g => g.Day)
            .ToList();

        // At risk on a day: every spell that lasted at least that long. A spell censored on a day was
        // at risk on it, so the day's events come off before its censored spells do.
        var atRisk = byDay.Sum(d => d.Events + d.Censored);
        var still = 1.0;
        var steps = new List<(int Day, double Still)>(byDay.Count);
        foreach (var (day, events, censored) in byDay)
        {
            if (atRisk > 0 && events > 0)
            {
                still *= 1 - ((double)events / atRisk);
            }

            steps.Add((day, still));
            atRisk -= events + censored;
        }

        return steps;
    }
}
