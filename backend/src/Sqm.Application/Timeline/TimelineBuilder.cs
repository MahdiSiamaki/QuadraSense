using System.Globalization;
using Sqm.Contracts.Timeline;
using Sqm.Domain.Bindings;
using Sqm.Domain.Timeline;

namespace Sqm.Application.Timeline;

/// <summary>What the caller may see of the identifiers related to the centre, and how they are written.</summary>
/// <param name="Numbers">May look up numbers (lookup.subscriber).</param>
/// <param name="Sims">May look up SIMs (lookup.imsi).</param>
/// <param name="Handsets">May look up handsets (lookup.imei).</param>
/// <param name="Write">Writes an identifier of a kind: as it is, or masked.</param>
/// <param name="Masked">The caller lacks identifier.reveal.</param>
public sealed record TimelineVisibility(
    bool Numbers, bool Sims, bool Handsets, Func<TimelineCentre, string, string> Write, bool Masked);

/// <summary>
/// Turns an entity's stored bindings into its timeline: each binding's lifetime, the bindings
/// combined by each related identifier, and the figures for the whole.
/// </summary>
/// <remarks>
/// <para>
/// Pure: the history is read by the store and the decisions about who may see what are made by
/// the endpoint; this only derives and arranges. Lifetimes come from <see cref="BindingLifetime"/>,
/// which applies events with the fold's own rules, so a timeline cannot tell a different story
/// from current state - and where the two disagree, it counts the disagreement rather than
/// choosing silently. The state shown is current state's.
/// </para>
/// <para>
/// <b>Related identifiers follow the Relationships page's rule:</b> each kind is returned only to
/// someone who may look that kind up, and is otherwise reported as withheld - an empty list would
/// read as "none".
/// </para>
/// </remarks>
public static class TimelineBuilder
{
    /// <summary>Builds the timeline.</summary>
    public static TimelineResponse Build(
        TimelineCentre centre, string identifier, StoredTimeline stored, TimelineVisibility visibility,
        int maxBindings, DateOnly? dataThrough)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(visibility);

        var lives = stored.Bindings
            .Select(b => (Binding: b, Life: BindingLifetime.Derive(b.InDump, b.Events)))
            .ToList();

        bool Visible(TimelineCentre kind) => kind == centre || kind switch
        {
            TimelineCentre.Msisdn => visibility.Numbers,
            TimelineCentre.Imsi => visibility.Sims,
            _ => visibility.Handsets,
        };

        string? Shown(TimelineCentre kind, string value) =>
            !Visible(kind) ? null : kind == centre ? identifier : visibility.Write(kind, value);

        var bindings = lives.Select(x => new TimelineBindingInfo(
            Shown(TimelineCentre.Msisdn, x.Binding.Msisdn.ToString(CultureInfo.InvariantCulture)),
            Shown(TimelineCentre.Imsi, x.Binding.Imsi.ToString(CultureInfo.InvariantCulture)),
            Shown(TimelineCentre.Imei, x.Binding.Imei),
            x.Binding.Tac, x.Binding.Brand, x.Binding.Model,
            x.Binding.Active, x.Binding.InDump,
            Iso(x.Life.FirstSeen), x.Life.FirstSeenIsDumpWindow, Iso(x.Life.LastChange),
            [.. x.Life.Periods.Select(Period)],
            [.. x.Life.Events.Select((e, i) => new TimelineEventInfo(
                Iso(e.Date)!,
                e.Label == ChangeLabel.Add ? "add" : "remove",
                x.Life.Outcomes[i] switch
                {
                    FoldOutcome.RedundantAdd => "redundant",
                    FoldOutcome.OrphanRemove => "orphan",
                    _ => "applied",
                }))])).ToList();

        var groups = new List<TimelineGroupInfo>();
        foreach (var kind in new[] { TimelineCentre.Msisdn, TimelineCentre.Imsi, TimelineCentre.Imei })
        {
            if (kind == centre || !Visible(kind))
            {
                continue;
            }

            groups.AddRange(lives
                .GroupBy(x => Key(kind, x.Binding))
                .Select(g =>
                {
                    var first = g.Where(x => x.Life.FirstSeen is not null).MinBy(x => x.Life.FirstSeen);
                    var anyBinding = g.First().Binding;
                    return new TimelineGroupInfo(
                        Kind(kind),
                        visibility.Write(kind, g.Key),
                        !visibility.Masked && IsComplete(kind, g.Key),
                        kind == TimelineCentre.Imei ? anyBinding.Tac : null,
                        kind == TimelineCentre.Imei ? anyBinding.Brand : null,
                        kind == TimelineCentre.Imei ? anyBinding.Model : null,
                        g.Count(),
                        g.Any(x => x.Binding.Active),
                        Iso(first.Life?.FirstSeen),
                        first.Life?.FirstSeenIsDumpWindow ?? false,
                        Iso(g.Max(x => x.Life.LastChange)),
                        [.. LifetimePeriods.Union(g.SelectMany(x => x.Life.Periods)).Select(Period)]);
                })
                .OrderBy(g => g.FirstSeen, StringComparer.Ordinal)
                .ThenBy(g => g.Key, StringComparer.Ordinal));
        }

        var earliest = lives.Where(x => x.Life.FirstSeen is not null).MinBy(x => x.Life.FirstSeen);
        var withheld = new[] { TimelineCentre.Msisdn, TimelineCentre.Imsi, TimelineCentre.Imei }
            .Where(k => !Visible(k)).Select(Kind).ToList();
        var handset = stored.Bindings.Count > 0 ? stored.Bindings[0] : null;

        return new TimelineResponse(
            Kind(centre), identifier,
            centre == TimelineCentre.Imei ? handset?.Tac : null,
            centre == TimelineCentre.Imei ? handset?.Brand : null,
            centre == TimelineCentre.Imei ? handset?.Model : null,
            new TimelineSummaryInfo(
                Iso(earliest.Life?.FirstSeen),
                earliest.Life?.FirstSeenIsDumpWindow ?? false,
                Iso(lives.Max(x => x.Life.LastChange)),
                lives.Count,
                lives.Count(x => x.Binding.Active),
                lives.Select(x => x.Binding.Msisdn).Distinct().Count(),
                lives.Select(x => x.Binding.Imsi).Distinct().Count(),
                lives.Select(x => x.Binding.Imei).Where(i => i.Length == 14).Distinct(StringComparer.Ordinal).Count(),
                lives.Sum(x => x.Life.RedundantAdds),
                lives.Sum(x => x.Life.OrphanRemoves),
                lives.Count(x => (x.Life.State == BindingState.Active) != x.Binding.Active)),
            groups,
            bindings,
            stored.Total > stored.Bindings.Count,
            maxBindings,
            withheld,
            visibility.Masked,
            Iso(InitialDump.WindowStart)!,
            Iso(InitialDump.WindowEnd)!,
            Iso(dataThrough),
            stored.ElapsedMs,
            stored.RowsRead);
    }

    private static string Key(TimelineCentre kind, StoredBinding b) => kind switch
    {
        TimelineCentre.Msisdn => b.Msisdn.ToString(CultureInfo.InvariantCulture),
        TimelineCentre.Imsi => b.Imsi.ToString(CultureInfo.InvariantCulture),
        _ => b.Imei,
    };

    private static bool IsComplete(TimelineCentre kind, string value) =>
        value.Length == kind switch { TimelineCentre.Msisdn => 10, TimelineCentre.Imsi => 15, _ => 14 }
        && value.All(char.IsAsciiDigit);

    /// <summary>The API's name for a kind - the same words the Explorer and the entity summary use.</summary>
    public static string Kind(TimelineCentre kind) => kind switch
    {
        TimelineCentre.Msisdn => "msisdn",
        TimelineCentre.Imsi => "imsi",
        _ => "imei",
    };

    private static TimelinePeriodInfo Period(LifetimePeriod p) => new(Iso(p.Start)!, p.StartsInDump, Iso(p.End));

    private static string? Iso(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
