using Sqm.Application.Dashboards;
using Sqm.Contracts.Dashboard;

namespace Sqm.Application.Tests.Dashboards;

/// <summary>
/// The export's period comparison against the same cases as the card's
/// (<c>frontend/tests/unit/features/dashboard/widgets/periodComparison.spec.ts</c>): a file must not
/// say what the card does not.
/// </summary>
public sealed class PeriodComparisonTests
{
    private static DailyChange Day(string date, long added, long removed, long cumulative) =>
        new(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), added, removed, added - removed, cumulative, 0);

    /// <summary>2026-03-01 .. 2026-03-08, every day delivered except 03-06.</summary>
    private static readonly IReadOnlyList<DailyChange> Changes =
    [
        Day("2026-03-01", 10, 4, 1006),
        Day("2026-03-02", 20, 5, 1021),
        Day("2026-03-03", 30, 6, 1045),
        Day("2026-03-04", 40, 7, 1078),
        Day("2026-03-05", 50, 8, 1120),
        Day("2026-03-07", 70, 10, 1180),
        Day("2026-03-08", 80, 11, 1249),
    ];

    private static DateOnly D(string iso) => DateOnly.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    private static IReadOnlyList<(DateOnly, double)> Series(string metric) => PeriodComparison.Series(metric, Changes, []);

    [Fact]
    public void The_previous_period_is_the_same_number_of_calendar_days_ending_the_day_before()
    {
        Assert.Equal((D("2026-03-01"), D("2026-03-04")), PeriodComparison.PreviousWindow(D("2026-03-05"), D("2026-03-08")));
        Assert.Equal((D("2026-02-27"), D("2026-02-28")), PeriodComparison.PreviousWindow(D("2026-03-01"), D("2026-03-02")));
    }

    [Fact]
    public void A_flow_is_summed_over_delivered_days_and_a_missing_day_is_never_filled_in()
    {
        var f = PeriodComparison.Figure(Series("added"), D("2026-03-05"), D("2026-03-08"), level: false, average: false);
        Assert.Equal(4, f.CalendarDays);
        Assert.Equal(3, f.DeliveredDays);
        Assert.Equal(200, f.Value);
    }

    [Fact]
    public void An_average_is_per_delivered_day_not_per_calendar_day()
    {
        var f = PeriodComparison.Figure(Series("added"), D("2026-03-05"), D("2026-03-08"), level: false, average: true);
        Assert.Equal(200.0 / 3, f.Value);
    }

    [Fact]
    public void Population_is_a_level_the_last_delivered_day_or_the_mean()
    {
        Assert.Equal(1249, PeriodComparison.Figure(Series("population"), D("2026-03-05"), D("2026-03-08"), level: true, average: false).Value);
        Assert.Equal((1120 + 1180 + 1249) / 3.0, PeriodComparison.Figure(Series("population"), D("2026-03-05"), D("2026-03-08"), level: true, average: true).Value);
    }

    [Fact]
    public void Against_the_previous_period_a_total_over_unequal_delivered_days_is_flagged()
    {
        var c = PeriodComparison.Compare("added", "total", "previous", null, null, D("2026-03-05"), D("2026-03-08"), Series("added"), D("2026-03-01"));
        Assert.Null(c.Unavailable);
        Assert.Equal(100, c.Baseline!.Value);
        Assert.Equal(200, c.Current.Value);
        Assert.Equal(100, c.Absolute);
        Assert.Equal(100, c.Percent);
        Assert.True(c.Uneven);

        Assert.False(PeriodComparison.Compare("added", "dailyAverage", "previous", null, null, D("2026-03-05"), D("2026-03-08"), Series("added"), null).Uneven);
        Assert.False(PeriodComparison.Compare("population", "total", "previous", null, null, D("2026-03-05"), D("2026-03-08"), Series("population"), null).Uneven);
    }

    [Fact]
    public void A_baseline_before_the_first_daily_file_is_unavailable_not_zero()
    {
        var c = PeriodComparison.Compare("added", "total", "previous", null, null, D("2026-03-01"), D("2026-03-04"), Series("added"), D("2026-03-01"));
        Assert.Contains("before the first daily file", c.Unavailable, StringComparison.Ordinal);
        Assert.Null(c.Absolute);
    }

    [Fact]
    public void A_custom_baseline_needs_both_dates_in_order()
    {
        Assert.Contains("both", PeriodComparison.Compare("added", "total", "custom", D("2026-03-01"), null, D("2026-03-05"), D("2026-03-08"), Series("added"), null).Unavailable, StringComparison.Ordinal);
        Assert.Contains("starts after it ends", PeriodComparison.Compare("added", "total", "custom", D("2026-03-04"), D("2026-03-01"), D("2026-03-05"), D("2026-03-08"), Series("added"), null).Unavailable, StringComparison.Ordinal);
        Assert.Equal(30, PeriodComparison.Compare("added", "total", "custom", D("2026-03-01"), D("2026-03-02"), D("2026-03-05"), D("2026-03-08"), Series("added"), null).Baseline!.Value);
    }

    [Fact]
    public void A_change_from_zero_has_no_percentage_and_a_smaller_loss_reads_as_a_rise()
    {
        IReadOnlyList<DailyChange> zero = [Day("2026-03-01", 0, 0, 0), Day("2026-03-02", 5, 0, 5)];
        var c = PeriodComparison.Compare("added", "total", "previous", null, null, D("2026-03-02"), D("2026-03-02"), PeriodComparison.Series("added", zero, []), D("2026-03-01"));
        Assert.Equal(5, c.Absolute);
        Assert.Null(c.Percent);

        IReadOnlyList<DailyChange> loss = [Day("2026-03-01", 0, 10, 990), Day("2026-03-02", 0, 5, 985)];
        var n = PeriodComparison.Compare("net", "total", "previous", null, null, D("2026-03-02"), D("2026-03-02"), PeriodComparison.Series("net", loss, []), D("2026-03-01"));
        Assert.Equal(5, n.Absolute);
        Assert.Equal(50, n.Percent);
    }

    [Fact]
    public void SIM_and_handset_changes_read_the_churn_series()
    {
        IReadOnlyList<DailyChurn> churn =
        [
            new(D("2026-03-01"), 1, 10), new(D("2026-03-02"), 2, 20), new(D("2026-03-03"), 3, 30), new(D("2026-03-04"), 4, 40),
        ];
        Assert.True(PeriodComparison.UsesChurn("deviceChanges"));
        var c = PeriodComparison.Compare("deviceChanges", "total", "previous", null, null, D("2026-03-03"), D("2026-03-04"),
            PeriodComparison.Series("deviceChanges", [], churn), D("2026-03-01"));
        Assert.Equal(30, c.Baseline!.Value);
        Assert.Equal(70, c.Current.Value);
    }
}
