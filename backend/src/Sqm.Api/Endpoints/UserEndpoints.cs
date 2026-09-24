using Sqm.Api.Auth;
using Sqm.Application.Identity;

namespace Sqm.Api.Endpoints;

/// <summary>User administration.</summary>
/// <remarks>
/// Reading and changing are separate permissions throughout. An access review needs to see who
/// can do what without being able to alter it, and giving a reviewer <c>user.manage</c> so they
/// can read the list is how least privilege quietly stops meaning anything.
/// </remarks>
public static class UserEndpoints
{
    /// <summary>Registers the user administration routes.</summary>
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/users").WithTags("Users");

        group.MapGet("/", SearchAsync)
            .RequireAuthorization(Policy(Permissions.UserView))
            .WithName("SearchUsers")
            .WithSummary("Users, filtered and paged.");

        group.MapGet("/{id:long}", GetAsync)
            .RequireAuthorization(Policy(Permissions.UserView))
            .WithName("GetUser")
            .WithSummary("One user, with every permission and where it comes from.");

        group.MapGet("/{id:long}/sessions", SessionsAsync)
            .RequireAuthorization(Policy(Permissions.UserView))
            .WithName("GetUserSessions")
            .WithSummary("Devices this user has signed in from.");

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(Policy(Permissions.UserManage))
            .WithName("CreateUser")
            .WithSummary("Creates a local account.");

        group.MapPut("/{id:long}", UpdateAsync)
            .RequireAuthorization(Policy(Permissions.UserManage))
            .WithName("UpdateUser")
            .WithSummary("Updates a user's profile fields.");

        group.MapPut("/{id:long}/active", SetActiveAsync)
            .RequireAuthorization(Policy(Permissions.UserManage))
            .WithName("SetUserActive")
            .WithSummary("Activates or deactivates an account.");

        group.MapPut("/{id:long}/roles", SetRolesAsync)
            .RequireAuthorization(Policy(Permissions.UserManage))
            .WithName("SetUserRoles")
            .WithSummary("Replaces a user's roles.");

        group.MapPut("/{id:long}/permissions", SetPermissionsAsync)
            .RequireAuthorization(Policy(Permissions.UserManage))
            .WithName("SetUserPermissions")
            .WithSummary("Replaces a user's direct grants and denies.");

        group.MapPost("/{id:long}/password", ResetPasswordAsync)
            .RequireAuthorization(Policy(Permissions.UserManage))
            .WithName("ResetUserPassword")
            .WithSummary("Sets a new password and forces a change at next sign-in.");

        group.MapPost("/{id:long}/unlock", UnlockAsync)
            .RequireAuthorization(Policy(Permissions.UserManage))
            .WithName("UnlockUser")
            .WithSummary("Clears a lockout early.");

        group.MapPost("/{id:long}/revoke-sessions", RevokeSessionsAsync)
            .RequireAuthorization(Policy(Permissions.UserManage))
            .WithName("RevokeUserSessions")
            .WithSummary("Signs a user out of every device.");

        return app;
    }

    private static string Policy(string permission) => PermissionPolicyProvider.Prefix + permission;

    // =======================================================================

    private static async Task<IResult> SearchAsync(
        IUserDirectory users,
        string? search,
        string? role,
        bool? active,
        string? sort,
        // Nullable, not defaulted. A minimal API treats a non-nullable `int page` as REQUIRED
        // from the query string and rejects the request without one; C# will not let an optional
        // parameter precede the CancellationToken. Nullable says "absent is fine" to both.
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        // The sort arrives as a string and is mapped onto an enum here. An unrecognised value
        // falls back to the default rather than erroring: a stale bookmark should show the list,
        // not a validation message.
        var order = sort switch
        {
            "lastLogin" => UserSort.LastLogin,
            "created" => UserSort.Created,
            _ => UserSort.DisplayName,
        };

        var result = await users.SearchAsync(new UserQuery(
            search, role, active, page is > 0 ? page.Value : 1,
            pageSize is > 0 ? pageSize.Value : 25, order), ct).ConfigureAwait(false);

        return Results.Ok(result);
    }

    private static async Task<IResult> GetAsync(long id, IUserDirectory users, CancellationToken ct)
    {
        var detail = await users.GetAsync(id, ct).ConfigureAwait(false);
        return detail is null ? Results.NotFound() : Results.Ok(detail);
    }

    private static async Task<IResult> SessionsAsync(
        long id, ISessionStore sessions, CancellationToken ct)
    {
        var list = await sessions.ListForUserAsync(id, null, 50, ct).ConfigureAwait(false);
        return Results.Ok(list);
    }

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request, HttpContext http, IUserDirectory users, IRoleDirectory roles,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var notHeld = GrantGuard.NotHeld(CurrentUser.Require(http), GrantGuard.AddedByRoles(
            [], request.RoleCodes ?? [], await PermissionsByRoleAsync(roles, ct).ConfigureAwait(false)));
        if (notHeld.Count > 0)
        {
            return GrantGuard.Refuse(notHeld);
        }

        if (string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["username"] = ["A username and display name are required."],
            });
        }

        try
        {
            var id = await users.CreateAsync(
                new NewUser(
                    request.Username, request.DisplayName, request.Email, request.JobTitle,
                    request.Phone, request.RoleCodes ?? [], request.MustChangePassword),
                request.Password,
                CurrentUser.Actor(http),
                CurrentUser.Context(http),
                ct).ConfigureAwait(false);

            var detail = await users.GetAsync(id, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/users/{id}", detail);
        }
        catch (DuplicateUsernameException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["username"] = [ex.Message],
            });
        }
        catch (PasswordPolicyException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["password"] = [ex.Message],
            });
        }
        catch (RoleChangeRefusedException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["roleCodes"] = [ex.Message],
            });
        }
    }

    private static async Task<IResult> UpdateAsync(
        long id, UpdateUserRequest request, HttpContext http, IUserDirectory users,
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

        var updated = await users.UpdateAsync(
            id,
            new UserUpdate(request.DisplayName, request.Email, request.JobTitle, request.Phone),
            CurrentUser.Actor(http), CurrentUser.Context(http), ct).ConfigureAwait(false);

        return updated ? Results.NoContent() : Results.NotFound();
    }

    /// <remarks>
    /// Refusing self-deactivation is not paternalism. An administrator who disables their own
    /// account is signed out by the same transaction and, if they were the last one, cannot sign
    /// back in to undo it. The last-administrator guard catches that case anyway; this catches it
    /// with a message that says what went wrong.
    /// </remarks>
    private static async Task<IResult> SetActiveAsync(
        long id, SetActiveRequest request, HttpContext http, IUserDirectory users,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.IsActive && id == CurrentUser.Require(http).UserId)
        {
            return Results.Problem(
                title: "You cannot deactivate your own account",
                detail: "Ask another administrator to do it.",
                statusCode: StatusCodes.Status409Conflict);
        }

        try
        {
            var changed = await users.SetActiveAsync(
                id, request.IsActive, request.Reason, CurrentUser.Actor(http),
                CurrentUser.Context(http), ct).ConfigureAwait(false);

            return changed ? Results.NoContent() : Results.NotFound();
        }
        catch (LastAdministratorException ex)
        {
            return Conflict(ex);
        }
    }

    private static async Task<IResult> SetRolesAsync(
        long id, SetRolesRequest request, HttpContext http, IUserDirectory users,
        IRoleDirectory roles, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = CurrentUser.Require(http);
        if (id == actor.UserId)
        {
            return GrantGuard.RefuseSelf();
        }

        var target = await users.GetAsync(id, ct).ConfigureAwait(false);
        if (target is null)
        {
            return Results.NotFound();
        }

        var notHeld = GrantGuard.NotHeld(actor, GrantGuard.AddedByRoles(
            target.Roles.Select(r => r.Code), request.RoleCodes ?? [],
            await PermissionsByRoleAsync(roles, ct).ConfigureAwait(false)));
        if (notHeld.Count > 0)
        {
            return GrantGuard.Refuse(notHeld);
        }

        try
        {
            var changed = await users.SetRolesAsync(
                id, request.RoleCodes ?? [], CurrentUser.Actor(http), CurrentUser.Context(http), ct)
                .ConfigureAwait(false);

            return changed ? Results.NoContent() : Results.NotFound();
        }
        catch (LastAdministratorException ex)
        {
            return Conflict(ex);
        }
        catch (RoleChangeRefusedException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["roleCodes"] = [ex.Message],
            });
        }
    }

    private static async Task<IResult> SetPermissionsAsync(
        long id, SetPermissionsRequest request, HttpContext http, IUserDirectory users,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var overrides = (request.Overrides ?? [])
            .Select(o => new PermissionOverride(
                o.PermissionCode,
                string.Equals(o.Effect, "deny", StringComparison.OrdinalIgnoreCase)
                    ? PermissionEffect.Deny
                    : PermissionEffect.Grant,
                o.Reason))
            .ToList();

        // One override per permission. The same code twice - or a grant and a deny of it - hit
        // the table's primary key, and only the foreign key was translated, so it was a 500.
        if (overrides.GroupBy(o => o.PermissionCode, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1)
            is { } repeated)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["overrides"] = [$"'{repeated.Key}' is listed more than once. Give each permission one grant or one deny."],
            });
        }

        var actor = CurrentUser.Require(http);
        if (id == actor.UserId)
        {
            return GrantGuard.RefuseSelf();
        }

        var target = await users.GetAsync(id, ct).ConfigureAwait(false);
        if (target is null)
        {
            return Results.NotFound();
        }

        var notHeld = GrantGuard.NotHeld(
            actor, GrantGuard.LoosenedByOverrides(target.Permissions, overrides));
        if (notHeld.Count > 0)
        {
            return GrantGuard.Refuse(notHeld);
        }

        try
        {
            var changed = await users.SetPermissionOverridesAsync(
                id, overrides, CurrentUser.Actor(http), CurrentUser.Context(http), ct)
                .ConfigureAwait(false);

            return changed ? Results.NoContent() : Results.NotFound();
        }
        catch (LastAdministratorException ex)
        {
            return Conflict(ex);
        }
        catch (Npgsql.PostgresException ex)
            when (ex.SqlState == Npgsql.PostgresErrorCodes.ForeignKeyViolation)
        {
            // The foreign key to auth.permission is what rejects an unknown code, so this is the
            // honest place to translate it - rather than duplicating the catalogue in C# to
            // produce a prettier message that can go out of date.
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["overrides"] = ["One or more of those permission codes does not exist."],
            });
        }
    }

    private static async Task<IResult> ResetPasswordAsync(
        long id, ResetPasswordRequest request, HttpContext http, IUserDirectory users,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var reset = await users.ResetPasswordAsync(
                id, request.NewPassword, CurrentUser.Actor(http), CurrentUser.Context(http), ct)
                .ConfigureAwait(false);

            return reset ? Results.NoContent() : Results.NotFound();
        }
        catch (PasswordPolicyException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["newPassword"] = [ex.Message],
            });
        }
    }

    private static async Task<IResult> UnlockAsync(
        long id, HttpContext http, IUserDirectory users, CancellationToken ct)
    {
        var unlocked = await users.UnlockAsync(
            id, CurrentUser.Actor(http), CurrentUser.Context(http), ct).ConfigureAwait(false);

        return unlocked ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> RevokeSessionsAsync(
        long id, HttpContext http, ISessionStore sessions, IAuditLog audit, CancellationToken ct)
    {
        var actor = CurrentUser.Actor(http);

        var revoked = await sessions.RevokeAllForUserAsync(
            id, null, actor, "revoked by administrator", ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry(
            ActorName: actor,
            Action: "user.sessions.revoke",
            Category: AuditCategory.User,
            Outcome: AuditOutcome.Success,
            ActorUserId: CurrentUser.Require(http).UserId,
            TargetType: "user",
            TargetId: CurrentUser.Id(id),
            Ip: http.Connection.RemoteIpAddress?.ToString(),
            CorrelationId: http.TraceIdentifier,
            Detail: new Dictionary<string, object?> { ["sessionsRevoked"] = revoked }), ct)
            .ConfigureAwait(false);

        return Results.Ok(new { revoked });
    }

    /// <summary>
    /// 409, not 400. The request was well-formed; the system's state is what refuses it.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> PermissionsByRoleAsync(
        IRoleDirectory roles, CancellationToken ct)
    {
        var all = await roles.GetRolesAsync(ct).ConfigureAwait(false);
        return all.ToDictionary(r => r.Code, r => r.PermissionCodes, StringComparer.Ordinal);
    }

    private static IResult Conflict(Exception ex) => Results.Problem(
        title: "That change would lock everyone out",
        detail: ex.Message,
        statusCode: StatusCodes.Status409Conflict);
}

/// <summary>A new local account.</summary>
public sealed record CreateUserRequest(
    string Username,
    string DisplayName,
    string Password,
    string? Email,
    string? JobTitle,
    string? Phone,
    IReadOnlyList<string>? RoleCodes,
    bool MustChangePassword = true);

/// <summary>Profile fields an administrator may change.</summary>
public sealed record UpdateUserRequest(
    string DisplayName, string? Email, string? JobTitle, string? Phone);

/// <summary>Activation or deactivation, with a reason recorded in the audit log.</summary>
public sealed record SetActiveRequest(bool IsActive, string? Reason);

/// <summary>The complete set of roles a user should hold after this call.</summary>
public sealed record SetRolesRequest(IReadOnlyList<string>? RoleCodes);

/// <summary>The complete set of direct overrides a user should have after this call.</summary>
public sealed record SetPermissionsRequest(IReadOnlyList<PermissionOverrideRequest>? Overrides);

/// <summary>One direct grant or deny.</summary>
public sealed record PermissionOverrideRequest(
    string PermissionCode, string Effect, string? Reason);

/// <summary>An administrator setting someone else's password.</summary>
public sealed record ResetPasswordRequest(string NewPassword);
