using Sqm.Api.Auth;
using Sqm.Application.Abstractions;
using Sqm.Application.Identity;
using Sqm.Contracts.Lookup;
using Sqm.Domain.Identifiers;

namespace Sqm.Api.Endpoints;

/// <summary>What the caller wants to explore.</summary>
/// <param name="Identifier">A phone number, a SIM identity or a handset. Digits, any spacing.</param>
public sealed record RelationshipRequest(string Identifier);

/// <summary>
/// The relationships between a number, a SIM and a handset, from whichever one you hold.
/// </summary>
/// <remarks>
/// <para>
/// <b>POST, not GET.</b> The identifier is the most sensitive value in this product and it travels
/// in the body for the same reason the subscriber lookup does: a query string reaches server
/// access logs, proxy logs and browser history, none of which are access-controlled.
/// </para>
/// <para>
/// <b>Permission is per identifier kind, not per page.</b> A relationship has two ends and seeing
/// it means seeing both, so the centre requires the permission for its own kind and each section
/// requires the permission for the kind it contains. A caller who may look up a number but not a
/// handset gets the SIMs and is told the handsets were withheld - an empty list would read as
/// "this number has no devices", which is a different and false statement.
/// </para>
/// <para>
/// Every call is audited, including the refusals. The permission checks happen inside the handler
/// rather than on the route, which puts them outside the pipeline's automatic audit - the same
/// trap <see cref="DeviceEndpoints"/> documents, where a check made this way once left a refusal
/// unrecorded.
/// </para>
/// </remarks>
public static class RelationshipEndpoints
{
    /// <summary>Registers the relationship routes.</summary>
    public static IEndpointRouteBuilder MapRelationshipEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Authentication at the group; the permission that matters depends on what was asked for
        // and is therefore decided in the handler.
        var group = app.MapGroup("/api/v1/relationships")
            .RequireAuthorization()
            .WithTags("Relationships");

        group.MapPost("/explore", ExploreAsync)
            .WithName("ExploreRelationships")
            .WithSummary(
                "Everything connected to one identifier: its numbers, SIMs, handsets, and any "
                + "handset shown to share a physical device with them.");

        return app;
    }

    private static async Task<IResult> ExploreAsync(
        RelationshipRequest request,
        HttpContext http,
        IDeviceAnalyticsStore store,
        IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);

        var user = CurrentUser.Require(http);

        // The same classifier the Devices search uses: length alone decides, because the three
        // lengths are disjoint in this feed. One rule, so an identifier cannot mean one thing on
        // this page and another on that one.
        var term = DeviceSearchTerm.Classify(request.Identifier);

        var kind = term?.Kind switch
        {
            DeviceSearchKind.Msisdn => RelatedKind.Msisdn,
            DeviceSearchKind.Imsi => RelatedKind.Imsi,
            DeviceSearchKind.Imei => RelatedKind.Imei,
            _ => (RelatedKind?)null,
        };

        if (kind is not { } centre || term is not { } parsed)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["identifier"] =
                [
                    "Enter a phone number (10 digits), a SIM identity (15 digits) or a handset "
                    + "(14 digits). Length alone decides which, because the three lengths do not "
                    + "overlap in this feed.",
                ],
            });
        }

        // The centre's own kind is the gate. Without it there is nothing to show and nothing to
        // partially withhold - the request itself is the thing being refused.
        var centrePermission = PermissionFor(centre);

        if (!user.Can(centrePermission))
        {
            await audit.WriteAsync(Entry(
                user, http, centrePermission, AuditOutcome.Denied,
                new Dictionary<string, object?> { ["kind"] = centre.ToString() }), ct)
                .ConfigureAwait(false);

            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Not permitted",
                detail: $"Exploring a {Describe(centre)} needs the \"{centrePermission}\" permission.");
        }

        var allowed = new RelationshipSections(
            Subscribers: user.Can(Permissions.LookupSubscriber),
            Sims: user.Can(Permissions.LookupImsi),
            Handsets: user.Can(Permissions.LookupImei));

        var graph = await store
            .GetRelationshipsAsync(centre, parsed.Digits, allowed, ct).ConfigureAwait(false);

        // Recorded with counts rather than with the identifiers themselves. The identifier is in
        // the audit's own subject field; repeating every neighbour would turn the audit log into a
        // second copy of the data it exists to police.
        await audit.WriteAsync(Entry(
            user, http, centrePermission, AuditOutcome.Success,
            new Dictionary<string, object?>
            {
                ["kind"] = centre.ToString(),
                ["found"] = graph.Found,
                ["subscribers"] = graph.Subscribers.Count,
                ["sims"] = graph.Sims.Count,
                ["handsets"] = graph.Handsets.Count,
                ["paired"] = graph.Paired.Count,
                ["withheld"] = string.Join(",", graph.Withheld),
            }), ct).ConfigureAwait(false);

        return Results.Ok(user.Can(Permissions.IdentifierReveal) ? graph : Masked(graph));
    }

    /// <summary>The graph with every neighbour's identifier redacted.</summary>
    /// <remarks>
    /// identifier.reveal is what decides complete identifiers everywhere else - IMSI search, a
    /// device's identifier list - and this page ignored it, so a user whose reveal was denied saw
    /// every IMSI, IMEI and number here in full. The centre is left as typed: masking an
    /// identifier back to the person who supplied it protects nothing. TACs and models stay, as
    /// they do elsewhere - they describe devices, not people.
    /// </remarks>
    private static RelationshipGraph Masked(RelationshipGraph graph) => graph with
    {
        Subscribers = [.. graph.Subscribers.Select(n => n with { Value = IdentifierMask.Msisdn(n.Value) })],
        Sims = [.. graph.Sims.Select(n => n with { Value = IdentifierMask.Imsi(n.Value) })],
        Handsets = [.. graph.Handsets.Select(n => n with { Value = IdentifierMask.Imei(n.Value) })],
        Paired = [.. graph.Paired.Select(p => p with { Imei = IdentifierMask.Imei(p.Imei) })],
        Identifiers = IdentifierVisibility.Masked,
    };

    private static string PermissionFor(RelatedKind kind) => kind switch
    {
        RelatedKind.Msisdn => Permissions.LookupSubscriber,
        RelatedKind.Imsi => Permissions.LookupImsi,
        _ => Permissions.LookupImei,
    };

    private static string Describe(RelatedKind kind) => kind switch
    {
        RelatedKind.Msisdn => "phone number",
        RelatedKind.Imsi => "SIM identity",
        _ => "handset",
    };

    private static AuditEntry Entry(
        AuthenticatedUser user, HttpContext http, string action, AuditOutcome outcome,
        Dictionary<string, object?> detail) =>
        new(
            ActorName: user.Username,
            Action: action,
            Category: AuditCategory.Data,
            Outcome: outcome,
            ActorUserId: user.UserId,
            TargetType: "relationship",
            Ip: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            CorrelationId: http.TraceIdentifier,
            Detail: detail);
}
