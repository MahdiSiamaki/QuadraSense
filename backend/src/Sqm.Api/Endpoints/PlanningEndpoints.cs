using System.Globalization;
using Sqm.Api.Auth;
using Sqm.Api.Infrastructure;
using Sqm.Application.Identity;
using Sqm.Application.Planning;
using Sqm.Contracts.Planning;
using Sqm.Domain.Identifiers;
using Microsoft.Extensions.Options;
using Sqm.Infrastructure.ClickHouse;

namespace Sqm.Api.Endpoints;

/// <summary>Network and product planning: the 2G/3G sunset, bands, eSIM, lifecycle, IoT and satellite views (N1-N6).</summary>
/// <remarks>
/// <para>
/// <b>Read, never computed here.</b> The per-number classes are the planning snapshot's (analytics
/// migration 030), built by the import worker per delivery; these routes filter and page them. Until a
/// run is published they answer 503 and say why.
/// </para>
/// <para>
/// <b>Permissions.</b> <c>planning.view</c> on every route. A page of numbers also needs
/// <c>lookup.subscriber</c>, <c>identifier.reveal</c> decides masking, and is audited with its class
/// and filters - never a number. An aggregate CSV needs <c>data.export</c>. A list of numbers for a
/// campaign is not exported here: that goes through the controlled export, with its own permission,
/// reason and second approval (the product owner's decision of 2026-10-21).
/// </para>
/// </remarks>
public static class PlanningEndpoints
{
    /// <summary>Rows per page of numbers, at most.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Rows paging can reach. Beyond it is a campaign list, which is the controlled export's.</summary>
    public const int MaxReachable = 10_000;

    private static readonly string[] Views = ["sunset", "esim", "iot", "bands", "ntn"];

    /// <summary>Why a list of numbers cannot be narrowed by model for this class.</summary>
    public const string NotNarrowable =
        "A list narrowed by vendor, model or device type is kept for the exposed, LTE-M / NB-IoT-only and not-assessable "
        + "classes: the planning run keeps the models of those numbers only. Clear the filter to list this class.";
    private static readonly string[] Breakdowns = ["vendor", "model", "deviceType", "tac"];
    private static readonly string[] LifecycleTables = ["vendor-moves", "model-moves", "cohorts", "device-age", "model-age"];

    /// <summary>Registers the planning routes.</summary>
    public static IEndpointRouteBuilder MapPlanningEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var builder = app.MapGroup("/api/v1/planning").WithTags("Planning")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.PlanningView);

        builder.MapGet("/status", StatusAsync)
            .WithName("GetPlanningStatus")
            .WithSummary("The published planning runs, the one being built, and the radio profiles they read.");

        builder.MapGet("/sunset", SunsetAsync)
            .WithName("GetPlanningSunset")
            .WithSummary("N1: numbers by what their handsets can use after a 2G/3G shutdown. Names nobody.");

        builder.MapGet("/esim", EsimAsync)
            .WithName("GetPlanningEsim")
            .WithSummary("N3: numbers by whether they hold an eSIM-capable handset. Names nobody.");

        builder.MapGet("/iot", IotAsync)
            .WithName("GetPlanningIot")
            .WithSummary("N5: IoT / M2M devices, their 2G/3G exposure and the SIMs they have carried. Names nobody.");

        builder.MapGet("/bands", BandsAsync)
            .WithName("GetPlanningBands")
            .WithSummary("N2: models by the LTE and NR bands they list, matched to the operator's configured bands. Names nobody.");

        builder.MapGet("/ntn", NtnAsync)
            .WithName("GetPlanningNtn")
            .WithSummary("N6: models by satellite (3GPP NTN) capability, its mode, orbits and bands. Names nobody.");

        builder.MapGet("/lifecycle", LifecycleAsync)
            .WithName("GetPlanningLifecycle")
            .WithSummary("N4: device and model age, changes of handset by kind and between models, and time to a change. Names nobody.");

        builder.MapPost("/lifecycle/export", LifecycleExportAsync)
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DataExport)
            .WithName("ExportPlanningLifecycle")
            .WithSummary("One table of the lifecycle view as CSV: the moves, the cohorts or the ages. Names nobody.");

        builder.MapGet("/breakdown", BreakdownAsync)
            .WithName("GetPlanningBreakdown")
            .WithSummary("One class of a view by vendor, model, device type or TAC. Names nobody.");

        builder.MapPost("/breakdown/export", BreakdownExportAsync)
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DataExport)
            .WithName("ExportPlanningBreakdown")
            .WithSummary("The same breakdown as CSV. Names nobody.");

        builder.MapPost("/numbers", NumbersAsync)
            .WithName("ListPlanningNumbers")
            .WithSummary("A page of the numbers in one class. Needs lookup.subscriber; audited without the numbers.");

        return app;
    }

    // ------------------------------------------------------------------ routes

    private static async Task<IResult> StatusAsync(IPlanningReader reader, CancellationToken ct) =>
        Results.Ok(await reader.GetStatusAsync(ct).ConfigureAwait(false));

    private static async Task<IResult> SunsetAsync(
        IPlanningReader reader, ulong? run, string? vendor, string? model, string? deviceType, CancellationToken ct) =>
        Answer(await reader.GetSunsetAsync(run, new PlanningFilter(vendor, model, deviceType), ct).ConfigureAwait(false));

    private static async Task<IResult> EsimAsync(
        IPlanningReader reader, ulong? run, string? vendor, string? model, string? deviceType, CancellationToken ct) =>
        Answer(await reader.GetEsimAsync(run, new PlanningFilter(vendor, model, deviceType), ct).ConfigureAwait(false));

    private static async Task<IResult> IotAsync(
        IPlanningReader reader, ulong? run, string? vendor, string? model, string? deviceType, CancellationToken ct) =>
        Answer(await reader.GetIotAsync(run, new PlanningFilter(vendor, model, deviceType), ct).ConfigureAwait(false));

    private static async Task<IResult> BandsAsync(
        IPlanningReader reader, IOptionsMonitor<OperatorBandsOptions> bands,
        ulong? run, string? vendor, string? model, string? deviceType, CancellationToken ct) =>
        Answer(await reader.GetBandsAsync(run, new PlanningFilter(vendor, model, deviceType), bands.CurrentValue, ct).ConfigureAwait(false));

    private static async Task<IResult> NtnAsync(
        IPlanningReader reader, ulong? run, string? vendor, string? model, string? deviceType, CancellationToken ct) =>
        Answer(await reader.GetNtnAsync(run, new PlanningFilter(vendor, model, deviceType), ct).ConfigureAwait(false));

    private static async Task<IResult> LifecycleAsync(
        IPlanningReader reader, ulong? run, string? vendor, string? model, string? deviceType, CancellationToken ct) =>
        Answer(await reader.GetLifecycleAsync(run, new PlanningFilter(vendor, model, deviceType), 50, ct).ConfigureAwait(false));

    private static async Task<IResult> LifecycleExportAsync(
        PlanningLifecycleExportRequest request, IPlanningReader reader, HttpContext http, IAuditLog audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var table = request.Table ?? string.Empty;
        if (!LifecycleTables.Contains(table, StringComparer.Ordinal))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["table"] = ["One of: " + string.Join(", ", LifecycleTables) + "."],
            });
        }

        var filter = new PlanningFilter(request.Vendor, request.Model, request.DeviceType);
        if (await reader.GetLifecycleAsync(request.RunId, filter, 500, ct).ConfigureAwait(false) is not { } view)
        {
            return Unavailable();
        }

        if (!view.Built)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not built for this run",
                detail: "This run was built before the lifecycle measures existed and holds none. Export from a run built since.");
        }

        object?[] Run() => [view.Run.AsOfSequence, view.Run.AsOfDate];
        string[] runHeadings = ["Delivery", "Business date"];
        static string Cohort(string cohort) => cohort == "dump" ? "Before 2026-01-26 (in the initial dump)" : cohort;

        var csv = table switch
        {
            "vendor-moves" or "model-moves" => CsvExport.Write(
                ["From", "To", "Changes", "Of which to a newer model", "Window from", "Window to", .. runHeadings],
                (table == "vendor-moves" ? view.VendorMoves : view.ModelMoves).Select(m => (IReadOnlyList<object?>)
                    [m.From, m.To, m.Changes, m.Upgrades, view.FirstMonth, view.LastMonth, .. Run()])),
            "cohorts" => CsvExport.Write(
                ["Arrived on a number", "Handsets on numbers", "Changed", "Censored",
                 "Changed within 30 days (%, empty = not yet observable)", "Changed within 90 days (%)", "Changed within 180 days (%)",
                 "Upgraded within 90 days (%)", "Upgraded within 180 days (%)", "Median days to a change (empty = not reached)", .. runHeadings],
                view.Cohorts.Concat(view.Dump is { } dump ? [dump] : []).Select(c => (IReadOnlyList<object?>)
                    [Cohort(c.Cohort), c.Spells, c.Changed, c.Censored, c.ChangedBy30, c.ChangedBy90, c.ChangedBy180,
                     c.UpgradedBy90, c.UpgradedBy180, c.MedianDays, .. Run()])),
            "device-age" => CsvExport.Write(
                ["First seen on the network", "Handsets held now", "Share (%)", .. runHeadings],
                view.DeviceAge.Select(a => (IReadOnlyList<object?>)[a.Label, a.Handsets, a.SharePercent, .. Run()])),
            _ => CsvExport.Write(
                ["GSMA allocation year of the model", "Handsets held now", "Share (%)", .. runHeadings],
                view.ModelAge.Select(a => (IReadOnlyList<object?>)[a.Label, a.Handsets, a.SharePercent, .. Run()])),
        };

        var user = CurrentUser.Require(http);
        await audit.WriteAsync(Entry(user, http, Permissions.DataExport, AuditOutcome.Success, new()
        {
            ["view"] = "lifecycle",
            ["table"] = table,
            ["run"] = view.Run.RunId,
            ["filter"] = Filter(filter),
        }), ct).ConfigureAwait(false);

        var name = string.Create(CultureInfo.InvariantCulture,
            $"planning-lifecycle-{table}-{view.Run.AsOfDate ?? view.Run.AsOfSequence.ToString(CultureInfo.InvariantCulture)}.csv");
        return Results.File(csv, "text/csv; charset=utf-8", name);
    }

    private static async Task<IResult> BreakdownAsync(
        IPlanningReader reader, IOptionsMonitor<OperatorBandsOptions> bands, string? view, string? @class, string? by, ulong? run,
        string? vendor, string? model, string? deviceType, int? limit, CancellationToken ct)
    {
        if (Invalid(view, by) is { } problem)
        {
            return problem;
        }

        return Answer(await reader.GetBreakdownAsync(
            run, view!, @class ?? string.Empty, by!, new PlanningFilter(vendor, model, deviceType), limit ?? 50,
            bands.CurrentValue, ct).ConfigureAwait(false));
    }

    private static async Task<IResult> BreakdownExportAsync(
        PlanningBreakdownRequest request, IPlanningReader reader, IOptionsMonitor<OperatorBandsOptions> bands,
        HttpContext http, IAuditLog audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Invalid(request.View, request.By) is { } problem)
        {
            return problem;
        }

        var filter = new PlanningFilter(request.Vendor, request.Model, request.DeviceType);
        if (await reader.GetBreakdownAsync(
                request.RunId, request.View!, request.Class ?? string.Empty, request.By!, filter, 500,
                bands.CurrentValue, ct).ConfigureAwait(false) is not { } breakdown)
        {
            return Unavailable();
        }

        var csv = CsvExport.Write(
            [Heading(breakdown.By), "Name", "TAC", "Numbers (estimated)", "Active bindings", "Handsets (estimated)", "View", "Class", "Delivery", "Business date"],
            breakdown.Rows.Select(r => (IReadOnlyList<object?>)
            [
                r.Key, r.Label, r.Tac, r.Numbers, r.Bindings, r.Handsets,
                breakdown.View, breakdown.Class, breakdown.Run.AsOfSequence, breakdown.Run.AsOfDate,
            ]));

        var user = CurrentUser.Require(http);
        await audit.WriteAsync(Entry(user, http, Permissions.DataExport, AuditOutcome.Success, new()
        {
            ["view"] = breakdown.View,
            ["class"] = breakdown.Class,
            ["by"] = breakdown.By,
            ["run"] = breakdown.Run.RunId,
            ["filter"] = Filter(filter),
            ["exported"] = breakdown.Rows.Count,
        }), ct).ConfigureAwait(false);

        var name = string.Create(CultureInfo.InvariantCulture,
            $"planning-{breakdown.View}-{breakdown.Class}-by-{breakdown.By}-{breakdown.Run.AsOfDate ?? breakdown.Run.AsOfSequence.ToString(CultureInfo.InvariantCulture)}.csv");
        return Results.File(csv, "text/csv; charset=utf-8", name);
    }

    private static async Task<IResult> NumbersAsync(
        PlanningNumberRequest request, HttpContext http, IPlanningReader reader, IAuditLog audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = CurrentUser.Require(http);
        var view = request.View ?? "sunset";
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (view is not ("sunset" or "esim"))
        {
            errors["view"] = ["\"sunset\" or \"esim\"."];
        }

        if (request.PageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"Between 1 and {MaxPageSize}."];
        }

        if (request.Page < 1 || ((long)(request.Page - 1) * Math.Max(1, request.PageSize)) >= MaxReachable)
        {
            errors["page"] = [$"From 1, within the first {MaxReachable:N0} rows. A whole class is a campaign list: the controlled export."];
        }

        var filter = new PlanningFilter(request.Vendor, request.Model, request.DeviceType);
        if (filter.Any && !ClickHousePlanningReader.KeepsModels(view, request.Class ?? string.Empty))
        {
            errors["vendor"] = [NotNarrowable];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var detail = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["view"] = view,
            ["class"] = request.Class,
            ["reason"] = request.Reason,
            ["filter"] = Filter(filter),
            ["page"] = request.Page,
        };

        if (!user.Can(Permissions.LookupSubscriber))
        {
            await audit.WriteAsync(Entry(user, http, "planning.numbers", AuditOutcome.Denied, detail), ct).ConfigureAwait(false);
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not permitted",
                detail: "A list of numbers needs the \"lookup.subscriber\" permission. The figures above it do not.");
        }

        var read = await reader.ListNumbersAsync(
            request.RunId, view, request.Class ?? string.Empty, request.Reason, filter, request.Page, request.PageSize, ct).ConfigureAwait(false);
        if (read is not { } page)
        {
            return Unavailable("No published run holds its numbers: only the two newest runs keep them.");
        }

        var reveal = user.Can(Permissions.IdentifierReveal);
        var reasons = ClickHousePlanningReader.Reasons.ToDictionary(r => r.Code, r => r.Label);
        var rows = page.Rows.Select(r => new PlanningNumberRow(
            reveal ? r.Msisdn.ToString(CultureInfo.InvariantCulture) : IdentifierMask.Msisdn(r.Msisdn),
            reasons.GetValueOrDefault(r.Reason),
            r.Handsets, r.Bindings, r.Models)).ToList();

        detail["returned"] = rows.Count;
        detail["total"] = page.Total;
        detail["run"] = page.Run.RunId;
        await audit.WriteAsync(Entry(user, http, "planning.numbers", AuditOutcome.Success, detail), ct).ConfigureAwait(false);

        return Results.Ok(new PlanningNumberPage(page.Run, request.Class ?? string.Empty, rows, page.Total, request.Page, request.PageSize, !reveal));
    }

    // ------------------------------------------------------------------ helpers

    private static IResult Answer<T>(T? response) where T : class =>
        response is null ? Unavailable() : Results.Ok(response);

    private static IResult Unavailable(string? detail = null) => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "No planning run yet",
        detail: detail ?? "The planning figures are built by the import worker when it is idle, after the radio profiles of the "
                + "active GSMA version are parsed. None is published yet, or the run asked for is no longer held.");

    private static IResult? Invalid(string? view, string? by)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (view is null || !Views.Contains(view, StringComparer.Ordinal))
        {
            errors["view"] = ["One of: " + string.Join(", ", Views) + "."];
        }

        if (by is null || !Breakdowns.Contains(by, StringComparer.Ordinal))
        {
            errors["by"] = ["One of: " + string.Join(", ", Breakdowns) + "."];
        }

        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    private static string Heading(string by) => by switch
    {
        "vendor" => "Vendor",
        "model" => "Model",
        "deviceType" => "Device type",
        _ => "TAC",
    };

    private static Dictionary<string, string?> Filter(PlanningFilter filter) => new(StringComparer.Ordinal)
    {
        ["vendor"] = filter.Vendor,
        ["model"] = filter.Model,
        ["deviceType"] = filter.DeviceType,
    };

    private static AuditEntry Entry(
        AuthenticatedUser user, HttpContext http, string action, AuditOutcome outcome, Dictionary<string, object?> detail) =>
        new(user.Username, action, AuditCategory.Data, outcome, user.UserId, "planning",
            Ip: http.Connection.RemoteIpAddress?.ToString(), UserAgent: http.Request.Headers.UserAgent.ToString(),
            CorrelationId: http.TraceIdentifier, Detail: detail);
}
