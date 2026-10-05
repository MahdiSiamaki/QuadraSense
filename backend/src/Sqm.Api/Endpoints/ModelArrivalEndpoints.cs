using System.Globalization;
using Sqm.Api.Auth;
using Sqm.Application.Devices;
using Sqm.Application.Identity;
using Sqm.Contracts.Devices;

namespace Sqm.Api.Endpoints;

/// <summary>New models and network age, from the per-model day counts (analytics migration 025).</summary>
/// <remarks>
/// <para>
/// <b>First seen, not launched.</b> A model's first appearance is the first daily file that named one
/// of its handsets. Models the initial dump listed were seen before the data starts and are never
/// "new"; the first weeks after 2026-01-26 also fill in models the dump's month happened to miss.
/// </para>
/// <para>
/// <b>Network age is time in this data</b> - days since first seen to the data-through day - and is
/// never called an age: the feed has no manufacturing or activation date. For anything in the dump it
/// is a lower bound.
/// </para>
/// <para>Models are not personal data: <c>device.view</c>, as the rest of the device module.</para>
/// </remarks>
public static class ModelArrivalEndpoints
{
    /// <summary>Rows per page, at most.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Registers the routes.</summary>
    public static IEndpointRouteBuilder MapModelArrivalEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/devices").WithTags("Devices")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DeviceView);

        group.MapGet("/arrivals/status", StatusAsync)
            .WithName("GetModelArrivalStatus")
            .WithSummary("Whether first appearances cover the whole event log.");

        group.MapGet("/new-models", NewModelsAsync)
            .WithName("ListNewModels")
            .WithSummary("Models first seen in a period - not in the initial dump - newest first.");

        group.MapGet("/arrivals", ArrivalsAsync)
            .WithName("GetModelArrivals")
            .WithSummary("How many models first appeared per month or week.");

        group.MapGet("/{tac:regex(^[0-9]{{8}}$)}/network-age", NetworkAgeAsync)
            .WithName("GetModelNetworkAge")
            .WithSummary("One model's first appearance and network age.");

        return app;
    }

    private static string? Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? NotReady(ModelArrivalState state) =>
        !state.Deployed ? "New models are not deployed on this server (analytics migration 025)."
        : !state.Complete ? $"First appearances are still being written ({state.DaysWritten} of {state.DaysInLog} days); "
                            + "run Sqm.Ingestion --refresh-tac-days."
        : null;

    private static async Task<IResult> StatusAsync(IModelArrivalReader reader, CancellationToken ct)
    {
        var state = await reader.GetStateAsync(ct).ConfigureAwait(false);
        return Results.Ok(new ModelArrivalStatus(NotReady(state) is null, NotReady(state), Iso(state.DataThrough), Iso(NetworkAge.DataStart)!));
    }

    private static async Task<IResult> NewModelsAsync(
        IModelArrivalReader reader, CancellationToken ct,
        DateOnly? from = null, DateOnly? to = null, string? brand = null, string? deviceType = null,
        bool knownOnly = true, int page = 1, int pageSize = 50)
    {
        var state = await reader.GetStateAsync(ct).ConfigureAwait(false);
        if (NotReady(state) is { } reason || state.DataThrough is not { } through)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Not available",
                detail: NotReady(state) ?? "No day is written yet.");
        }

        var end = to ?? through;
        var start = from ?? end.AddDays(-29);
        if (page < 1 || pageSize is < 1 or > MaxPageSize || start > end || (brand?.Length ?? 0) > 100 || (deviceType?.Length ?? 0) > 100)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["query"] = [$"from <= to; page from 1; pageSize 1 to {MaxPageSize}; brand and deviceType up to 100 characters."],
            });
        }

        var result = await reader.ListAsync(
            new NewModelQuery(start, end, string.IsNullOrWhiteSpace(brand) ? null : brand.Trim(),
                string.IsNullOrWhiteSpace(deviceType) ? null : deviceType, knownOnly, page, pageSize), ct).ConfigureAwait(false);

        return Results.Ok(new NewModelsResponse(
            [.. result.Rows.Select(r => new NewModelInfo(
                r.Tac, r.Brand, r.Model, r.DeviceType, Iso(r.FirstSeen)!, NetworkAge.Days(r.FirstSeen, false, through),
                r.DaysSeen, r.FirstDayImeis, r.Handsets, r.Sims))],
            result.Total, page, pageSize, Iso(start)!, Iso(end)!, Iso(through)));
    }

    private static async Task<IResult> ArrivalsAsync(IModelArrivalReader reader, CancellationToken ct, string grain = "month")
    {
        if (grain is not ("month" or "week"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["grain"] = ["month or week."] });
        }

        var state = await reader.GetStateAsync(ct).ConfigureAwait(false);
        if (NotReady(state) is { } reason)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Not available", detail: reason);
        }

        var periods = await reader.ArrivalsAsync(grain == "week", ct).ConfigureAwait(false);
        return Results.Ok(new ModelArrivalsResponse(
            grain, [.. periods.Select(p => new ModelArrivalPeriodInfo(Iso(p.PeriodStart)!, p.Models, p.KnownModels))],
            Iso(NetworkAge.DataStart)!));
    }

    private static async Task<IResult> NetworkAgeAsync(string tac, IModelArrivalReader reader, CancellationToken ct)
    {
        var state = await reader.GetStateAsync(ct).ConfigureAwait(false);
        if (NotReady(state) is { } reason)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Not available", detail: reason);
        }

        if (await reader.FirstSeenAsync(tac, ct).ConfigureAwait(false) is not { } seen)
        {
            return Results.NotFound();
        }

        return Results.Ok(new ModelNetworkAgeResponse(
            tac, seen.InDump, Iso(seen.FirstSeen), NetworkAge.Days(seen.FirstSeen, seen.InDump, state.DataThrough),
            seen.InDump, seen.DaysSeen, Iso(state.DataThrough)));
    }
}
