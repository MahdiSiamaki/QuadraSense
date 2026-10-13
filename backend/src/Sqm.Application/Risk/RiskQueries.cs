using System.Globalization;
using Sqm.Application.Quality;
using Sqm.Contracts.Risk;
using Sqm.Domain.Risk;

namespace Sqm.Application.Risk;

/// <summary>The rule set, the published run, the flagged days, and whether lists can be served.</summary>
/// <param name="Options">The rule set as configured.</param>
/// <param name="Settings">The rule set as the domain judges with it, carrying its version.</param>
/// <param name="State">The published run, whether it is current, and the days with data.</param>
/// <param name="Flagged">Every check the feed-quality monitor flagged in the 30 days to the run's as-of day.</param>
/// <param name="Calibrated">True when at least one rule has a threshold.</param>
/// <param name="NotReady">Why lists and judgements cannot be served, or null when they can.</param>
public sealed record RiskContext(
    RiskOptions Options, RiskSettings Settings, RiskReadState State, IReadOnlyList<FlaggedDay> Flagged,
    bool Calibrated, string? NotReady)
{
    /// <summary>Flagged days relevant to a rule, inside its window.</summary>
    public IReadOnlyList<DateOnly> FlaggedIn(RiskRuleSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (State.Run is not { } run || spec.WindowDays is not { } days)
        {
            return [];
        }

        var from = run.AsOf.AddDays(-(days - 1));
        return [.. Flagged.Where(f => f.Date >= from && f.Date <= run.AsOf && spec.RelevantChecks.Contains(f.Check))
            .Select(f => f.Date).Distinct().Order()];
    }
}

/// <summary>Why a risk aggregate was not answered.</summary>
public enum RiskRefusal
{
    /// <summary>Answered.</summary>
    None,

    /// <summary>Risk signals cannot be served yet (503): not deployed, not computed or not calibrated.</summary>
    Unavailable,

    /// <summary>The rule asked about has no calibrated threshold (422).</summary>
    NoThreshold,
}

/// <summary>A risk aggregate, or why it was not answered.</summary>
/// <param name="Value">The answer, when <paramref name="Refusal"/> is None.</param>
/// <param name="Refusal">Why not, otherwise.</param>
/// <param name="Reason">The refusal in words, for a person; may be null for Unavailable, as the route has always allowed.</param>
public sealed record RiskAnswer<T>(T? Value, RiskRefusal Refusal = RiskRefusal.None, string? Reason = null)
    where T : class;

/// <summary>
/// The risk aggregates that name nobody - status, overview per rule, distribution, device types and the
/// daily SIM-change evidence - composed once, for the risk routes and for the dashboard export.
/// </summary>
/// <remarks>
/// <para>
/// <b>One composition, two callers, on purpose.</b> A dashboard export must show what the card showed,
/// and the quickest way to make the two disagree is to write the readiness rules and the judging
/// twice. The routes keep their status codes; this class says only what the answer is, or why there
/// is none.
/// </para>
/// <para>
/// <b>Plain values in.</b> The rule set and the feed-quality settings are passed as values: the API reads
/// them from its options monitor per request, and this layer takes no hosting dependency.
/// </para>
/// </remarks>
public static class RiskQueries
{
    /// <summary>Why the daily evidence is unavailable when the risk tables are not deployed.</summary>
    public const string NotDeployed = "Risk signals are not deployed on this server (analytics migrations 023 and 024).";

    /// <summary>Why a rule's device types cannot be listed.</summary>
    public const string NoThreshold = "This rule has no calibrated threshold.";

    /// <summary>The rule set, the published run, the flagged days, and whether lists can be served.</summary>
    /// <param name="reader">The risk reader.</param>
    /// <param name="quality">The feed-quality reader, for the days that hold a rule at Anomaly.</param>
    /// <param name="qualityOptions">How a day's file is judged.</param>
    /// <param name="options">The rule set in force for this request.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<RiskContext> ContextAsync(
        IRiskReader reader, IFeedQualityReader quality, FeedQualityOptions qualityOptions, RiskOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(qualityOptions);
        ArgumentNullException.ThrowIfNull(options);

        var problems = options.Problems();
        var state = await reader.GetStateAsync(options.Floors, ct).ConfigureAwait(false);
        var calibrated = options.ThresholdsByRule().Count > 0;

        IReadOnlyList<FlaggedDay> flagged = [];
        if (state.Run is { } run)
        {
            // The longest window: 30 days to the run's as-of day.
            var report = await FeedQualityReport.BuildAsync(quality, qualityOptions, run.AsOf.AddDays(-29), run.AsOf, ct)
                .ConfigureAwait(false);
            flagged = RiskEvaluation.Flagged(report);
        }

        string? notReady =
            problems.Count > 0 ? "The Risk settings are invalid: " + string.Join(" ", problems)
            : !state.Deployed ? NotDeployed
            : state.Run is null ? "Risk measures have not been computed yet. The import worker computes them while it is idle; "
                                  + "Sqm.Ingestion --refresh-risk computes them now."
            : !calibrated ? "Risk thresholds are not calibrated yet. Each is cut from the measured distribution at the number "
                            + "of entities analysts can review (ADR-014); until then nothing is judged."
            : null;

        return new RiskContext(options, options.Settings(), state, flagged, calibrated, notReady);
    }

    /// <summary>The rules, their thresholds and windows, the published measures and whether they are current.</summary>
    /// <remarks>Always answered: a status that is not ready says why, and that is its content.</remarks>
    public static async Task<RiskStatusResponse> StatusAsync(IRiskReader reader, RiskContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(context);

        var options = context.Options;

        return new RiskStatusResponse(
            context.NotReady is null,
            context.NotReady,
            context.Calibrated,
            options.Version(),
            options.MaxDefectShare,
            Iso(context.State.DataThrough),
            context.State.Run is { } run ? new RiskRunInfo(Iso(run.AsOf)!, run.PublishedAt, context.State.Stale) : null,
            [.. RiskRules.Catalogue.Select(spec => RuleInfo(spec, context))],
            context.State.Deployed ? await reader.DeviceTypesAsync(ct).ConfigureAwait(false) : []);
    }

    /// <summary>Per rule, how many entities are listed and at which level. Names nobody.</summary>
    public static async Task<RiskAnswer<RiskOverviewResponse>> OverviewAsync(IRiskReader reader, RiskContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(context);

        if (context.NotReady is not null || context.State.Run is not { } run)
        {
            return new(null, RiskRefusal.Unavailable, context.NotReady);
        }

        var thresholds = context.Options.ThresholdsByRule();
        var counts = await reader.CountAsync(run, thresholds, context.Settings.MaxDefectShare, ct).ConfigureAwait(false);

        return new(new RiskOverviewResponse(
            context.Settings.Version,
            Iso(run.AsOf)!,
            [.. counts.Select(c =>
            {
                var spec = RiskRules.Spec(c.Rule);
                var capped = spec.Windowed && context.FlaggedIn(spec).Count > 0;
                var level = !spec.Windowed || capped ? RiskLevel.Anomaly : RiskLevel.RiskSignal;
                return new RiskOverviewRule(c.Rule.ToString(), level.ToString(), capped, c.Risk, c.DataQuality);
            })]));
    }

    /// <summary>How a rule's stored values spread, and where its threshold sits. Names nobody.</summary>
    /// <remarks>Needs a published run, not a calibration: the distribution is what a threshold is cut from.</remarks>
    public static async Task<RiskAnswer<RiskDistributionResponse>> DistributionAsync(
        IRiskReader reader, RiskContext context, RiskRule rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(context);

        if (context.State.Run is not { } run || !context.State.Deployed)
        {
            return new(null, RiskRefusal.Unavailable, context.NotReady);
        }

        // A bucket starts exactly where the list starts, so the listed range is never half a bucket.
        var threshold = context.Settings.Thresholds.GetValueOrDefault(rule)?.Value;
        var edges = RiskEvaluation.DistributionEdges(rule, context.Options.Floors);
        if (threshold is { } t && t + 1 > edges[0] && !edges.Contains(t + 1))
        {
            edges = [.. edges.Append(t + 1).Order()];
        }

        var buckets = await reader.DistributionAsync(run, rule, edges, ct).ConfigureAwait(false);
        var spec = RiskRules.Spec(rule);

        return new(new RiskDistributionResponse(
            rule.ToString(), spec.Unit, threshold,
            RiskEvaluation.Floor(rule, context.Options.Floors),
            [.. buckets.Select(b => new RiskBucketInfo(b.From, b.To, b.Clean, b.Raw))], Iso(run.AsOf)!));
    }

    /// <summary>Whether a rule's list names SIMs or IMEIs, which have a device type; numbers have none.</summary>
    public static bool HasDeviceTypes(RiskRule rule) => RiskEvaluation.ListOf(rule) != RiskList.Numbers;

    /// <summary>A list's entities by GSMA device type, at the configured threshold. Names nobody.</summary>
    /// <remarks>The caller has checked <see cref="HasDeviceTypes"/>.</remarks>
    public static async Task<RiskAnswer<RiskDeviceTypesResponse>> DeviceTypesAsync(
        IRiskReader reader, RiskContext context, RiskRule rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(context);

        if (context.NotReady is not null || context.State.Run is not { } run)
        {
            return new(null, RiskRefusal.Unavailable, context.NotReady);
        }

        if (context.Settings.Thresholds.GetValueOrDefault(rule) is not { } threshold)
        {
            return new(null, RiskRefusal.NoThreshold, NoThreshold);
        }

        var rows = await reader.DeviceTypesOfListAsync(
            run, new RiskListQuery(rule, RiskView.Risk, threshold, context.Settings.MaxDefectShare, 1, 1), ct).ConfigureAwait(false);

        return new(new RiskDeviceTypesResponse(
            rule.ToString(),
            RiskEvaluation.ListOf(rule) == RiskList.Sims ? "most frequent TAC in 20 days" : "handset",
            [.. rows.Select(r => new RiskDeviceTypeCount(r.DeviceType, r.Entities))]));
    }

    /// <summary>Every day's SIM changes, what the screens set aside, and the flagged days. Names nobody.</summary>
    /// <remarks>Needs only the deployed tables: the evidence is counted, not judged.</remarks>
    public static async Task<RiskAnswer<RiskDailyResponse>> DailyAsync(
        IRiskReader reader, IFeedQualityReader quality, FeedQualityOptions qualityOptions, RiskFloorOptions floors, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(qualityOptions);
        ArgumentNullException.ThrowIfNull(floors);

        var state = await reader.GetStateAsync(floors, ct).ConfigureAwait(false);
        if (!state.Deployed)
        {
            return new(null, RiskRefusal.Unavailable, NotDeployed);
        }

        var days = await reader.SimChangeDaysAsync(ct).ConfigureAwait(false);
        if (days.Count == 0)
        {
            return new(new RiskDailyResponse([]));
        }

        // Flagged days: dates and check names only, for risk.view holders who may not hold import.view.
        var report = await FeedQualityReport.BuildAsync(quality, qualityOptions, days[0].Date, days[^1].Date, ct)
            .ConfigureAwait(false);
        var flagged = RiskEvaluation.Flagged(report)
            .GroupBy(f => f.Date)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(f => f.Check.ToString()).Distinct()]);

        return new(new RiskDailyResponse([.. days.Select(d => new RiskChangeDayInfo(
            Iso(d.Date)!, d.Changes, d.MultiNumber + d.Unscreened, flagged.GetValueOrDefault(d.Date) ?? []))]));
    }

    private static RiskRuleInfo RuleInfo(RiskRuleSpec spec, RiskContext context)
    {
        var threshold = context.Settings.Thresholds.GetValueOrDefault(spec.Rule);
        RiskWindow? window = context.State.Run is { } run && spec.WindowDays is { } days
            ? RiskEvaluation.Window(run.AsOf, days, context.State.DaysWithData)
            : null;

        return new RiskRuleInfo(
            spec.Rule.ToString(), spec.Family.ToString(), RiskEvaluation.ListOf(spec.Rule).ToString(), spec.Unit, spec.What,
            spec.WindowDays, Iso(window?.From), Iso(window?.To), window?.DaysWithData,
            threshold?.Value, threshold?.Tacs, RiskEvaluation.Floor(spec.Rule, context.Options.Floors),
            (spec.Windowed ? RiskLevel.RiskSignal : RiskLevel.Anomaly).ToString(),
            [.. spec.RelevantChecks.Select(c => c.ToString())],
            [.. context.FlaggedIn(spec).Select(d => Iso(d)!)],
            RiskEvaluation.HasDataQualityView(spec.Rule));
    }

    private static string? Iso(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
