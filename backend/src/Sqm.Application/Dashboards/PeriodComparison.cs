using Sqm.Contracts.Dashboard;

namespace Sqm.Application.Dashboards;

/// <summary>One period's figure.</summary>
/// <param name="From">First day, inclusive.</param>
/// <param name="To">Last day, inclusive.</param>
/// <param name="CalendarDays">Days from <paramref name="From"/> to <paramref name="To"/>.</param>
/// <param name="DeliveredDays">Days in the period with a row.</param>
/// <param name="Value">The figure; null when no day in the period was delivered.</param>
public sealed record PeriodFigure(DateOnly From, DateOnly To, int CalendarDays, int DeliveredDays, double? Value);

/// <summary>A period against its baseline.</summary>
/// <param name="Current">The dashboard's range.</param>
/// <param name="Baseline">The baseline, or null when none could be formed.</param>
/// <param name="Absolute">Current minus baseline.</param>
/// <param name="Percent">The change over the baseline's size; null from zero.</param>
/// <param name="Unavailable">Why there is nothing to compare with; null when there is.</param>
/// <param name="Uneven">A total over periods with different numbers of delivered days.</param>
public sealed record PeriodComparisonResult(
    PeriodFigure Current, PeriodFigure? Baseline, double? Absolute, double? Percent, string? Unavailable, bool Uneven);

/// <summary>
/// The period-comparison widget's arithmetic, for its export - the same rules as
/// <c>widgets/change/periodComparison.ts</c>, which the card draws with, so a file never says what
/// the card does not.
/// </summary>
/// <remarks>
/// Flows (added, removed, net, SIM and handset changes) are summed over delivered days, or averaged
/// per delivered day. <c>population</c> is a level: active bindings on the last delivered day, or their
/// average. A missing day is never filled in: a total over periods with different delivered-day counts
/// is flagged instead.
/// </remarks>
public static class PeriodComparison
{
    /// <summary>The metrics read from the churn series rather than the change series.</summary>
    public static bool UsesChurn(string metric) => metric is "simChanges" or "deviceChanges";

    /// <summary>The same number of calendar days, ending the day before <paramref name="from"/>.</summary>
    public static (DateOnly From, DateOnly To) PreviousWindow(DateOnly from, DateOnly to)
    {
        var days = to.DayNumber - from.DayNumber + 1;
        return (from.AddDays(-days), from.AddDays(-1));
    }

    /// <summary>One period's figure from dated values: (date, value, cumulative) rows.</summary>
    public static PeriodFigure Figure(
        IReadOnlyList<(DateOnly Date, double Value)> rows, DateOnly from, DateOnly to, bool level, bool average)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var days = rows.Where(r => r.Date >= from && r.Date <= to).OrderBy(r => r.Date).ToList();
        var calendar = to.DayNumber - from.DayNumber + 1;
        if (days.Count == 0)
        {
            return new PeriodFigure(from, to, calendar, 0, null);
        }

        var sum = days.Sum(r => r.Value);
        double value = level && !average ? days[^1].Value : average ? sum / days.Count : sum;
        return new PeriodFigure(from, to, calendar, days.Count, value);
    }

    /// <summary>The values a metric reads, by day.</summary>
    public static IReadOnlyList<(DateOnly Date, double Value)> Series(
        string metric, IReadOnlyList<DailyChange> changes, IReadOnlyList<DailyChurn> churn)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(churn);

        return metric switch
        {
            "added" => [.. changes.Select(c => (c.Date, (double)c.Added))],
            "removed" => [.. changes.Select(c => (c.Date, (double)c.Removed))],
            "net" => [.. changes.Select(c => (c.Date, (double)c.Net))],
            "population" => [.. changes.Select(c => (c.Date, (double)c.Cumulative))],
            "simChanges" => [.. churn.Select(c => (c.Date, (double)c.SimChanges))],
            "deviceChanges" => [.. churn.Select(c => (c.Date, (double)c.DeviceChanges))],
            _ => [],
        };
    }

    /// <summary>The comparison the widget draws.</summary>
    /// <param name="metric">The config's metric.</param>
    /// <param name="aggregate">total or dailyAverage.</param>
    /// <param name="baseline">previous or custom.</param>
    /// <param name="baselineFrom">A custom baseline's first day.</param>
    /// <param name="baselineTo">A custom baseline's last day.</param>
    /// <param name="from">The dashboard's range, first day.</param>
    /// <param name="to">The dashboard's range, last day.</param>
    /// <param name="rows">The metric's series.</param>
    /// <param name="dataStart">The first delivered day.</param>
    public static PeriodComparisonResult Compare(
        string metric, string aggregate, string baseline, DateOnly? baselineFrom, DateOnly? baselineTo,
        DateOnly from, DateOnly to, IReadOnlyList<(DateOnly Date, double Value)> rows, DateOnly? dataStart)
    {
        var level = metric == "population";
        var average = aggregate == "dailyAverage";
        var current = Figure(rows, from, to, level, average);

        PeriodComparisonResult None(string why, PeriodFigure? b = null) => new(current, b, null, null, why, false);

        DateOnly bFrom, bTo;
        if (baseline == "custom")
        {
            if (baselineFrom is null || baselineTo is null)
            {
                return None("Set both baseline dates in the widget’s settings.");
            }

            if (baselineFrom > baselineTo)
            {
                return None("The baseline starts after it ends.");
            }

            (bFrom, bTo) = (baselineFrom.Value, baselineTo.Value);
        }
        else
        {
            (bFrom, bTo) = PreviousWindow(from, to);
        }

        if (dataStart is { } start && bTo < start)
        {
            return None($"The baseline ({Iso(bFrom)} to {Iso(bTo)}) is before the first daily file, {Iso(start)}.");
        }

        var b = Figure(rows, bFrom, bTo, level, average);
        if (b.Value is not { } baseValue)
        {
            return None($"No day from {Iso(bFrom)} to {Iso(bTo)} was delivered.", b);
        }

        if (current.Value is not { } currentValue)
        {
            return None("No day in the dashboard’s range was delivered.", b);
        }

        var absolute = currentValue - baseValue;
        double? percent = baseValue == 0 ? null : absolute / Math.Abs(baseValue) * 100;
        var uneven = !average && !level && current.DeliveredDays != b.DeliveredDays;
        return new PeriodComparisonResult(current, b, absolute, percent, null, uneven);
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
