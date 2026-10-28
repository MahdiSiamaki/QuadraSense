using Sqm.Application.Planning;

namespace Sqm.Application.Tests;

/// <summary>
/// Kaplan-Meier over handset spells: a spell censored before a horizon counts for as long as it was
/// followed and no further, and a horizon nobody was followed to has no answer.
/// </summary>
public sealed class SurvivalTests
{
    private static Survival.Spells Changed(int days, long count) => new(days, true, count);

    private static Survival.Spells Censored(int days, long count) => new(days, false, count);

    // Ten spells: two change on day 10, three are censored on day 20 (still held at the delivery),
    // one changes on day 30, four are censored on day 40.
    private static readonly Survival.Spells[] Ten =
        [Changed(10, 2), Censored(20, 3), Changed(30, 1), Censored(40, 4)];

    [Fact]
    public void A_censored_spell_leaves_the_risk_set_instead_of_counting_as_unchanged()
    {
        var shares = Survival.EventShareBy(Ten, [5, 10, 25, 30, 40]);

        // Day 10: 2 of 10 at risk -> 0.8 still. Day 30: 1 of the 5 still at risk -> 0.8 x 0.8 = 0.64.
        // A plain share would say 3 of 10 by day 30; the three censored on day 20 were never followed there.
        Assert.Equal([0.0, 0.2, 0.2, 0.36, 0.36], shares.Select(s => Math.Round(s!.Value, 6)));
    }

    [Fact]
    public void A_horizon_no_spell_was_followed_to_has_no_answer()
    {
        Assert.Null(Survival.EventShareBy(Ten, [41])[0]);
        Assert.All(Survival.EventShareBy([], [1, 30]), s => Assert.Null(s));
    }

    [Fact]
    public void A_spell_censored_on_the_day_of_an_event_was_at_risk_that_day()
    {
        var share = Survival.EventShareBy([Changed(10, 1), Censored(10, 1)], [10])[0];

        Assert.Equal(0.5, share);
    }

    [Fact]
    public void The_median_is_the_first_day_half_had_changed_and_null_when_they_never_did()
    {
        Assert.Equal(6, Survival.MedianDays([Changed(5, 1), Changed(6, 1), Changed(7, 1), Changed(8, 1)]));
        Assert.Null(Survival.MedianDays(Ten));
    }
}
