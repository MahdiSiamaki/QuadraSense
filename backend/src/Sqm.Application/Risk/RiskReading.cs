using Sqm.Application.Quality;
using Sqm.Domain.Risk;

namespace Sqm.Application.Risk;

/// <summary>Which stored list a rule is read from.</summary>
public enum RiskList
{
    /// <summary>Per SIM, windowed: <c>risk_sim_window</c>.</summary>
    Sims,

    /// <summary>Per IMEI, windowed: <c>risk_imei_window</c>.</summary>
    ImeiWindow,

    /// <summary>Per IMEI, all time: <c>risk_imei_lifetime</c>.</summary>
    ImeiLifetime,

    /// <summary>Per number, SIM changes: <c>risk_sim_change_day</c>.</summary>
    Numbers,
}

/// <summary>Risk signals, read or the risk view of a list.</summary>
public enum RiskView
{
    /// <summary>Entities over the threshold on clean evidence.</summary>
    Risk,

    /// <summary>
    /// Entities over the threshold only because of rows set aside as feed defects, or with most of their
    /// evidence set aside. Not behaviour.
    /// </summary>
    DataQuality,
}

/// <summary>The newest published snapshot run.</summary>
/// <param name="RunId">Its id.</param>
/// <param name="AsOf">The data-through day its windows end on.</param>
/// <param name="Fingerprint">The inputs it was built from.</param>
/// <param name="PublishedAt">When it was published.</param>
public sealed record RiskPublishedRun(ulong RunId, DateOnly AsOf, ulong Fingerprint, DateTimeOffset PublishedAt);

/// <summary>What the risk pages can be served from right now.</summary>
/// <param name="Deployed">False when analytics migration 024 is not applied.</param>
/// <param name="Run">The newest published run, or null.</param>
/// <param name="Stale">Why that run no longer matches today's data, or null when it does.</param>
/// <param name="DataThrough">The latest day of the event log.</param>
/// <param name="DaysWithData">Every day the binding history holds, so a window can say how many of its days had data.</param>
public sealed record RiskReadState(
    bool Deployed, RiskPublishedRun? Run, string? Stale, DateOnly? DataThrough, IReadOnlySet<DateOnly> DaysWithData);

/// <summary>A SIM's stored measures.</summary>
public sealed record RiskSimMeasures(
    long Imeis30, long Imeis30Raw, long Imeis20, long Tacs20, long Imeis7, long Imeis7Raw,
    long Adds30, long AddsSetAside30, int AddDays30, long MaxImeisOneDay30, DateOnly? MaxDay30,
    IReadOnlyList<string> TopTacs20);

/// <summary>An IMEI's stored windowed measures.</summary>
public sealed record RiskImeiWindowMeasures(
    long Sims30, long Sims30Raw, long Sims7, long Numbers30,
    long Adds30, long AddsSetAside30, int AddDays30, long MaxSimsOneDay30, DateOnly? MaxDay30);

/// <summary>An IMEI's stored all-time measures.</summary>
public sealed record RiskImeiLifetimeMeasures(
    long SimsEver, long NumbersEver, long Bindings, long NotRemovedDated, long NotRemovedDump);

/// <summary>A number's SIM changes in the 7 days to the run's as-of day.</summary>
/// <param name="Changes7">Days with a SIM change not set aside.</param>
/// <param name="Changes7Raw">Days with a SIM change, before the screen.</param>
/// <param name="LastChange">The latest of them.</param>
public sealed record RiskNumberMeasures(long Changes7, long Changes7Raw, DateOnly? LastChange);

/// <summary>Everything stored for one entity. A part is null when the entity has no row there: below the storage floor.</summary>
/// <param name="Family">The entity's unit.</param>
/// <param name="Key">The identifier, as stored.</param>
/// <param name="Sim">For a SIM.</param>
/// <param name="ImeiWindow">For an IMEI, windowed.</param>
/// <param name="ImeiLifetime">For an IMEI, all time.</param>
/// <param name="Number">For a number.</param>
/// <param name="Tac">For an IMEI: its TAC.</param>
/// <param name="Brand">For an IMEI: GSMA brand, when known.</param>
/// <param name="Model">For an IMEI: GSMA marketing name, when known.</param>
public sealed record RiskEntityMeasures(
    RiskFamily Family, string Key,
    RiskSimMeasures? Sim = null, RiskImeiWindowMeasures? ImeiWindow = null, RiskImeiLifetimeMeasures? ImeiLifetime = null,
    RiskNumberMeasures? Number = null, string? Tac = null, string? Brand = null, string? Model = null);

/// <summary>One page of a list.</summary>
/// <param name="Rows">The page, in list order.</param>
/// <param name="Total">Every entity in the list.</param>
/// <param name="ElapsedMs">Server time.</param>
/// <param name="RowsRead">Rows read.</param>
public sealed record RiskListPage(IReadOnlyList<RiskEntityMeasures> Rows, long Total, long ElapsedMs, long RowsRead);

/// <summary>A list read: which rule, which view, at which thresholds, which page.</summary>
/// <param name="Rule">The rule whose list is read.</param>
/// <param name="View">Risk, or the data-quality view.</param>
/// <param name="Threshold">More than this is listed.</param>
/// <param name="MaxDefectShare">Above this share of adds set aside, an entity belongs to the data-quality view.</param>
/// <param name="Page">From 1.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="DeviceTypes">
/// GSMA device types to keep, or empty for all: the handset's own for an IMEI list, the type of the SIM's
/// most frequent TAC in 20 days for a SIM list. Number lists have none.
/// </param>
public sealed record RiskListQuery(
    RiskRule Rule, RiskView View, RiskThreshold Threshold, double MaxDefectShare, int Page, int PageSize,
    IReadOnlyList<string>? DeviceTypes = null);

/// <summary>How many entities a rule lists, in each view.</summary>
public sealed record RiskRuleCount(RiskRule Rule, long Risk, long DataQuality);

/// <summary>Reads the published risk snapshot and the per-day SIM changes. Read-only.</summary>
public interface IRiskReader
{
    /// <summary>The published run, whether it is current, and the days with data.</summary>
    Task<RiskReadState> GetStateAsync(RiskFloorOptions floors, CancellationToken ct);

    /// <summary>One page of a rule's list, largest value first.</summary>
    Task<RiskListPage> ListAsync(RiskPublishedRun run, RiskListQuery query, CancellationToken ct);

    /// <summary>The GSMA device types a list can be narrowed to, as the active GSMA version names them.</summary>
    Task<IReadOnlyList<string>> DeviceTypesAsync(CancellationToken ct);

    /// <summary>Per rule, how many entities each view lists at these thresholds.</summary>
    Task<IReadOnlyList<RiskRuleCount>> CountAsync(
        RiskPublishedRun run, IReadOnlyDictionary<RiskRule, RiskThreshold> thresholds, double maxDefectShare, CancellationToken ct);

    /// <summary>Everything stored for one entity, or null when it has no row anywhere.</summary>
    Task<RiskEntityMeasures?> GetEntityAsync(RiskPublishedRun run, RiskFamily family, string key, CancellationToken ct);

    /// <summary>
    /// The entities bound to this one in the 30 days to the run's as-of day - a SIM's IMEIs and numbers,
    /// an IMEI's SIMs, a number's SIMs - with whatever is stored for each. Those with no stored row are
    /// below every floor and left out.
    /// </summary>
    Task<IReadOnlyList<RiskEntityMeasures>> GetLinkedAsync(RiskPublishedRun run, RiskFamily family, string key, CancellationToken ct);
}

/// <summary>
/// Turns stored measures into the domain's evidence, with their windows, and the feed-quality report
/// into flagged days. Pure.
/// </summary>
public static class RiskEvaluation
{
    /// <summary>The list a rule is read from.</summary>
    public static RiskList ListOf(RiskRule rule) => rule switch
    {
        RiskRule.SharedImeiSims30 or RiskRule.SharedImeiNumbers30 => RiskList.ImeiWindow,
        RiskRule.SharedImeiSimsEver or RiskRule.SharedImeiSimsNotRemoved => RiskList.ImeiLifetime,
        RiskRule.HighDeviceCount30 or RiskRule.RapidDeviceChange7 or RiskRule.Randomisation20 => RiskList.Sims,
        _ => RiskList.Numbers,
    };

    /// <summary>
    /// True when a rule's list has a data-quality view: a raw count beside the clean one, or a share of
    /// adds set aside. All-time counts have neither.
    /// </summary>
    public static bool HasDataQualityView(RiskRule rule) => ListOf(rule) != RiskList.ImeiLifetime;

    /// <summary>The storage floor a rule's threshold is checked against.</summary>
    public static int Floor(RiskRule rule, RiskFloorOptions floors)
    {
        ArgumentNullException.ThrowIfNull(floors);
        return rule switch
        {
            RiskRule.SharedImeiSims30 or RiskRule.SharedImeiNumbers30 => floors.ImeiSims30,
            RiskRule.SharedImeiSimsEver => floors.ImeiSimsEver,
            RiskRule.SharedImeiSimsNotRemoved => floors.ImeiSimsNotRemoved,
            RiskRule.RepeatedSimChange7 => 1,
            _ => floors.SimImeis30,
        };
    }

    /// <summary>The window of so many days ending on <paramref name="asOf"/>, with how many of them have data.</summary>
    public static RiskWindow Window(DateOnly asOf, int days, IReadOnlySet<DateOnly> daysWithData)
    {
        ArgumentNullException.ThrowIfNull(daysWithData);
        var from = asOf.AddDays(-(days - 1));
        return new RiskWindow(from, asOf, daysWithData.Count(d => d >= from && d <= asOf));
    }

    /// <summary>The measures a row holds, as the domain judges them.</summary>
    public static RiskEvidence Evidence(RiskEntityMeasures entity, DateOnly asOf, IReadOnlySet<DateOnly> daysWithData)
    {
        ArgumentNullException.ThrowIfNull(entity);

        RiskWindow W(int days) => Window(asOf, days, daysWithData);

        var measures = new List<RiskMeasure>();
        long adds = 0, aside = 0;

        if (entity.Sim is { } s)
        {
            measures.Add(new RiskMeasure(RiskRule.HighDeviceCount30, s.Imeis30, W(30)));
            measures.Add(new RiskMeasure(RiskRule.RapidDeviceChange7, s.Imeis7, W(7)));
            measures.Add(new RiskMeasure(RiskRule.Randomisation20, s.Imeis20, W(20), s.Tacs20));
            (adds, aside) = (s.Adds30, s.AddsSetAside30);
        }

        if (entity.ImeiWindow is { } w)
        {
            measures.Add(new RiskMeasure(RiskRule.SharedImeiSims30, w.Sims30, W(30)));
            measures.Add(new RiskMeasure(RiskRule.SharedImeiNumbers30, w.Numbers30, W(30)));
            (adds, aside) = (w.Adds30, w.AddsSetAside30);
        }

        if (entity.ImeiLifetime is { } l)
        {
            measures.Add(new RiskMeasure(RiskRule.SharedImeiSimsEver, l.SimsEver, null));
            measures.Add(new RiskMeasure(RiskRule.SharedImeiSimsNotRemoved, l.NotRemovedDated, null));
        }

        if (entity.Number is { } n)
        {
            measures.Add(new RiskMeasure(RiskRule.RepeatedSimChange7, n.Changes7, W(7)));
            (adds, aside) = (n.Changes7Raw, n.Changes7Raw - n.Changes7);
        }

        return new RiskEvidence(entity.Family, measures, adds, aside);
    }

    /// <summary>Every check the feed-quality monitor flagged, day by day.</summary>
    public static IReadOnlyList<FlaggedDay> Flagged(FeedQualityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return [.. report.Days.SelectMany(d => d.Findings.Where(f => f.Flagged).Select(f => new FlaggedDay(d.Day.Date, f.Check)))];
    }
}
