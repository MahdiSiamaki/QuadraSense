using Sqm.Application.Timeline;
using Sqm.Domain.Bindings;
using Sqm.Domain.Timeline;

namespace Sqm.Application.Tests;

/// <summary>
/// A timeline shows each binding's lifetime and combines them by what they were bound to - and
/// shows a related identifier only to someone who may look that kind up.
/// </summary>
public sealed class TimelineBuilderTests
{
    private const string Imei = "35085748000001";
    private const ulong NumberA = 9121234567, NumberB = 9121234568;
    private const ulong SimA = 432110123456789, SimB = 432110123456780;

    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Apr10 = new(2026, 4, 10);
    private static readonly DateOnly Jun5 = new(2026, 6, 5);

    private static BindingEvent E(DateOnly d, ChangeLabel l) => new(d, d.DayNumber, l);

    /// <summary>
    /// One handset: SIM A with number A from the dump, removed on 1 March; SIM B with number A added
    /// that day and still held; SIM B with number B for April to June.
    /// </summary>
    private static StoredTimeline Handset(bool lastActive = true) => new(
    [
        new StoredBinding(NumberA, SimB, Imei, "35085748", "Samsung", "Galaxy A32", false,
            [E(Mar1, ChangeLabel.Add)], lastActive),
        new StoredBinding(NumberB, SimB, Imei, "35085748", "Samsung", "Galaxy A32", false,
            [E(Jun5, ChangeLabel.Remove), E(Apr10, ChangeLabel.Add)], false),
        new StoredBinding(NumberA, SimA, Imei, "35085748", "Samsung", "Galaxy A32", true,
            [E(Mar1, ChangeLabel.Remove)], false),
    ], Total: 3, ElapsedMs: 12, RowsRead: 81_920);

    private static TimelineVisibility All(bool masked = false) => new(
        true, true, true, (_, v) => masked ? "****" + v[^3..] : v, masked);

    [Fact]
    public void Bindings_carry_their_lifetimes_and_the_whole_carries_its_figures()
    {
        var t = TimelineBuilder.Build(TimelineCentre.Imei, Imei, Handset(), All(), 10_000, new DateOnly(2026, 9, 26));

        Assert.Equal(("imei", "35085748", "Samsung", "Galaxy A32"), (t.Kind, t.Tac, t.Brand, t.Model));
        Assert.Equal(("2025-12-27", true, "2026-06-05"), (t.Summary.FirstSeen, t.Summary.FirstSeenIsDumpWindow, t.Summary.LastChange));
        Assert.Equal((3, 1, 2, 2, 1), (t.Summary.Bindings, t.Summary.ActiveBindings, t.Summary.Numbers, t.Summary.Sims, t.Summary.Handsets));

        var april = t.Bindings[1];
        Assert.Equal([new("2026-04-10", false, "2026-06-05")], april.Periods);
        Assert.Equal(["2026-04-10", "2026-06-05"], april.Events.Select(e => e.Date));
        Assert.Equal((Imei, "2026-04-10"), (april.Imei, april.FirstSeen));
    }

    [Fact]
    public void Groups_join_the_periods_of_each_related_identifier()
    {
        var t = TimelineBuilder.Build(TimelineCentre.Imei, Imei, Handset(), All(), 10_000, null);

        // Number A: the dump binding until 1 March, then SIM B from the same day - one unbroken period.
        var numberA = Assert.Single(t.Groups, g => g.Kind == "msisdn" && g.Key == NumberA.ToString());
        Assert.Equal([new("2025-12-27", true, null)], numberA.Periods);
        Assert.Equal((2, true, true), (numberA.Bindings, numberA.Active, numberA.Drillable));

        // SIM B: from 1 March, still held - the April-June binding with number B lies inside it.
        var simB = Assert.Single(t.Groups, g => g.Kind == "imsi" && g.Key == SimB.ToString());
        Assert.Equal([new("2026-03-01", false, null)], simB.Periods);

        // Nothing is grouped by the centre's own kind.
        Assert.DoesNotContain(t.Groups, g => g.Kind == "imei");
    }

    [Fact]
    public void A_kind_the_caller_may_not_look_up_is_withheld_and_said_to_be()
    {
        var noNumbers = All() with { Numbers = false };

        var t = TimelineBuilder.Build(TimelineCentre.Imei, Imei, Handset(), noNumbers, 10_000, null);

        Assert.All(t.Bindings, b => Assert.Null(b.Msisdn));
        Assert.DoesNotContain(t.Groups, g => g.Kind == "msisdn");
        Assert.Equal(["msisdn"], t.Withheld);
        Assert.Contains(t.Groups, g => g.Kind == "imsi");
    }

    [Fact]
    public void Masked_identifiers_cannot_be_opened()
    {
        var t = TimelineBuilder.Build(TimelineCentre.Imei, Imei, Handset(), All(masked: true), 10_000, null);

        Assert.True(t.Masked);
        Assert.All(t.Groups, g => Assert.False(g.Drillable));
        Assert.All(t.Bindings, b => Assert.StartsWith("****", b.Imsi, StringComparison.Ordinal));
        Assert.Equal(Imei, t.Identifier); // what was asked is not news to the caller
    }

    [Fact]
    public void Events_say_what_they_did_and_disagreements_with_current_state_are_counted()
    {
        // Current state says the March binding has ended; its events say it is still held.
        var t = TimelineBuilder.Build(TimelineCentre.Imei, Imei, Handset(lastActive: false), All(), 10_000, null);

        Assert.Equal(1, t.Summary.StateDisagreements);
        Assert.False(t.Bindings[0].Active); // current state's verdict is the one shown
        Assert.Equal(["applied"], t.Bindings[2].Events.Select(e => e.Effect));
    }

    [Fact]
    public void More_bindings_than_the_limit_are_reported_as_truncated()
    {
        var stored = Handset() with { Total = 12_000 };

        Assert.True(TimelineBuilder.Build(TimelineCentre.Imei, Imei, stored, All(), 3, null).Truncated);
        Assert.False(TimelineBuilder.Build(TimelineCentre.Imei, Imei, Handset(), All(), 3, null).Truncated);
    }
}
