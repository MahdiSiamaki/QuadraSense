using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Sqm.Api.Auth;
using Sqm.Application.Identity;

namespace Sqm.Api.Endpoints;

/// <summary>Sign-in, sign-out, and everything a user does about their own account.</summary>
public static class AuthEndpoints
{
    /// <summary>Rate-limiter partition name for the login endpoint.</summary>
    public const string LoginRateLimiter = "login";

    /// <summary>Registers the authentication routes.</summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/auth").WithTags("Authentication");

        group.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimiter)
            .WithName("Login")
            .WithSummary("Exchanges a username and password for a session cookie.");

        group.MapPost("/login/two-step", CompleteTwoStepAsync)
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimiter)
            .WithName("LoginSecondStep")
            .WithSummary("Exchanges the ticket from a good password and a code for a session cookie.");

        group.MapPost("/logout", LogoutAsync)
            .RequireAuthorization(PermissionPolicyProvider.PasswordChangeAllowed)
            .WithName("Logout")
            .WithSummary("Ends the current session.");

        group.MapGet("/me", MeAsync)
            .RequireAuthorization(PermissionPolicyProvider.PasswordChangeAllowed)
            .WithName("CurrentUser")
            .WithSummary("The signed-in user, their permissions, and their security facts.");

        group.MapPut("/me", UpdateProfileAsync)
            .RequireAuthorization(PermissionPolicyProvider.Authenticated)
            .WithName("UpdateProfile")
            .WithSummary("Updates the fields a user may change about themselves.");

        group.MapPost("/me/password", ChangePasswordAsync)
            .RequireAuthorization(PermissionPolicyProvider.PasswordChangeAllowed)
            .WithName("ChangePassword")
            .WithSummary("Changes the signed-in user's password.");

        group.MapGet("/me/sessions", MySessionsAsync)
            .RequireAuthorization(PermissionPolicyProvider.Authenticated)
            .WithName("MySessions")
            .WithSummary("Devices this account has signed in from.");

        group.MapDelete("/me/sessions/{id:guid}", RevokeMySessionAsync)
            .RequireAuthorization(PermissionPolicyProvider.Authenticated)
            .WithName("RevokeMySession")
            .WithSummary("Signs one other device out.");

        group.MapDelete("/me/sessions", RevokeMyOtherSessionsAsync)
            .RequireAuthorization(PermissionPolicyProvider.Authenticated)
            .WithName("RevokeMyOtherSessions")
            .WithSummary("Signs every other device out.");

        return app;
    }

    // =======================================================================

    /// <summary>
    /// Signs in and issues the session and CSRF cookies.
    /// </summary>
    /// <remarks>
    /// The response body deliberately says almost nothing on failure. Whether the username
    /// exists, whether the password was close, and whether the account is deactivated are all the
    /// same answer - except for a lockout, which is only reported to a caller who has already
    /// proved they know the password (see <c>LocalPasswordAuthenticator</c>).
    /// </remarks>
    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext http,
        IPasswordAuthenticator authenticator,
        ISessionStore sessions,
        IUserDirectory users,
        ITwoStepService twoStep,
        IOptions<AuthOptions> authOptions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(twoStep);
        ArgumentNullException.ThrowIfNull(authOptions);

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["username"] = ["Enter your username and password."],
            });
        }

        var context = CurrentUser.Context(http);

        var outcome = await authenticator
            .AuthenticateAsync(request.Username.Trim(), request.Password, context, ct)
            .ConfigureAwait(false);

        if (outcome.Status == LoginStatus.TwoStepRequired)
        {
            var challenge = await twoStep
                .BeginChallengeAsync(outcome.UserId!.Value, request.RememberMe, ct)
                .ConfigureAwait(false);

            return Results.Json(
                new SecondStepRequired(challenge.Ticket, challenge.ExpiresAt),
                statusCode: StatusCodes.Status202Accepted);
        }

        if (!outcome.Succeeded)
        {
            return Results.Json(new LoginFailure(
                outcome.Status.ToString(),
                Message(outcome),
                outcome.LockedUntil), statusCode: StatusCodes.Status401Unauthorized);
        }

        return await StartSessionAsync(
            outcome.UserId!.Value, request.RememberMe, http, sessions, users, twoStep,
            authOptions.Value.Session, ct).ConfigureAwait(false);
    }

    /// <summary>Signs in with a code, after a good password.</summary>
    private static async Task<IResult> CompleteTwoStepAsync(
        SecondStepRequest request,
        HttpContext http,
        ITwoStepService twoStep,
        ISessionStore sessions,
        IUserDirectory users,
        IOptions<AuthOptions> authOptions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(twoStep);
        ArgumentNullException.ThrowIfNull(authOptions);

        if (string.IsNullOrEmpty(request.Ticket) || string.IsNullOrWhiteSpace(request.Code))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["code"] = ["Enter the code from your authenticator."],
            });
        }

        var result = await twoStep
            .CompleteChallengeAsync(request.Ticket, request.Code, CurrentUser.Context(http), ct)
            .ConfigureAwait(false);

        return result.Status switch
        {
            TwoStepResultStatus.Success => await StartSessionAsync(
                result.UserId!.Value, result.Remember, http, sessions, users, twoStep,
                authOptions.Value.Session, ct).ConfigureAwait(false),

            TwoStepResultStatus.AccountLocked => Results.Json(new LoginFailure(
                nameof(LoginStatus.AccountLocked),
                Message(new LoginOutcome(LoginStatus.AccountLocked)),
                result.LockedUntil), statusCode: StatusCodes.Status401Unauthorized),

            TwoStepResultStatus.ChallengeExpired => Results.Json(new LoginFailure(
                nameof(TwoStepResultStatus.ChallengeExpired),
                "That sign-in has run out of time or attempts. Enter your password again.",
                null), statusCode: StatusCodes.Status401Unauthorized),

            _ => Results.Json(new LoginFailure(
                nameof(TwoStepResultStatus.InvalidCode),
                "That code is not right. Use the code showing now, or a recovery code.",
                null), statusCode: StatusCodes.Status401Unauthorized),
        };
    }

    /// <summary>Everything that follows a complete sign-in, by either path.</summary>
    private static async Task<IResult> StartSessionAsync(
        long userId,
        bool remember,
        HttpContext http,
        ISessionStore sessions,
        IUserDirectory users,
        ITwoStepService twoStep,
        SessionSettings options,
        CancellationToken ct)
    {
        var context = CurrentUser.Context(http);

        var session = await sessions
            .CreateAsync(userId, context.Ip, context.UserAgent, remember, ct)
            .ConfigureAwait(false);

        http.Response.Cookies.Append(CookieNames.Session(options), session.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.RequireSecureCookies,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            // Without "remember me", no lifetime at all: a session cookie dies with the browser,
            // and the server enforces the real limit anyway. With it, exactly the session's own
            // lifetime, so the cookie ends with the session it names and with the CSRF cookie
            // beside it. A session cookie that outlives its CSRF cookie refuses the next sign-in.
            MaxAge = remember ? options.RememberedLifetime : null,
        });

        CsrfMiddleware.IssueToken(
            http, options, remember ? options.RememberedLifetime : options.AbsoluteTimeout);

        var detail = await users.GetAsync(userId, ct).ConfigureAwait(false);
        var status = await twoStep.GetStatusAsync(userId, ct).ConfigureAwait(false);

        return Results.Ok(Describe(
            detail!, session.IdleExpiresAt, session.AbsoluteExpiresAt,
            status.Required && !status.Enabled));
    }

    private static string Message(LoginOutcome outcome) => outcome.Status switch
    {
        LoginStatus.AccountLocked =>
            "Too many failed attempts. Try again shortly, or ask an administrator to unlock the "
            + "account.",
        LoginStatus.AccountDisabled =>
            "This account has been deactivated. Contact an administrator.",
        _ => "Incorrect username or password.",
    };

    private static async Task<IResult> LogoutAsync(
        HttpContext http,
        ISessionStore sessions,
        IAuditLog audit,
        IOptions<AuthOptions> authOptions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(authOptions);

        var session = CurrentUser.Session(http)!;
        var options = authOptions.Value.Session;

        await sessions.RevokeAsync(session.SessionId, session.User.Username, "signed out", ct)
            .ConfigureAwait(false);

        CookieNames.ClearSession(http.Response, options);

        CsrfMiddleware.ClearToken(http, options);

        await audit.WriteAsync(new AuditEntry(
            ActorName: session.User.Username,
            Action: "auth.logout",
            Category: AuditCategory.Authentication,
            Outcome: AuditOutcome.Success,
            ActorUserId: session.User.UserId,
            Ip: http.Connection.RemoteIpAddress?.ToString(),
            CorrelationId: http.TraceIdentifier), ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(
        HttpContext http, IUserDirectory users, CancellationToken ct)
    {
        var session = CurrentUser.Session(http)!;
        var detail = await users.GetAsync(session.User.UserId, ct).ConfigureAwait(false);

        return detail is null
            ? Results.Unauthorized()
            : Results.Ok(Describe(
                detail, session.IdleExpiresAt, session.AbsoluteExpiresAt,
                session.User.MustSetUpTwoStep));
    }

    private static async Task<IResult> UpdateProfileAsync(
        ProfileRequest request, HttpContext http, IUserDirectory users, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["displayName"] = ["A display name is required."],
            });
        }

        var session = CurrentUser.Session(http)!;

        // The request maps onto ProfileUpdate, which carries exactly four fields. There is no
        // path from this body to a role, a permission or the active flag, however it is shaped.
        await users.UpdateProfileAsync(
            session.User.UserId,
            new ProfileUpdate(request.DisplayName, request.Email, request.JobTitle, request.Phone),
            CurrentUser.Context(http), ct).ConfigureAwait(false);

        var detail = await users.GetAsync(session.User.UserId, ct).ConfigureAwait(false);
        return Results.Ok(Describe(
            detail!, session.IdleExpiresAt, session.AbsoluteExpiresAt,
            session.User.MustSetUpTwoStep));
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request, HttpContext http, IUserDirectory users, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A missing field is a form error, not a 500 from the password hasher.
        if (string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [string.IsNullOrEmpty(request.CurrentPassword) ? "currentPassword" : "newPassword"] =
                    ["Enter your current password and the new one."],
            });
        }

        var session = CurrentUser.Session(http)!;

        try
        {
            var changed = await users.ChangeOwnPasswordAsync(
                session.User.UserId, request.CurrentPassword, request.NewPassword,
                session.SessionId, CurrentUser.Context(http), ct).ConfigureAwait(false);

            if (!changed)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["That is not your current password."],
                });
            }
        }
        catch (PasswordPolicyException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["newPassword"] = [ex.Message],
            });
        }

        return Results.NoContent();
    }

    private static async Task<IResult> MySessionsAsync(
        HttpContext http, ISessionStore sessions, CancellationToken ct)
    {
        var session = CurrentUser.Session(http)!;

        var list = await sessions
            .ListForUserAsync(session.User.UserId, session.SessionId, 25, ct)
            .ConfigureAwait(false);

        return Results.Ok(list);
    }

    private static async Task<IResult> RevokeMySessionAsync(
        Guid id, HttpContext http, ISessionStore sessions, IAuditLog audit, CancellationToken ct)
    {
        var session = CurrentUser.Session(http)!;

        // Scoped to this user's own sessions by listing them first. Without that check the id is
        // a guessable handle to anyone's session - and "sign out my other laptop" would become
        // "sign out anyone".
        var mine = await sessions
            .ListForUserAsync(session.User.UserId, session.SessionId, 200, ct)
            .ConfigureAwait(false);

        if (!mine.Any(s => s.Id == id))
        {
            return Results.NotFound();
        }

        await sessions.RevokeAsync(id, session.User.Username, "signed out by the user", ct)
            .ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry(
            ActorName: session.User.Username,
            Action: "auth.session.revoke",
            Category: AuditCategory.Authentication,
            Outcome: AuditOutcome.Success,
            ActorUserId: session.User.UserId,
            TargetType: "session",
            TargetId: id.ToString(),
            Ip: http.Connection.RemoteIpAddress?.ToString(),
            CorrelationId: http.TraceIdentifier), ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> RevokeMyOtherSessionsAsync(
        HttpContext http, ISessionStore sessions, IAuditLog audit, CancellationToken ct)
    {
        var session = CurrentUser.Session(http)!;

        var revoked = await sessions
            .RevokeAllForUserAsync(
                session.User.UserId, session.SessionId, session.User.Username,
                "signed out by the user", ct)
            .ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry(
            ActorName: session.User.Username,
            Action: "auth.session.revoke_others",
            Category: AuditCategory.Authentication,
            Outcome: AuditOutcome.Success,
            ActorUserId: session.User.UserId,
            Ip: http.Connection.RemoteIpAddress?.ToString(),
            CorrelationId: http.TraceIdentifier,
            Detail: new Dictionary<string, object?> { ["sessionsRevoked"] = revoked }), ct)
            .ConfigureAwait(false);

        return Results.Ok(new SessionsRevoked(revoked));
    }

    /// <summary>Projects a user onto the shape the SPA needs to render itself.</summary>
    private static CurrentUserResponse Describe(
        UserDetail detail,
        DateTimeOffset idleExpires,
        DateTimeOffset absoluteExpires,
        bool mustSetUpTwoStep) =>
        new(
            detail.Id,
            detail.Username,
            detail.DisplayName,
            detail.Email,
            detail.JobTitle,
            detail.Phone,
            detail.MustChangePassword,
            // The granted set only. The full catalogue with provenance is the user detail page's
            // concern; the SPA's own navigation only needs to know what it may show.
            [.. detail.Permissions.Where(p => p.IsGranted).Select(p => p.Code)],
            [.. detail.Roles.Select(r => r.DisplayName)],
            detail.LastLoginAt,
            detail.PreviousLoginAt,
            detail.LastLoginIp,
            detail.PasswordUpdatedAt,
            detail.ActiveSessions,
            idleExpires,
            absoluteExpires,
            detail.EmailVerifiedAt is not null,
            detail.PhoneVerifiedAt is not null,
            detail.TwoStepEnabledAt is not null,
            mustSetUpTwoStep);
}

/// <summary>Credentials submitted to the login endpoint.</summary>
/// <param name="Username">The login name.</param>
/// <param name="Password">The password.</param>
/// <param name="RememberMe">Keep this browser signed in, for the configured number of days.</param>
public sealed record LoginRequest(
    [Required] string Username, [Required] string Password, bool RememberMe = false);

/// <summary>The password was right. A code is needed next.</summary>
/// <param name="Ticket">Carries the sign-in to the next step. Not a session.</param>
/// <param name="ExpiresAt">When it stops working.</param>
public sealed record SecondStepRequired(string Ticket, DateTimeOffset ExpiresAt);

/// <summary>The second step of a sign-in.</summary>
public sealed record SecondStepRequest(string? Ticket, string? Code);

/// <summary>How many other devices were signed out.</summary>
public sealed record SessionsRevoked(int Count);

/// <summary>Why a sign-in was refused.</summary>
/// <param name="Status">Machine-readable status, for the client to branch on.</param>
/// <param name="Message">What to show the person.</param>
/// <param name="LockedUntil">Set only when the account is locked.</param>
public sealed record LoginFailure(string Status, string Message, DateTimeOffset? LockedUntil);

/// <summary>Fields a user may change about themselves.</summary>
public sealed record ProfileRequest(
    string DisplayName, string? Email, string? JobTitle, string? Phone);

/// <summary>A password change by its owner.</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Everything the SPA needs about the signed-in user.</summary>
public sealed record CurrentUserResponse(
    long Id,
    string Username,
    string DisplayName,
    string? Email,
    string? JobTitle,
    string? Phone,
    bool MustChangePassword,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Roles,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? PreviousLoginAt,
    string? LastLoginIp,
    DateTimeOffset? PasswordUpdatedAt,
    int ActiveSessions,
    DateTimeOffset SessionIdleExpiresAt,
    DateTimeOffset SessionAbsoluteExpiresAt,
    bool EmailVerified,
    bool PhoneVerified,
    bool TwoStepEnabled,
    bool MustSetUpTwoStep);
