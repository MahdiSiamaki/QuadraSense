using Sqm.Domain.Bindings;

namespace Sqm.Domain.Timeline;

/// <summary>The initial dump's window: everything observed in it, not a snapshot at one moment.</summary>
/// <remarks>
/// Measured, not assumed (docs/discovery/03-dated-daily-files.md): the dump covers 2025-12-27 to
/// 2026-01-25 and averages 1.59 bindings per number, which no single instant produces; the first
/// daily file is 2026-01-26. So a binding from the dump has no start date. The product owner
/// decided (2026-10-01) to show the window, and to sort and compute with its first day - the
/// earliest the binding could have been seen.
/// </remarks>
public static class InitialDump
{
    /// <summary>The first day the dump covers.</summary>
    public static readonly DateOnly WindowStart = new(2025, 12, 27);

    /// <summary>The last day the dump covers. The daily files begin the day after.</summary>
    public static readonly DateOnly WindowEnd = new(2026, 1, 25);
}

/// <summary>One dated change to a binding, as the daily file stated it.</summary>
/// <param name="Date">The business date of the file.</param>
/// <param name="Sequence">The file's sequence. Orders files of the SAME date only: a day imported
/// late takes the next sequence, so across dates the date decides.</param>
/// <param name="Label">Add or remove.</param>
public readonly record struct BindingEvent(DateOnly Date, int Sequence, ChangeLabel Label);

/// <summary>A stretch of time over which the feed held a binding as active.</summary>
/// <param name="Start">The day it was added, or the dump window's first day.</param>
/// <param name="StartsInDump">The binding was in the initial dump, so the start is a window, not a day.</param>
/// <param name="End">The day the feed removed it; null while it has not.</param>
public sealed record LifetimePeriod(DateOnly Start, bool StartsInDump, DateOnly? End)
{
    /// <summary>Not yet removed by the feed - which is not the same as present in the handset now.</summary>
    public bool IsOpen => End is null;
}

/// <summary>A binding's history, derived from its events: when it was held active, and what did not fit.</summary>
/// <param name="InDump">The initial dump listed it.</param>
/// <param name="Events">Its events, in the order they apply: by date, then sequence.</param>
/// <param name="Outcomes">What each event did, by <see cref="BindingFold"/>; aligned with <paramref name="Events"/>.</param>
/// <param name="Periods">When the feed held it active.</param>
/// <param name="FirstSeen">The dump window's first day if it was in the dump, otherwise its first event's date.</param>
/// <param name="FirstSeenIsDumpWindow">FirstSeen stands for the dump window, not a known day.</param>
/// <param name="LastChange">Its last event's date; null when no daily file ever mentioned it.</param>
/// <param name="State">The state its events leave it in, by <see cref="BindingFold"/>.</param>
/// <param name="RedundantAdds">Adds of a binding already active - mostly the dump's month-union.</param>
/// <param name="OrphanRemoves">Removes of a binding not active.</param>
/// <param name="SameDayConflicts">Dates carrying more than one event for it. Zero across the whole log
/// (measured 2026-10-01); counted if it ever happens, because their order within the day is then unknown.</param>
public sealed record BindingLifetime(
    bool InDump,
    IReadOnlyList<BindingEvent> Events,
    IReadOnlyList<FoldOutcome> Outcomes,
    IReadOnlyList<LifetimePeriod> Periods,
    DateOnly? FirstSeen,
    bool FirstSeenIsDumpWindow,
    DateOnly? LastChange,
    BindingState State,
    int RedundantAdds,
    int OrphanRemoves,
    int SameDayConflicts)
{
    /// <summary>
    /// Derives a binding's lifetime from its events, applying them with <see cref="BindingFold"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By date, then sequence - not sequence alone. A missing day imported late is given the next
    /// sequence number, so a remove on 12 May imported after 3 June's add would otherwise apply
    /// last and end a binding the feed had since re-added. The fold in ClickHouse orders the same
    /// way (ClickHouseIngestionStore.FoldFromHistoryAsync).
    /// </para>
    /// <para>
    /// Within one date an add is applied before a remove. That case does not occur: measured over the
    /// whole event log on 2026-10-01 - 1,800,586,041 events, 233 days - no binding has more than one
    /// event on any day. The rule exists so the answer is deterministic if it ever does, and the
    /// case is counted rather than hidden.
    /// </para>
    /// </remarks>
    public static BindingLifetime Derive(bool inDump, IEnumerable<BindingEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var ordered = events
            .OrderBy(e => e.Date)
            .ThenBy(e => e.Sequence)
            .ThenBy(e => e.Label)
            .ToList();

        var periods = new List<LifetimePeriod>();
        var outcomes = new List<FoldOutcome>(ordered.Count);
        var state = inDump ? BindingState.Active : BindingState.Unknown;
        DateOnly? openedOn = inDump ? InitialDump.WindowStart : null;
        var openedInDump = inDump;
        int redundant = 0, orphan = 0;

        foreach (var e in ordered)
        {
            var result = BindingFold.Apply(state, e.Label);
            outcomes.Add(result.Outcome);

            switch (result.Outcome)
            {
                case FoldOutcome.Applied when result.State == BindingState.Active:
                    openedOn = e.Date;
                    openedInDump = false;
                    break;

                case FoldOutcome.Applied:
                    periods.Add(new LifetimePeriod(openedOn!.Value, openedInDump, e.Date));
                    openedOn = null;
                    break;

                case FoldOutcome.RedundantAdd:
                    redundant++;
                    break;

                case FoldOutcome.OrphanRemove:
                    orphan++;
                    break;
            }

            state = result.State;
        }

        if (openedOn is { } start)
        {
            periods.Add(new LifetimePeriod(start, openedInDump, null));
        }

        var conflicts = ordered.GroupBy(e => e.Date).Count(g => g.Count() > 1);

        return new BindingLifetime(
            inDump,
            ordered,
            outcomes,
            periods,
            inDump ? InitialDump.WindowStart : ordered.Count > 0 ? ordered[0].Date : null,
            inDump,
            ordered.Count > 0 ? ordered[^1].Date : null,
            state,
            redundant,
            orphan,
            conflicts);
    }
}

/// <summary>Combining periods - several bindings of one SIM, say - into when any of them was held.</summary>
public static class LifetimePeriods
{
    /// <summary>
    /// The union of the periods: overlapping ones, and one ending on the day the next begins, become
    /// one. A SIM moved from one handset to another on the same day was never without a binding.
    /// </summary>
    public static IReadOnlyList<LifetimePeriod> Union(IEnumerable<LifetimePeriod> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);

        var merged = new List<LifetimePeriod>();

        foreach (var p in periods.OrderBy(p => p.Start).ThenByDescending(p => p.StartsInDump))
        {
            if (merged.Count > 0 && merged[^1] is var last && (last.End is null || p.Start <= last.End))
            {
                merged[^1] = last with
                {
                    End = last.End is null || p.End is null ? null : Max(last.End.Value, p.End.Value),
                };
            }
            else
            {
                merged.Add(p);
            }
        }

        return merged;
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
