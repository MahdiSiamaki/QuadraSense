using System.Globalization;
using Microsoft.Extensions.Options;
using Sqm.Api.Auth;
using Sqm.Api.Infrastructure;
using Sqm.Application.Identity;
using Sqm.Application.Quality;
using Sqm.Application.Risk;
using Sqm.Contracts.Risk;
using Sqm.Domain.Identifiers;
using Sqm.Domain.Risk;

namespace Sqm.Api.Endpoints;

/// <summary>Risk signals: the rules, the lists, and one entity's verdicts.</summary>
/// <remarks>
/// <para>
/// <b>Judged when read.</b> The worker stores counts (analytics migration 024); every level and every
/// reason here comes from <see cref="RiskRules"/>, applied to the returned rows with the configured rule
/// set and the days the feed-quality monitor flagged. A threshold change needs no recompute.
/// </para>
/// <para>
/// <b>Nothing is judged on invented numbers.</b> Until a threshold is cut from the measured
/// distribution (ADR-014), lists, overview and entity answer 503 and say so.
/// </para>
/// <para>
/// <b>Permissions.</b> <c>risk.view</c> on every route, decided by the product owner on 2026-10-02 for
/// Analyst and Administrator. A list or entity also needs the lookup permission of the unit it names;
/// <c>identifier.reveal</c> decides masking; export needs <c>data.export</c>. Identifiers travel only in
/// POST bodies, and the audit records the rule, view and thresholds - never an identifier.
/// </para>
/// </remarks>
public static class RiskEndpoints
{
    /// <summary>Rows per page, at most.</summary>
    public const int MaxPageSize = 500;

    /// <summary>Rows paging or an export can reach.</summary>
    public const int MaxReachable = 10_000;

    /// <summary>Registers the risk routes.</summary>
    public static IEndpointRouteBuilder MapRiskEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var builder = app.MapGroup("/api/v1/risk").WithTags("Risk")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.RiskView);

        builder.MapGet("/status", StatusAsync)
            .WithName("GetRiskStatus")
            .WithSummary("The rules, their thresholds and windows, the published measures and whether they are current.");

        builder.MapGet("/overview", OverviewAsync)
            .WithName("GetRiskOverview")
            .WithSummary("Per rule, how many entities are listed and at which level. Names nobody.");

        builder.MapGet("/distribution", DistributionAsync)
            .WithName("GetRiskDistribution")
            .WithSummary("How a rule's stored values spread, and where its threshold sits. Names nobody.");

        builder.MapGet("/device-types", DeviceTypesAsync)
            .WithName("GetRiskDeviceTypes")
            .WithSummary("A list's entities by GSMA device type, at the configured threshold. Names nobody.");

        builder.MapGet("/daily", DailyAsync)
            .WithName("GetRiskDaily")
            .WithSummary("Every day's SIM changes, what the screens set aside, and the flagged days. Names nobody.");

        builder.MapPost("/list", ListAsync)
            .WithName("ListRisk")
            .WithSummary("One page of a rule's list, judged with the rule set in force.");

        builder.MapPost("/entity", EntityAsync)
            .WithName("GetRiskEntity")
            .WithSummary("One number's, SIM's or handset's risk measures and verdicts.");

        builder.MapPost("/export", ExportAsync)
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DataExport)
            .WithName("ExportRisk")
            .WithSummary("A rule's list as CSV, up to the rows paging can reach.");

        return app;
    }

    // ------------------------------------------------------------------ routes

    // The five aggregates are composed in RiskQueries, which the dashboard export shares; a route here
    // parses what it was sent, reads the options as they are now, and turns the answer into its status.

    private static async Task<IResult> StatusAsync(
        IRiskReader reader, IFeedQualityReader quality, IOptions<FeedQualityOptions> qualityOptions,
        IOptionsMonitor<RiskOptions> riskOptions, CancellationToken ct)
    {
        var context = await ContextAsync(reader, quality, qualityOptions, riskOptions, ct).ConfigureAwait(false);
        return Results.Ok(await RiskQueries.StatusAsync(reader, context, ct).ConfigureAwait(false));
    }

    private static async Task<IResult> OverviewAsync(
        IRiskReader reader, IFeedQualityReader quality, IOptions<FeedQualityOptions> qualityOptions,
        IOptionsMonitor<RiskOptions> riskOptions, CancellationToken ct)
    {
        var context = await ContextAsync(reader, quality, qualityOptions, riskOptions, ct).ConfigureAwait(false);
        return Answer(await RiskQueries.OverviewAsync(reader, context, ct).ConfigureAwait(false));
    }

    private static async Task<IResult> DistributionAsync(
        string rule, IRiskReader reader, IFeedQualityReader quality, IOptions<FeedQualityOptions> qualityOptions,
        IOptionsMonitor<RiskOptions> riskOptions, CancellationToken ct)
    {
        if (!Enum.TryParse<RiskRule>(rule, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["rule"] = ["Unknown rule."] });
        }

        var context = await ContextAsync(reader, quality, qualityOptions, riskOptions, ct).ConfigureAwait(false);
        return Answer(await RiskQueries.DistributionAsync(reader, context, parsed, ct).ConfigureAwait(false));
    }

    private static async Task<IResult> DeviceTypesAsync(
        string rule, IRiskReader reader, IFeedQualityReader quality, IOptions<FeedQualityOptions> qualityOptions,
        IOptionsMonitor<RiskOptions> riskOptions, CancellationToken ct)
    {
        if (!Enum.TryParse<RiskRule>(rule, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed)
            || !RiskQueries.HasDeviceTypes(parsed))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["rule"] = ["A rule whose list names SIMs or IMEIs; numbers have no device type."],
            });
        }

        var context = await ContextAsync(reader, quality, qualityOptions, riskOptions, ct).ConfigureAwait(false);
        return Answer(await RiskQueries.DeviceTypesAsync(reader, context, parsed, ct).ConfigureAwait(false));
    }

    private static async Task<IResult> DailyAsync(
        IRiskReader reader, IFeedQualityReader quality, IOptions<FeedQualityOptions> qualityOptions,
        IOptionsMonitor<RiskOptions> riskOptions, CancellationToken ct) =>
        Answer(await RiskQueries.DailyAsync(reader, quality, qualityOptions.Value, riskOptions.CurrentValue.Floors, ct)
            .ConfigureAwait(false));

    private static async Task<IResult> ListAsync(
        RiskListRequest request, HttpContext http, IRiskReader reader, IFeedQualityReader quality,
        IOptions<FeedQualityOptions> qualityOptions, IOptionsMonitor<RiskOptions> riskOptions, IAuditLog audit,
        CancellationToken ct)
    {
        var (list, refusal) = await PrepareAsync(request, http, reader, quality, qualityOptions, riskOptions, audit, export: false, ct)
            .ConfigureAwait(false);
        if (list is null)
        {
            return refusal!;
        }

        var page = await reader.ListAsync(list.Run, list.Query, ct).ConfigureAwait(false);
        var rows = page.Rows.Select(r => Row(r, list)).ToList();

        await audit.WriteAsync(Entry(list.User, http, "risk.list", AuditOutcome.Success, list, new()
        {
            ["returned"] = rows.Count,
            ["total"] = page.Total,
            ["elapsedMs"] = page.ElapsedMs,
            ["rowsRead"] = page.RowsRead,
        }), ct).ConfigureAwait(false);

        return Results.Ok(new RiskListResponse(
            list.Query.Rule.ToString(), ViewName(list.Query.View), Kind(RiskRules.Spec(list.Query.Rule).Family),
            list.Query.Threshold.Value, list.Query.Threshold.Tacs, list.Overridden,
            Columns(RiskEvaluation.ListOf(list.Query.Rule)), rows,
            page.Total, Math.Min(page.Total, MaxReachable), list.Query.Page, list.Query.PageSize,
            !list.Reveal, list.Context.Settings.Version, Iso(list.Run.AsOf)!, page.ElapsedMs));
    }

    private static async Task<IResult> ExportAsync(
        RiskListRequest request, HttpContext http, IRiskReader reader, IFeedQualityReader quality,
        IOptions<FeedQualityOptions> qualityOptions, IOptionsMonitor<RiskOptions> riskOptions, IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (list, refusal) = await PrepareAsync(
            request with { Page = 1, PageSize = MaxPageSize }, http, reader, quality, qualityOptions, riskOptions, audit, export: true, ct)
            .ConfigureAwait(false);
        if (list is null)
        {
            return refusal!;
        }

        // Every row paging can reach, in one statement: the same rows, masked the same way.
        var page = await reader.ListAsync(list.Run, list.Query with { Page = 1, PageSize = MaxReachable }, ct).ConfigureAwait(false);
        var rows = page.Rows.Select(r => (Entity: r, Row: Row(r, list))).ToList();
        var columns = Columns(RiskEvaluation.ListOf(list.Query.Rule));

        // A list of SIMs carries each SIM's ICCID beside its IMSI: derived (Iccid), so headed as such,
        // and masked as the IMSI is, since it gives the IMSI away.
        var family = RiskRules.Spec(list.Query.Rule).Family;
        var sims = family == RiskFamily.Sim;

        var csv = CsvExport.Write(
            [Kind(family), .. (sims ? IccidHeading : []), "Level", "Assessable", .. columns.Select(c => c.Label), "Reasons", "As of", "Rules"],
            rows.Select(x => (IReadOnlyList<object?>)
            [
                x.Row.Key,
                .. (sims ? new object?[] { DerivedIccid(x.Entity.Key, list.Reveal) } : []),
                x.Row.Level, x.Row.Assessable,
                .. columns.Select(c => Cell(x.Row.Values.GetValueOrDefault(c.Name))),
                string.Join(" | ", x.Row.Reasons.Select(r => r.Text)),
                Iso(list.Run.AsOf), list.Context.Settings.Version,
            ]));

        await audit.WriteAsync(Entry(list.User, http, Permissions.DataExport, AuditOutcome.Success, list, new()
        {
            ["exported"] = rows.Count,
            ["total"] = page.Total,
            ["elapsedMs"] = page.ElapsedMs,
        }), ct).ConfigureAwait(false);

        var name = string.Create(CultureInfo.InvariantCulture, $"risk-{list.Query.Rule}-{ViewName(list.Query.View)}-{list.Run.AsOf:yyyy-MM-dd}.csv");
        return Results.File(csv, "text/csv; charset=utf-8", name);
    }

    private static async Task<IResult> EntityAsync(
        RiskEntityRequest request, HttpContext http, IRiskReader reader, IFeedQualityReader quality,
        IOptions<FeedQualityOptions> qualityOptions, IOptionsMonitor<RiskOptions> riskOptions, IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = CurrentUser.Require(http);
        var term = DeviceSearchTerm.Classify(request.Identifier);

        var (family, permission) = term?.Kind switch
        {
            DeviceSearchKind.Msisdn => (RiskFamily.Number, Permissions.LookupSubscriber),
            DeviceSearchKind.Imsi => (RiskFamily.Sim, Permissions.LookupImsi),
            DeviceSearchKind.Imei => (RiskFamily.Imei, Permissions.LookupImei),
            _ => ((RiskFamily?)null, (string?)null),
        };

        if (family is not { } unit || permission is null || term is not { } parsed)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["identifier"] = ["A phone number (10 digits), a SIM (15) or a handset (14)."],
            });
        }

        var detail = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = Kind(unit) };

        if (!user.Can(permission))
        {
            await audit.WriteAsync(EntityEntry(user, http, permission, AuditOutcome.Denied, detail), ct).ConfigureAwait(false);
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not permitted",
                detail: $"The risk measures of a {KindName(unit)} need the \"{permission}\" permission.");
        }

        var context = await ContextAsync(reader, quality, qualityOptions, riskOptions, ct).ConfigureAwait(false);
        if (context.NotReady is not null || context.State.Run is not { } run)
        {
            return Unavailable(context.NotReady);
        }

        var stored =await reader.GetEntityAsync(run, unit, parsed.Digits, ct).ConfigureAwait(false);
        var list = RiskEvaluation.ListOf(RiskRules.Catalogue.First(s => s.Family == unit).Rule);
        var columns = Columns(list);

        RiskAssessment Judge(RiskEntityMeasures e) => RiskRules.Assess(
            RiskEvaluation.Evidence(e, run.AsOf, context.State.DaysWithData), context.Settings, context.Flagged);

        var assessment = stored is null ? null : Judge(stored);
        var level = assessment?.Level ?? RiskLevel.Observation;

        // The pattern: this entity with those bound to it in the window. Counted, never named.
        var linked = (await reader.GetLinkedAsync(run, unit, parsed.Digits, ct).ConfigureAwait(false))
            .Select(e => (e.Family, Judge(e).Level))
            .ToList();
        var pattern = RiskRules.Pattern([(unit, level), .. linked]);

        var response = new RiskEntityResponse(
            Kind(unit), unit.ToString(), stored is not null, level.ToString(), assessment?.Assessable ?? true,
            [.. (assessment?.Reasons ?? []).Select(Reason)],
            stored is null ? new Dictionary<string, object?>(StringComparer.Ordinal) : Values(stored),
            columns, context.Settings.Version, Iso(run.AsOf)!,
            pattern.ToString(),
            [.. linked.GroupBy(l => l.Family).OrderBy(g => g.Key).Select(g => new RiskLinkedInfo(
                g.Key.ToString(), g.Count(), g.Count(l => l.Level >= RiskLevel.RiskSignal), g.Count(l => l.Level == RiskLevel.Anomaly)))]);

        detail["stored"] = response.Stored;
        detail["level"] = response.Level;
        detail["pattern"] = response.Pattern;
        detail["linked"] = linked.Count;
        detail["ruleSetVersion"] = context.Settings.Version;
        detail["runId"] = run.RunId;
        await audit.WriteAsync(EntityEntry(user, http, "risk.entity", AuditOutcome.Success, detail), ct).ConfigureAwait(false);

        return Results.Ok(response);
    }

    // ------------------------------------------------------------------ shared

    /// <summary>The rule set in force for this request, read from the options as they are now.</summary>
    private static Task<RiskContext> ContextAsync(
        IRiskReader reader, IFeedQualityReader quality, IOptions<FeedQualityOptions> qualityOptions,
        IOptionsMonitor<RiskOptions> riskOptions, CancellationToken ct) =>
        RiskQueries.ContextAsync(reader, quality, qualityOptions.Value, riskOptions.CurrentValue, ct);

    /// <summary>An aggregate's answer as the route has always given it: 200, 503 or 422.</summary>
    private static IResult Answer<T>(RiskAnswer<T> answer)
        where T : class => answer.Refusal switch
        {
            RiskRefusal.None => Results.Ok(answer.Value),
            RiskRefusal.NoThreshold => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity, title: "No threshold", detail: answer.Reason),
            _ => Unavailable(answer.Reason),
        };

    /// <summary>A checked list request: who, which run, which rule and thresholds.</summary>
    private sealed record PreparedList(
        AuthenticatedUser User, RiskContext Context, RiskPublishedRun Run, RiskListQuery Query, bool Overridden, bool Reveal)
    {
        /// <summary>The rule set with this request's threshold in place of the configured one.</summary>
        public RiskSettings Settings { get; } = Context.Settings with
        {
            Thresholds = new Dictionary<RiskRule, RiskThreshold>(Context.Settings.Thresholds) { [Query.Rule] = Query.Threshold },
        };
    }

    private static async Task<(PreparedList? List, IResult? Refusal)> PrepareAsync(
        RiskListRequest request, HttpContext http, IRiskReader reader, IFeedQualityReader quality,
        IOptions<FeedQualityOptions> qualityOptions, IOptionsMonitor<RiskOptions> riskOptions, IAuditLog audit,
        bool export, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = CurrentUser.Require(http);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (!Enum.TryParse<RiskRule>(request.Rule, ignoreCase: true, out var rule) || !Enum.IsDefined(rule))
        {
            errors["rule"] = ["One of: " + string.Join(", ", Enum.GetNames<RiskRule>()) + "."];
        }

        var view = request.View switch
        {
            null or "" or "risk" => RiskView.Risk,
            "dataQuality" => RiskView.DataQuality,
            _ => (RiskView?)null,
        };
        if (view is null)
        {
            errors["view"] = ["\"risk\" or \"dataQuality\"."];
        }

        if (request.PageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"Between 1 and {MaxPageSize}."];
        }

        if (request.Page < 1 || ((long)(request.Page - 1) * Math.Max(1, request.PageSize)) >= MaxReachable)
        {
            errors["page"] = [$"From 1, within the first {MaxReachable:N0} rows."];
        }

        if (errors.Count > 0)
        {
            return (null, Results.ValidationProblem(errors));
        }

        var spec = RiskRules.Spec(rule);
        var permission = LookupPermission(spec.Family);
        var detail = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rule"] = rule.ToString(),
            ["view"] = ViewName(view!.Value),
        };

        if (!user.Can(permission))
        {
            await audit.WriteAsync(new AuditEntry(
                user.Username, export ? Permissions.DataExport : "risk.list", AuditCategory.Data, AuditOutcome.Denied, user.UserId, "risk",
                Ip: http.Connection.RemoteIpAddress?.ToString(), UserAgent: http.Request.Headers.UserAgent.ToString(),
                CorrelationId: http.TraceIdentifier, Detail: detail), ct).ConfigureAwait(false);
            return (null, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not permitted",
                detail: $"This list names {KindName(spec.Family)}s, which needs the \"{permission}\" permission."));
        }

        var context = await ContextAsync(reader, quality, qualityOptions, riskOptions, ct).ConfigureAwait(false);
        if (context.NotReady is not null || context.State.Run is not { } run)
        {
            return (null, Unavailable(context.NotReady));
        }

        if (view == RiskView.DataQuality && !RiskEvaluation.HasDataQualityView(rule))
        {
            return (null, Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["view"] = ["An all-time count has no data-quality view: it has neither a raw count nor a share set aside."],
            }));
        }

        var types = request.DeviceTypes?.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (types.Count > 0)
        {
            var known = (await reader.DeviceTypesAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            var problem = RiskEvaluation.ListOf(rule) == RiskList.Numbers
                ? "A list of numbers has no device type."
                : types.FirstOrDefault(t => !known.Contains(t)) is { } unknown
                    ? $"\"{unknown}\" is not a GSMA device type in the active version."
                    : null;
            if (problem is not null)
            {
                return (null, Results.ValidationProblem(new Dictionary<string, string[]> { ["deviceTypes"] = [problem] }));
            }
        }

        var configured = context.Settings.Thresholds.GetValueOrDefault(rule);
        var overridden = request.Threshold is not null || request.TacsThreshold is not null;
        var value = request.Threshold ?? configured?.Value;
        var tacs = rule == RiskRule.Randomisation20 ? request.TacsThreshold ?? configured?.Tacs : null;

        if (value is not { } threshold || (rule == RiskRule.Randomisation20 && tacs is null))
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "No threshold",
                detail: "This rule has no calibrated threshold. Give one with the request to list it."));
        }

        var floor = RiskEvaluation.Floor(rule, context.Options.Floors);
        if (threshold + 1 < floor || threshold < 0 || tacs < 0)
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Below the storage floor",
                detail: $"Values below {floor} are not stored, so a threshold under {floor - 1} cannot be answered from the "
                        + "risk measures. The Explorer can count smaller values directly from the history."));
        }

        var query = new RiskListQuery(
            rule, view.Value, new RiskThreshold(threshold, tacs), context.Settings.MaxDefectShare, request.Page, request.PageSize, types);

        return (new PreparedList(user, context, run, query, overridden, user.Can(Permissions.IdentifierReveal)), null);
    }

    private static RiskListRow Row(RiskEntityMeasures entity, PreparedList list)
    {
        var assessment = RiskRules.Assess(
            RiskEvaluation.Evidence(entity, list.Run.AsOf, list.Context.State.DaysWithData), list.Settings, list.Context.Flagged);

        return new RiskListRow(
            list.Reveal ? entity.Key : Mask(entity.Family, entity.Key),
            list.Reveal,
            assessment.Level.ToString(),
            assessment.Assessable,
            [.. assessment.Reasons.Select(Reason)],
            Values(entity));
    }

    private static readonly string[] IccidHeading = ["ICCID (derived from IMSI)"];

    /// <summary>A SIM's ICCID for the export, from its IMSI before masking; masked as the IMSI would be.</summary>
    internal static string? DerivedIccid(string imsi, bool reveal) =>
        Iccid.FromImsi(imsi) is { } iccid ? (reveal ? iccid : IdentifierMask.Iccid(iccid)) : null;

    /// <summary>A value as one CSV cell: a list of tags joined by spaces.</summary>
    private static object? Cell(object? value) =>
        value is IReadOnlyList<string> tags ? string.Join(" ", tags) : value;

    private static RiskReasonInfo Reason(RiskReason r) =>
        new(r.Rule.ToString(), r.Level.ToString(), r.Value, r.Threshold, r.Capped, r.Text);

    /// <summary>A list's columns. Headings name their unit.</summary>
    private static IReadOnlyList<RiskColumnInfo> Columns(RiskList list) => list switch
    {
        RiskList.Sims =>
        [
            new("imeis30", "IMEIs added, 30 days", "Number"),
            new("imeis30Raw", "IMEIs added, 30 days, before screens", "Number"),
            new("imeis7", "IMEIs added, 7 days", "Number"),
            new("imeis20", "IMEIs added, 20 days", "Number"),
            new("tacs20", "TACs, 20 days", "Number"),
            new("adds30", "Adds, 30 days", "Number"),
            new("addsSetAside30", "Adds set aside as feed defects", "Number"),
            new("maxImeisOneDay30", "Most IMEIs in one day", "Number"),
            new("maxDay30", "On", "Date"),
            new("topTacs20", "Top TACs, 20 days", "Tags"),
        ],
        RiskList.Numbers =>
        [
            new("changes7", "Days with a SIM change, 7 days", "Number"),
            new("changes7Raw", "Days with a SIM change, before screens", "Number"),
            new("lastChange", "Last SIM change", "Date"),
        ],
        _ =>
        [
            new("tac", "TAC", "Text"),
            new("brand", "Brand", "Text"),
            new("model", "Model", "Text"),
            new("sims30", "SIMs added, 30 days", "Number"),
            new("sims30Raw", "SIMs added, 30 days, before screens", "Number"),
            new("numbers30", "Numbers added, 30 days", "Number"),
            new("sims7", "SIMs added, 7 days", "Number"),
            new("addsSetAside30", "Adds set aside as feed defects", "Number"),
            new("simsEver", "SIMs ever", "Number"),
            new("numbersEver", "Numbers ever", "Number"),
            new("notRemovedDated", "SIMs not yet removed by the feed", "Number"),
            new("notRemovedDump", "SIMs known only from the initial dump", "Number"),
        ],
    };

    private static Dictionary<string, object?> Values(RiskEntityMeasures e)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (e.Sim is { } s)
        {
            values["imeis30"] = s.Imeis30;
            values["imeis30Raw"] = s.Imeis30Raw;
            values["imeis7"] = s.Imeis7;
            values["imeis20"] = s.Imeis20;
            values["tacs20"] = s.Tacs20;
            values["adds30"] = s.Adds30;
            values["addsSetAside30"] = s.AddsSetAside30;
            values["maxImeisOneDay30"] = s.MaxImeisOneDay30;
            values["maxDay30"] = Iso(s.MaxDay30);
            values["topTacs20"] = s.TopTacs20;
        }

        if (e.Family == RiskFamily.Imei)
        {
            values["tac"] = e.Tac;
            values["brand"] = e.Brand;
            values["model"] = e.Model;
            values["sims30"] = e.ImeiWindow?.Sims30;
            values["sims30Raw"] = e.ImeiWindow?.Sims30Raw;
            values["numbers30"] = e.ImeiWindow?.Numbers30;
            values["sims7"] = e.ImeiWindow?.Sims7;
            values["addsSetAside30"] = e.ImeiWindow?.AddsSetAside30;
            values["simsEver"] = e.ImeiLifetime?.SimsEver;
            values["numbersEver"] = e.ImeiLifetime?.NumbersEver;
            values["notRemovedDated"] = e.ImeiLifetime?.NotRemovedDated;
            values["notRemovedDump"] = e.ImeiLifetime?.NotRemovedDump;
        }

        if (e.Number is { } n)
        {
            values["changes7"] = n.Changes7;
            values["changes7Raw"] = n.Changes7Raw;
            values["lastChange"] = Iso(n.LastChange);
        }

        return values;
    }

    private static IResult Unavailable(string? reason) => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable, title: "Risk signals are not available", detail: reason);

    private static string LookupPermission(RiskFamily family) => family switch
    {
        RiskFamily.Number => Permissions.LookupSubscriber,
        RiskFamily.Sim => Permissions.LookupImsi,
        _ => Permissions.LookupImei,
    };

    private static string Kind(RiskFamily family) => family switch
    {
        RiskFamily.Number => "msisdn",
        RiskFamily.Sim => "imsi",
        _ => "imei",
    };

    private static string KindName(RiskFamily family) => family switch
    {
        RiskFamily.Number => "phone number",
        RiskFamily.Sim => "SIM (IMSI)",
        _ => "handset IMEI",
    };

    private static string Mask(RiskFamily family, string value) => family switch
    {
        RiskFamily.Number => IdentifierMask.Msisdn(value),
        RiskFamily.Sim => IdentifierMask.Imsi(value),
        _ => IdentifierMask.Imei(value),
    };

    private static string ViewName(RiskView view) => view == RiskView.Risk ? "risk" : "dataQuality";

    private static string? Iso(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>What was listed and at which thresholds - never which entities.</summary>
    private static AuditEntry Entry(
        AuthenticatedUser user, HttpContext http, string action, AuditOutcome outcome, PreparedList list, Dictionary<string, object?> extra)
    {
        var detail = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rule"] = list.Query.Rule.ToString(),
            ["view"] = ViewName(list.Query.View),
            ["deviceTypes"] = string.Join(",", list.Query.DeviceTypes ?? []),
            ["threshold"] = list.Query.Threshold.Value,
            ["tacsThreshold"] = list.Query.Threshold.Tacs,
            ["overridden"] = list.Overridden,
            ["page"] = list.Query.Page,
            ["pageSize"] = list.Query.PageSize,
            ["masked"] = !list.Reveal,
            ["ruleSetVersion"] = list.Context.Settings.Version,
            ["runId"] = list.Run.RunId,
        };

        foreach (var (key, value) in extra)
        {
            detail[key] = value;
        }

        return new AuditEntry(
            user.Username, action, AuditCategory.Data, outcome, user.UserId, "risk",
            Ip: http.Connection.RemoteIpAddress?.ToString(), UserAgent: http.Request.Headers.UserAgent.ToString(),
            CorrelationId: http.TraceIdentifier, Detail: detail);
    }

    private static AuditEntry EntityEntry(
        AuthenticatedUser user, HttpContext http, string action, AuditOutcome outcome, Dictionary<string, object?> detail) =>
        new(user.Username, action, AuditCategory.Data, outcome, user.UserId, "risk",
            Ip: http.Connection.RemoteIpAddress?.ToString(), UserAgent: http.Request.Headers.UserAgent.ToString(),
            CorrelationId: http.TraceIdentifier, Detail: detail);
}
