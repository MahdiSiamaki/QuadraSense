using Sqm.Api.Auth;
using Sqm.Application.Identity;

namespace Sqm.Api.Endpoints;

/// <summary>The audit log.</summary>
/// <remarks>
/// Read-only, and there is no write endpoint by design. Entries are written by the code that
/// performs the action, inside the same transaction where one exists - an API that accepts audit
/// entries would let a caller author the record of what they did.
/// </remarks>
public static class AuditEndpoints
{
    /// <summary>Registers the audit routes.</summary>
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/audit")
            .WithTags("Audit")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.AuditView);

        group.MapGet("/", QueryAsync)
            .WithName("QueryAuditLog")
            .WithSummary("Audit entries, filtered and paged, newest first.");

        group.MapGet("/actions", ActionsAsync)
            .WithName("ListAuditActions")
            .WithSummary("Distinct action codes present in the log, for the filter.");

        return app;
    }

    private static async Task<IResult> QueryAsync(
        IAuditLog audit,
        string? search,
        string? action,
        string? category,
        string? outcome,
        long? actorUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        // Nullable for the same reason as the user list: a non-nullable int is a REQUIRED query
        // parameter to a minimal API.
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        // Both filters parse leniently: an unrecognised value becomes "no filter" rather than a
        // validation error, because these arrive from URL state and a stale link should show the
        // log rather than an error page.
        AuditCategory? parsedCategory =
            Enum.TryParse<AuditCategory>(category, ignoreCase: true, out var c) && Enum.IsDefined(c) ? c : null;

        AuditOutcome? parsedOutcome =
            Enum.TryParse<AuditOutcome>(outcome, ignoreCase: true, out var o) && Enum.IsDefined(o) ? o : null;

        var result = await audit.QueryAsync(new AuditQuery(
            search, action, parsedCategory, parsedOutcome, actorUserId, from, to,
            page is > 0 ? page.Value : 1, pageSize is > 0 ? pageSize.Value : 50), ct)
            .ConfigureAwait(false);

        return Results.Ok(result);
    }

    private static async Task<IResult> ActionsAsync(IAuditLog audit, CancellationToken ct) =>
        Results.Ok(await audit.GetActionsAsync(ct).ConfigureAwait(false));
}
