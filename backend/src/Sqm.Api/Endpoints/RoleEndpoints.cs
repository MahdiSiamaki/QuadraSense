using Sqm.Api.Auth;
using Sqm.Application.Identity;

namespace Sqm.Api.Endpoints;

/// <summary>Role administration and the permission catalogue.</summary>
public static class RoleEndpoints
{
    /// <summary>Registers the role routes.</summary>
    public static IEndpointRouteBuilder MapRoleEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/roles").WithTags("Roles");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(Policy(Permissions.RoleView))
            .WithName("ListRoles")
            .WithSummary("Every role, with its permissions and member count.");

        group.MapGet("/{id:long}", GetAsync)
            .RequireAuthorization(Policy(Permissions.RoleView))
            .WithName("GetRole")
            .WithSummary("One role.");

        group.MapGet("/{id:long}/members", MembersAsync)
            .RequireAuthorization(Policy(Permissions.RoleView))
            .WithName("GetRoleMembers")
            .WithSummary("The users holding a role.");

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(Policy(Permissions.RoleManage))
            .WithName("CreateRole")
            .WithSummary("Creates a role.");

        group.MapPut("/{id:long}", UpdateAsync)
            .RequireAuthorization(Policy(Permissions.RoleManage))
            .WithName("UpdateRole")
            .WithSummary("Renames a role. The code is immutable.");

        group.MapPut("/{id:long}/permissions", SetPermissionsAsync)
            .RequireAuthorization(Policy(Permissions.RoleManage))
            .WithName("SetRolePermissions")
            .WithSummary("Replaces a role's permissions.");

        group.MapDelete("/{id:long}", DeleteAsync)
            .RequireAuthorization(Policy(Permissions.RoleManage))
            .WithName("DeleteRole")
            .WithSummary("Deletes a role that is not built-in and has no members.");

        // The catalogue is readable by anyone who can see EITHER roles or users, because the
        // user detail page needs the descriptions to explain what a grant means. It carries no
        // information about who holds what.
        app.MapGet("/api/v1/permissions", CatalogueAsync)
            .RequireAuthorization(Policy(Permissions.RoleView))
            .WithTags("Roles")
            .WithName("ListPermissions")
            .WithSummary("The permission catalogue, grouped by category.");

        return app;
    }

    private static string Policy(string permission) => PermissionPolicyProvider.Prefix + permission;

    // =======================================================================

    private static async Task<IResult> ListAsync(IRoleDirectory roles, CancellationToken ct) =>
        Results.Ok(await roles.GetRolesAsync(ct).ConfigureAwait(false));

    private static async Task<IResult> GetAsync(long id, IRoleDirectory roles, CancellationToken ct)
    {
        var role = await roles.GetRoleAsync(id, ct).ConfigureAwait(false);
        return role is null ? Results.NotFound() : Results.Ok(role);
    }

    private static async Task<IResult> MembersAsync(
        long id, IRoleDirectory roles, CancellationToken ct) =>
        Results.Ok(await roles.GetMembersAsync(id, ct).ConfigureAwait(false));

    private static async Task<IResult> CatalogueAsync(
        IRoleDirectory roles, CancellationToken ct) =>
        Results.Ok(await roles.GetPermissionCatalogueAsync(ct).ConfigureAwait(false));

    private static async Task<IResult> CreateAsync(
        CreateRoleRequest request, HttpContext http, IRoleDirectory roles, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["code"] = ["A code and a display name are required."],
            });
        }

        try
        {
            var id = await roles.CreateRoleAsync(
                request.Code, request.DisplayName, request.Description ?? string.Empty,
                request.PermissionCodes ?? [], CurrentUser.Actor(http),
                CurrentUser.Context(http), ct).ConfigureAwait(false);

            var role = await roles.GetRoleAsync(id, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/roles/{id}", role);
        }
        catch (RoleChangeRefusedException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["code"] = [ex.Message],
            });
        }
    }

    private static async Task<IResult> UpdateAsync(
        long id, UpdateRoleRequest request, HttpContext http, IRoleDirectory roles,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["displayName"] = ["A display name is required."],
            });
        }

        var updated = await roles.UpdateRoleAsync(
            id, request.DisplayName, request.Description ?? string.Empty,
            CurrentUser.Actor(http), CurrentUser.Context(http), ct).ConfigureAwait(false);

        return updated ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> SetPermissionsAsync(
        long id, SetRolePermissionsRequest request, HttpContext http, IRoleDirectory roles,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var changed = await roles.SetRolePermissionsAsync(
                id, request.PermissionCodes ?? [], CurrentUser.Actor(http),
                CurrentUser.Context(http), ct).ConfigureAwait(false);

            return changed ? Results.NoContent() : Results.NotFound();
        }
        catch (LastAdministratorException ex)
        {
            return Results.Problem(
                title: "That change would lock everyone out",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (RoleChangeRefusedException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["permissionCodes"] = [ex.Message],
            });
        }
    }

    private static async Task<IResult> DeleteAsync(
        long id, HttpContext http, IRoleDirectory roles, CancellationToken ct)
    {
        try
        {
            var deleted = await roles.DeleteRoleAsync(
                id, CurrentUser.Actor(http), CurrentUser.Context(http), ct).ConfigureAwait(false);

            return deleted ? Results.NoContent() : Results.NotFound();
        }
        catch (RoleChangeRefusedException ex)
        {
            return Results.Problem(
                title: "That role cannot be deleted",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    }
}

/// <summary>A new role.</summary>
public sealed record CreateRoleRequest(
    string Code, string DisplayName, string? Description, IReadOnlyList<string>? PermissionCodes);

/// <summary>A role's editable text. The code is absent because it is immutable.</summary>
public sealed record UpdateRoleRequest(string DisplayName, string? Description);

/// <summary>The complete set of permissions a role should carry after this call.</summary>
public sealed record SetRolePermissionsRequest(IReadOnlyList<string>? PermissionCodes);
