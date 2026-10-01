using Sqm.Domain.Bindings;
using Sqm.Domain.Timeline;

namespace Sqm.Domain.Tests;

/// <summary>
/// A binding's lifetime is what its events say, applied in date order with the same rules as the
/// fold - including the initial dump's window, which is not a date.
/// </summary>
public sealed class BindingLifetimeTests
{
    private static readonly DateOnly D1 = new(2026, 3, 1);
    private static readonly DateOnly D2 = new(2026, 4, 10);
    private static readonly DateOnly D3 = new(2026, 6, 5);

    private static BindingEvent Add(DateOnly d, int seq = 0) => new(d, seq == 0 ? d.DayNumber : seq, ChangeLabel.Add);

    private static BindingEvent Remove(DateOnly d, int seq = 0) => new(d, seq == 0 ? d.DayNumber : seq, ChangeLabel.Remove);

    [Fact]
    public void A_dump_binding_no_file_ever_mentioned_is_open_from_the_window_and_has_no_last_change()
    {
        var life = BindingLifetime.Derive(inDump: true, []);

        var period = Assert.Single(life.Periods);
        Assert.Equal((InitialDump.WindowStart, true, (DateOnly?)null), (period.Start, period.StartsInDump, period.End));
        Assert.Equal((InitialDump.WindowStart, true, (DateOnly?)null), (life.FirstSeen, life.FirstSeenIsDumpWindow, life.LastChange));
        Assert.Equal(BindingState.Active, life.State);
    }

    [Fact]
    public void Removed_then_added_again_is_two_periods()
    {
        var life = BindingLifetime.Derive(inDump: true, [Remove(D1), Add(D2)]);

        Assert.Equal(
            [new LifetimePeriod(InitialDump.WindowStart, true, D1), new LifetimePeriod(D2, false, null)],
            life.Periods);
        Assert.Equal(D2, life.LastChange);
        Assert.Equal(BindingState.Active, life.State);
    }

    [Fact]
    public void A_binding_added_and_removed_by_the_feed_starts_on_its_add()
    {
        var life = BindingLifetime.Derive(inDump: false, [Add(D1), Remove(D2)]);

        Assert.Equal([new LifetimePeriod(D1, false, D2)], life.Periods);
        Assert.Equal((D1, false), (life.FirstSeen, life.FirstSeenIsDumpWindow));
        Assert.Equal(BindingState.Inactive, life.State);
    }

    /// <summary>The month-union dump: the first daily file "adds" what the dump already held.</summary>
    [Fact]
    public void An_add_of_a_binding_already_held_is_counted_and_does_not_restart_it()
    {
        var life = BindingLifetime.Derive(inDump: true, [Add(D1)]);

        Assert.Equal([new LifetimePeriod(InitialDump.WindowStart, true, null)], life.Periods);
        Assert.Equal(1, life.RedundantAdds);
        Assert.Equal([FoldOutcome.RedundantAdd], life.Outcomes);
    }

    [Fact]
    public void A_remove_of_a_binding_never_held_is_counted_and_makes_no_period()
    {
        var life = BindingLifetime.Derive(inDump: false, [Remove(D1)]);

        Assert.Empty(life.Periods);
        Assert.Equal(1, life.OrphanRemoves);
        Assert.Equal([FoldOutcome.OrphanRemove], life.Outcomes);
        Assert.Equal((D1, D1), (life.FirstSeen!.Value, life.LastChange!.Value));
        Assert.Equal(BindingState.Unknown, life.State);
    }

    /// <summary>
    /// A missing day imported late takes the next sequence. By sequence this binding would end
    /// removed; by date - what happened - it was removed and then added again.
    /// </summary>
    [Fact]
    public void Events_apply_by_date_not_by_sequence()
    {
        var life = BindingLifetime.Derive(inDump: true, [Add(D3, seq: 100), Remove(D2, seq: 200)]);

        Assert.Equal(
            [new LifetimePeriod(InitialDump.WindowStart, true, D2), new LifetimePeriod(D3, false, null)],
            life.Periods);
        Assert.Equal(BindingState.Active, life.State);
        Assert.Equal([D2, D3], life.Events.Select(e => e.Date));
    }

    [Fact]
    public void Two_events_on_one_date_are_counted_and_applied_add_first()
    {
        var life = BindingLifetime.Derive(inDump: false, [Remove(D1, seq: 7), Add(D1, seq: 7)]);

        Assert.Equal(1, life.SameDayConflicts);
        Assert.Equal([new LifetimePeriod(D1, false, D1)], life.Periods);
    }

    /// <summary>Whatever the history, the lifetime ends where the fold ends.</summary>
    [Fact]
    public void The_final_state_is_always_the_folds()
    {
        var random = new Random(20261001);

        for (var run = 0; run < 2_000; run++)
        {
            var inDump = random.Next(2) == 0;
            var events = Enumerable.Range(0, random.Next(0, 12))
                .Select(i => new BindingEvent(
                    new DateOnly(2026, 1, 26).AddDays(random.Next(0, 240)),
                    random.Next(1, 300),
                    random.Next(2) == 0 ? ChangeLabel.Add : ChangeLabel.Remove))
                .GroupBy(e => e.Date).Select(g => g.First()) // one event a day, as measured
                .ToList();

            var life = BindingLifetime.Derive(inDump, events);
            var folded = BindingFold.FoldAll(
                inDump ? BindingState.Active : BindingState.Unknown,
                events.OrderBy(e => e.Date).Select(e => e.Label));

            Assert.Equal(folded, life.State);
            Assert.Equal(life.State == BindingState.Active, life.Periods.Count > 0 && life.Periods[^1].IsOpen);
            Assert.All(life.Periods.Zip(life.Periods.Skip(1)), pair => Assert.True(pair.First.End <= pair.Second.Start));
        }
    }

    [Fact]
    public void A_union_joins_overlapping_and_same_day_periods_and_keeps_gaps()
    {
        var union = LifetimePeriods.Union(
        [
            new LifetimePeriod(D2, false, D3),
            new LifetimePeriod(InitialDump.WindowStart, true, D1),
            new LifetimePeriod(D1, false, D2.AddDays(-1)),          // starts the day the first ends
            new LifetimePeriod(D3.AddDays(10), false, null),        // a gap, then still held
        ]);

        Assert.Equal(
            [
                new LifetimePeriod(InitialDump.WindowStart, true, D2.AddDays(-1)),
                new LifetimePeriod(D2, false, D3),
                new LifetimePeriod(D3.AddDays(10), false, null),
            ],
            union);
    }

    [Fact]
    public void An_open_period_absorbs_everything_after_it_starts()
    {
        var union = LifetimePeriods.Union(
        [
            new LifetimePeriod(D1, false, null),
            new LifetimePeriod(D2, false, D3),
        ]);

        Assert.Equal([new LifetimePeriod(D1, false, null)], union);
    }
}
