using System.Globalization;
using Sqm.Api.Auth;
using Sqm.Application.Dashboards;
using Sqm.Application.Identity;
using Sqm.Contracts.Dashboards;

namespace Sqm.Api.Endpoints;

/// <summary>The dashboard builder: dashboards, their grants and each person's preferences (ADR-016).</summary>
/// <remarks>
/// <para>
/// Under <c>/api/v1/dashboards</c> - plural - beside the analytics under <c>/api/v1/dashboard</c>,
/// whose routes keep their meaning.
/// </para>
/// <para>
/// <b>Thin on purpose.</b> Every rule - who may open, change, share or delete a dashboard, what a
/// definition may hold - lives in <see cref="DashboardService"/> and <see cref="DashboardAccess"/>;
/// here a request is bound, handed over, and the outcome turned into a status code. A dashboard the
/// caller cannot open answers 404, never 403, so an id reveals nothing.
/// </para>
/// <para>
/// <b>Two permission checks, never one.</b> dashboard.view admits the routes; what a widget shows is
/// checked again by its own endpoint, for the person looking, with their own permissions.
/// </para>
/// </remarks>
public static class DashboardBuilderEndpoints
{
    /// <summary>Registers the dashboard builder's routes.</summary>
    public static IEndpointRouteBuilder MapDashboardBuilderEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/dashboards").WithTags("Dashboards")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DashboardView);

        group.MapGet("", ListAsync)
            .WithName("ListDashboards")
            .WithSummary("The caller's dashboards, those shared with them, their default, and the limits.");

        group.MapPost("", CreateAsync)
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DashboardCreate)
            .WithName("CreateDashboard")
            .WithSummary("Creates a private dashboard from a validated definition.");

        group.MapGet("/widget-types", WidgetTypes)
            .WithName("GetDashboardWidgetTypes")
            .WithSummary("Which widget types the caller may use, decided from their permissions.");

        group.MapGet("/share-directory", ShareDirectoryAsync)
            .WithName("SearchDashboardShareDirectory")
            .WithSummary("Active users and roles to share with: names and usernames only.");

        group.MapPut("/preferences/default", SetDefaultAsync)
            .WithName("SetDefaultDashboard")
            .WithSummary("Sets what / opens for the caller: a dashboard, a template, or neither.");

        group.MapPost("/import", ImportAsync)
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DashboardCreate)
            .WithName("ImportDashboard")
            .WithSummary("Creates a private dashboard from a config file, with new widget ids.");

        group.MapGet("/{id:long}", GetAsync)
            .WithName("GetDashboard")
            .WithSummary("One dashboard, with what the caller may do with it.");

        group.MapPut("/{id:long}", UpdateAsync)
            .WithName("UpdateDashboard")
            .WithSummary("Replaces a dashboard if it is still at the revision the change started from; otherwise 409.");

        group.MapDelete("/{id:long}", DeleteAsync)
            .WithName("DeleteDashboard")
            .WithSummary("Deletes a dashboard - the arrangement only, never any data.");

        group.MapPost("/{id:long}/duplicate", DuplicateAsync)
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DashboardCreate)
            .WithName("DuplicateDashboard")
            .WithSummary("Copies a dashboard the caller can open into a private one. Grants are not copied.");

        group.MapGet("/{id:long}/config", ExportConfigAsync)
            .WithName("ExportDashboardConfig")
            .WithSummary("The dashboard's structure as a file: no results, owner, grants or preferences.");

        group.MapPut("/{id:long}/favorite", (long id, HttpContext http, DashboardService service, CancellationToken ct) =>
                SetFavoriteAsync(id, true, http, service, ct))
            .WithName("FavoriteDashboard")
            .WithSummary("Marks a dashboard as a favourite of the caller's.");

        group.MapDelete("/{id:long}/favorite", (long id, HttpContext http, DashboardService service, CancellationToken ct) =>
                SetFavoriteAsync(id, false, http, service, ct))
            .WithName("UnfavoriteDashboard")
            .WithSummary("Removes a dashboard from the caller's favourites.");

        group.MapGet("/{id:long}/grants", ListGrantsAsync)
            .WithName("ListDashboardGrants")
            .WithSummary("Who a dashboard is shared with, for its owner or dashboard.manage.");

        group.MapPost("/{id:long}/grants", AddGrantAsync)
            .WithName("AddDashboardGrant")
            .WithSummary("Shares a dashboard with a user or a role, as viewer or editor.");

        group.MapPut("/{id:long}/grants/{grantId:long}", UpdateGrantAsync)
            .WithName("UpdateDashboardGrant")
            .WithSummary("Changes a grant between viewer and editor.");

        group.MapDelete("/{id:long}/grants/{grantId:long}", RemoveGrantAsync)
            .WithName("RemoveDashboardGrant")
            .WithSummary("Revokes a grant; it takes effect on the grantee's next request.");

        return app;
    }

    // ------------------------------------------------------------------ routes

    private static async Task<IResult> ListAsync(HttpContext http, DashboardService service, CancellationToken ct, string? scope = null) =>
        Results.Ok(await service.ListAsync(CurrentUser.Require(http), string.Equals(scope, "all", StringComparison.Ordinal), ct).ConfigureAwait(false));

    private static async Task<IResult> GetAsync(long id, HttpContext http, DashboardService service, CancellationToken ct) =>
        ToResult(await service.GetAsync(CurrentUser.Require(http), id, ct).ConfigureAwait(false), Results.Ok);

    private static async Task<IResult> CreateAsync(
        CreateDashboardRequest request, HttpContext http, DashboardService service, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await service.CreateAsync(CurrentUser.Require(http), request, CurrentUser.Context(http), ct).ConfigureAwait(false);
        return ToResult(result, d => Results.Created($"/api/v1/dashboards/{d.Id}", d));
    }

    private static async Task<IResult> UpdateAsync(
        long id, UpdateDashboardRequest request, HttpContext http, DashboardService service, IAuditLog audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await service.UpdateAsync(CurrentUser.Require(http), id, request, CurrentUser.Context(http), ct).ConfigureAwait(false);
        return await AuditedAsync(result, http, audit, "dashboard.update", id, Results.Ok, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> DeleteAsync(long id, HttpContext http, DashboardService service, IAuditLog audit, CancellationToken ct)
    {
        var result = await service.DeleteAsync(CurrentUser.Require(http), id, CurrentUser.Context(http), ct).ConfigureAwait(false);
        return await AuditedAsync(result, http, audit, "dashboard.delete", id, _ => Results.NoContent(), ct).ConfigureAwait(false);
    }

    private static async Task<IResult> DuplicateAsync(
        long id, DuplicateDashboardRequest? request, HttpContext http, DashboardService service, CancellationToken ct)
    {
        var result = await service.DuplicateAsync(
            CurrentUser.Require(http), id, request ?? new DuplicateDashboardRequest(null), CurrentUser.Context(http), ct).ConfigureAwait(false);
        return ToResult(result, d => Results.Created($"/api/v1/dashboards/{d.Id}", d));
    }

    private static async Task<IResult> ImportAsync(
        ImportDashboardRequest request, HttpContext http, DashboardService service, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await service.ImportAsync(CurrentUser.Require(http), request, CurrentUser.Context(http), ct).ConfigureAwait(false);
        return ToResult(result, d => Results.Created($"/api/v1/dashboards/{d.Id}", d));
    }

    private static async Task<IResult> ExportConfigAsync(long id, HttpContext http, DashboardService service, CancellationToken ct) =>
        ToResult(await service.ExportConfigAsync(CurrentUser.Require(http), id, CurrentUser.Context(http), ct).ConfigureAwait(false), Results.Ok);

    private static IResult WidgetTypes(HttpContext http, DashboardService service) =>
        Results.Ok(service.WidgetTypes(CurrentUser.Require(http)));

    private static async Task<IResult> ShareDirectoryAsync(HttpContext http, DashboardService service, CancellationToken ct, string? q = null) =>
        ToResult(await service.SearchDirectoryAsync(CurrentUser.Require(http), q, ct).ConfigureAwait(false), Results.Ok);

    private static async Task<IResult> SetDefaultAsync(
        SetDefaultDashboardRequest request, HttpContext http, DashboardService service, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ToResult(await service.SetDefaultAsync(CurrentUser.Require(http), request, ct).ConfigureAwait(false), _ => Results.NoContent());
    }

    private static async Task<IResult> SetFavoriteAsync(long id, bool favorite, HttpContext http, DashboardService service, CancellationToken ct) =>
        ToResult(await service.SetFavoriteAsync(CurrentUser.Require(http), id, favorite, ct).ConfigureAwait(false), _ => Results.NoContent());

    private static async Task<IResult> ListGrantsAsync(long id, HttpContext http, DashboardService service, CancellationToken ct) =>
        ToResult(await service.ListGrantsAsync(CurrentUser.Require(http), id, ct).ConfigureAwait(false), Results.Ok);

    private static async Task<IResult> AddGrantAsync(
        long id, CreateDashboardGrantRequest request, HttpContext http, DashboardService service, IAuditLog audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await service.AddGrantAsync(CurrentUser.Require(http), id, request, CurrentUser.Context(http), ct).ConfigureAwait(false);
        return await AuditedAsync(result, http, audit, "dashboard.share.grant", id,
            g => Results.Created($"/api/v1/dashboards/{id}/grants/{g.Id}", g), ct).ConfigureAwait(false);
    }

    private static async Task<IResult> UpdateGrantAsync(
        long id, long grantId, UpdateDashboardGrantRequest request, HttpContext http, DashboardService service, IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await service.UpdateGrantAsync(CurrentUser.Require(http), id, grantId, request, CurrentUser.Context(http), ct).ConfigureAwait(false);
        return await AuditedAsync(result, http, audit, "dashboard.share.update", id, Results.Ok, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> RemoveGrantAsync(
        long id, long grantId, HttpContext http, DashboardService service, IAuditLog audit, CancellationToken ct)
    {
        var result = await service.RemoveGrantAsync(CurrentUser.Require(http), id, grantId, CurrentUser.Context(http), ct).ConfigureAwait(false);
        return await AuditedAsync(result, http, audit, "dashboard.share.revoke", id, _ => Results.NoContent(), ct).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------- results

    /// <summary>
    /// A refusal made inside a handler is outside the pipeline's automatic audit of 403s, so the
    /// endpoints that change or share a dashboard write their own Denied entry - with the dashboard's
    /// id, never its content.
    /// </summary>
    private static async Task<IResult> AuditedAsync<T>(
        DashboardResult<T> result, HttpContext http, IAuditLog audit, string action, long id, Func<T, IResult> ok, CancellationToken ct)
    {
        if (result.Outcome == DashboardOutcome.Forbidden)
        {
            var user = CurrentUser.Require(http);
            var context = CurrentUser.Context(http);
            await audit.WriteAsync(new AuditEntry(
                user.Username, action, AuditCategory.Data, AuditOutcome.Denied, user.UserId,
                "dashboard", id.ToString(CultureInfo.InvariantCulture), null,
                context.Ip, context.UserAgent, context.CorrelationId,
                new Dictionary<string, object?> { ["reason"] = result.Message }), ct).ConfigureAwait(false);
        }

        return ToResult(result, ok);
    }

    /// <summary>Turns a service outcome into the response the API promises for it.</summary>
    internal static IResult ToResult<T>(DashboardResult<T> result, Func<T, IResult> ok)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(ok);

        return result.Outcome switch
        {
            DashboardOutcome.Ok => ok(result.Value!),
            DashboardOutcome.NotFound => Results.NotFound(),
            DashboardOutcome.Forbidden => Results.Problem(
                statusCode: StatusCodes.Status403Forbidden, title: "Not allowed", detail: result.Message),
            DashboardOutcome.Invalid => Results.ValidationProblem(
                (result.Errors ?? []).GroupBy(e => e.Path, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal),
                detail: result.Message),
            DashboardOutcome.NameTaken => Results.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "Name already used", detail: result.Message),
            DashboardOutcome.Conflict => Results.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "Changed since you opened it", detail: result.Message,
                extensions: new Dictionary<string, object?>
                {
                    ["currentRevision"] = result.Conflict?.CurrentRevision,
                    ["updatedAt"] = result.Conflict?.UpdatedAt,
                    ["updatedBy"] = result.Conflict?.UpdatedByDisplayName,
                }),
            DashboardOutcome.GrantRefused => Results.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "Cannot share", detail: result.Message),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }
}
