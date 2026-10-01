using Microsoft.Extensions.Options;
using Sqm.Api.Auth;
using Sqm.Application.Identity;
using Sqm.Application.Timeline;
using Sqm.Contracts.Timeline;
using Sqm.Domain.Identifiers;

namespace Sqm.Api.Endpoints;

/// <summary>The timeline of one number, SIM or handset.</summary>
/// <remarks>
/// <para>
/// <b>Permission is per identifier kind,</b> as on the Relationships page and in the entity
/// summary: the centre needs the lookup permission of its own kind, and each related kind is
/// returned only to someone who may look that kind up - and is reported as withheld otherwise.
/// <c>identifier.reveal</c> decides masking.
/// </para>
/// <para>
/// <b>Not served from an incomplete history.</b> Until the backfill has written every day, a
/// timeline would silently miss the days not yet written; the answer is a 503 that says so.
/// </para>
/// <para>
/// POST, identifier in the body: never in a URL. Audited with the kind and the size of the
/// answer, never the identifier.
/// </para>
/// </remarks>
public static class TimelineEndpoints
{
    /// <summary>Registers the timeline route.</summary>
    public static IEndpointRouteBuilder MapTimelineEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/v1/timeline", TimelineAsync)
            .RequireAuthorization()
            .WithTags("Timeline")
            .WithName("GetTimeline")
            .WithSummary("Every binding of one number, SIM or handset over time, from the binding history.");

        return app;
    }

    private static async Task<IResult> TimelineAsync(
        TimelineRequest request, HttpContext http, ITimelineStore store, IAuditLog audit,
        IOptions<TimelineOptions> options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = CurrentUser.Require(http);
        var term = DeviceSearchTerm.Classify(request.Identifier);

        var (centre, permission) = term?.Kind switch
        {
            DeviceSearchKind.Msisdn => (TimelineCentre.Msisdn, Permissions.LookupSubscriber),
            DeviceSearchKind.Imsi => (TimelineCentre.Imsi, Permissions.LookupImsi),
            DeviceSearchKind.Imei => (TimelineCentre.Imei, Permissions.LookupImei),
            _ => ((TimelineCentre?)null, (string?)null),
        };

        if (centre is not { } kind || permission is null || term is not { } parsed)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["identifier"] =
                [
                    "A phone number (10 digits), a SIM (15) or a handset (14). A TAC is a model with "
                    + "up to millions of bindings - open it on its device page instead.",
                ],
            });
        }

        if (!user.Can(permission))
        {
            await audit.WriteAsync(Entry(user, http, permission, AuditOutcome.Denied, kind, null), ct).ConfigureAwait(false);
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not permitted",
                detail: $"The timeline of a {TimelineBuilder.Kind(kind)} needs the \"{permission}\" permission.");
        }

        switch (await store.GetReadinessAsync(ct).ConfigureAwait(false))
        {
            case HistoryReadiness.NotDeployed:
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Timelines are not available yet",
                    detail: "The binding history has not been deployed on this server (analytics migration 022).");

            case HistoryReadiness.Building:
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "The binding history is still being built",
                    detail: "Timelines are shown once every day of the event log is in the history; until then one "
                            + "would silently miss the days not yet written.");
        }

        var max = Math.Max(1, options.Value.MaxBindings);
        var stored = await store.GetAsync(kind, parsed.Digits, max, ct).ConfigureAwait(false);
        var through = await store.DataThroughAsync(ct).ConfigureAwait(false);
        var reveal = user.Can(Permissions.IdentifierReveal);

        var visibility = new TimelineVisibility(
            Numbers: user.Can(Permissions.LookupSubscriber),
            Sims: user.Can(Permissions.LookupImsi),
            Handsets: user.Can(Permissions.LookupImei),
            Write: reveal ? (_, v) => v : Mask,
            Masked: !reveal);

        var timeline = TimelineBuilder.Build(kind, parsed.Digits, stored, visibility, max, through);

        await audit.WriteAsync(Entry(user, http, permission, AuditOutcome.Success, kind, new()
        {
            ["bindings"] = timeline.Summary.Bindings,
            ["truncated"] = timeline.Truncated,
            ["withheld"] = string.Join(",", timeline.Withheld),
            ["masked"] = timeline.Masked,
            ["elapsedMs"] = stored.ElapsedMs,
        }), ct).ConfigureAwait(false);

        return Results.Ok(timeline);
    }

    private static string Mask(TimelineCentre kind, string value) => kind switch
    {
        TimelineCentre.Msisdn => IdentifierMask.Msisdn(value),
        TimelineCentre.Imsi => IdentifierMask.Imsi(value),
        _ => IdentifierMask.Imei(value),
    };

    /// <summary>Who asked for which kind's timeline, and how big it was - never the identifier.</summary>
    private static AuditEntry Entry(
        AuthenticatedUser user, HttpContext http, string permission, AuditOutcome outcome, TimelineCentre kind,
        Dictionary<string, object?>? extra)
    {
        var detail = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = TimelineBuilder.Kind(kind) };
        foreach (var (key, value) in extra ?? [])
        {
            detail[key] = value;
        }

        return new AuditEntry(
            user.Username, permission, AuditCategory.Data, outcome, user.UserId, "timeline",
            Ip: http.Connection.RemoteIpAddress?.ToString(), UserAgent: http.Request.Headers.UserAgent.ToString(),
            CorrelationId: http.TraceIdentifier, Detail: detail);
    }
}
