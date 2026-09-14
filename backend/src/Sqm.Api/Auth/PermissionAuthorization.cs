using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;
using Sqm.Application.Identity;

namespace Sqm.Api.Auth;

/// <summary>Requires one permission, or merely a signed-in user when the code is null.</summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    /// <summary>Creates a requirement for a permission code.</summary>
    public PermissionRequirement(string? permissionCode, bool allowedDuringForcedPasswordChange)
    {
        PermissionCode = permissionCode;
        AllowedDuringForcedPasswordChange = allowedDuringForcedPasswordChange;
    }

    /// <summary>The permission required, or <see langword="null"/> for "any signed-in user".</summary>
    public string? PermissionCode { get; }

    /// <summary>
    /// Whether this endpoint stays reachable for a user who must change their password.
    /// </summary>
    /// <remarks>
    /// Only the handful needed to complete that change. A forced password change that can be
    /// skipped by not visiting the page is not a control, so everything else is refused until the
    /// password is actually changed.
    /// </remarks>
    public bool AllowedDuringForcedPasswordChange { get; }
}

/// <summary>Evaluates <see cref="PermissionRequirement"/> against the resolved session.</summary>
public sealed class PermissionAuthorizationHandler
    : AuthorizationHandler<PermissionRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.Resource is not HttpContext http
            || http.Items[SessionAuthenticationHandler.SessionItemKey] is not ResolvedSession session)
        {
            // Not signed in. Left unhandled rather than explicitly failed, so the pipeline issues
            // a 401 challenge instead of a 403 - the difference tells the SPA whether to show the
            // login page or an access-denied message.
            return Task.CompletedTask;
        }

        if (session.User.MustChangePassword && !requirement.AllowedDuringForcedPasswordChange)
        {
            context.Fail(new AuthorizationFailureReason(this,
                "A password change is required before this account can be used."));
            return Task.CompletedTask;
        }

        if (requirement.PermissionCode is null || session.User.Can(requirement.PermissionCode))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Builds a policy for any permission code on demand, so endpoints can name one directly.
/// </summary>
/// <remarks>
/// <para>
/// The alternative is registering seventeen policies at startup and remembering to register the
/// eighteenth. A provider that parses the policy name cannot be forgotten, and the permission
/// catalogue stays the single list.
/// </para>
/// <para>
/// The default policy is <see cref="PermissionRequirement"/> with a null code - authenticated,
/// nothing more - and the fallback policy is the same. That is what makes "forgot to protect
/// this endpoint" a 401 rather than an open door: an endpoint with no authorisation metadata at
/// all still requires a session.
/// </para>
/// </remarks>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    /// <summary>Prefix that marks a policy name as a permission requirement.</summary>
    public const string Prefix = "perm:";

    /// <summary>Policy requiring only a valid session.</summary>
    public const string Authenticated = "authenticated";

    /// <summary>
    /// Policy requiring only a valid session, and reachable while a password change is forced.
    /// </summary>
    public const string PasswordChangeAllowed = "authenticated:password-change";

    private readonly AuthorizationPolicy _authenticated;
    private readonly AuthorizationPolicy _passwordChange;

    /// <summary>Creates the provider.</summary>
    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _authenticated = Build(null, allowDuringForcedPasswordChange: false);
        _passwordChange = Build(null, allowDuringForcedPasswordChange: true);
    }

    /// <inheritdoc />
    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => Task.FromResult(_authenticated);

    /// <summary>
    /// The policy applied to endpoints that declare no authorisation of their own.
    /// </summary>
    /// <remarks>
    /// Non-null on purpose: this is what makes the API deny by default. An endpoint added without
    /// <c>RequireAuthorization</c> is protected anyway, and the endpoints that genuinely must be
    /// anonymous say so with <c>AllowAnonymous</c> - which is visible in review, where a missing
    /// attribute is not.
    /// </remarks>
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() =>
        Task.FromResult<AuthorizationPolicy?>(_authenticated);

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (string.Equals(policyName, Authenticated, StringComparison.Ordinal))
        {
            return Task.FromResult<AuthorizationPolicy?>(_authenticated);
        }

        if (string.Equals(policyName, PasswordChangeAllowed, StringComparison.Ordinal))
        {
            return Task.FromResult<AuthorizationPolicy?>(_passwordChange);
        }

        if (policyName is not null && policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var code = policyName[Prefix.Length..];

            // Rejected here rather than at the first request that uses it. A typo in a permission
            // name would otherwise produce a policy nobody can satisfy, which looks exactly like
            // a correctly denied request.
            if (!Permissions.All.Contains(code))
            {
                throw new InvalidOperationException(
                    $"'{code}' is not a known permission. Add it to auth.permission and to "
                    + "Sqm.Application.Identity.Permissions.");
            }

            return Task.FromResult<AuthorizationPolicy?>(
                Build(code, allowDuringForcedPasswordChange: false));
        }

        return Task.FromResult<AuthorizationPolicy?>(null);
    }

    private static AuthorizationPolicy Build(string? code, bool allowDuringForcedPasswordChange) =>
        new AuthorizationPolicyBuilder(SessionAuthenticationHandler.SchemeName)
            .AddRequirements(new PermissionRequirement(code, allowDuringForcedPasswordChange))
            .Build();
}

/// <summary>Records every refusal in the audit log.</summary>
/// <remarks>
/// <para>
/// A 403 that leaves no trace is how someone probing for what they can reach stays invisible. The
/// entry names the user, the method and the path, so a pattern of refusals against the user
/// endpoints is visible as a pattern rather than as nothing at all.
/// </para>
/// <para>
/// 401s are not audited. An expired cookie produces one on every background poll a browser tab
/// makes, and drowning the log in those would defeat the purpose of writing the 403s.
/// </para>
/// </remarks>
public sealed class AuditingAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();
    private readonly IAuditLog _audit;

    /// <summary>Creates the handler.</summary>
    public AuditingAuthorizationResultHandler(IAuditLog audit) => _audit = audit;

    /// <inheritdoc />
    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (authorizeResult.Forbidden
            && context.Items[SessionAuthenticationHandler.SessionItemKey] is ResolvedSession session)
        {
            // Every permission the policy demanded, not the first. A route inside a group
            // carries both the group's requirement and its own, and naming only one made a
            // refused tac.activate read in the log as a refused import.view - a denial entry
            // that points at the wrong permission is worse than none.
            var codes = policy?.Requirements
                .OfType<PermissionRequirement>()
                .Select(r => r.PermissionCode)
                .Where(c => c is not null)
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];

            var required = codes.Length == 0 ? null : string.Join(' ', codes);

            await _audit.WriteAsync(new AuditEntry(
                ActorName: session.User.Username,
                Action: required ?? "request.forbidden",
                Category: AuditCategory.System,
                Outcome: AuditOutcome.Denied,
                ActorUserId: session.User.UserId,
                TargetType: "endpoint",
                TargetId: context.Request.Path.Value,
                TargetName: context.Request.Method,
                Ip: context.Connection.RemoteIpAddress?.ToString(),
                UserAgent: context.Request.Headers.UserAgent.ToString(),
                CorrelationId: context.TraceIdentifier,
                Detail: new Dictionary<string, object?>
                {
                    ["requiredPermissions"] = codes,
                    ["held"] = codes.Where(c => session.User.Can(c!)).ToArray(),
                    ["mustChangePassword"] = session.User.MustChangePassword,
                }), context.RequestAborted).ConfigureAwait(false);
        }

        await _default.HandleAsync(next, context, policy!, authorizeResult).ConfigureAwait(false);
    }
}

/// <summary>Convenience for reading the current session out of an <see cref="HttpContext"/>.</summary>
public static class CurrentUser
{
    /// <summary>The resolved session, or <see langword="null"/> for an anonymous request.</summary>
    public static ResolvedSession? Session(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Items[SessionAuthenticationHandler.SessionItemKey] as ResolvedSession;
    }

    /// <summary>The signed-in user. Throws if called on an endpoint that allows anonymous access.</summary>
    public static AuthenticatedUser Require(HttpContext http) =>
        Session(http)?.User
        ?? throw new InvalidOperationException(
            "No session on this request. This endpoint should require authorisation.");

    /// <summary>The username to record as the actor of an audited action.</summary>
    public static string Actor(HttpContext http) => Session(http)?.User.Username ?? "anonymous";

    /// <summary>Where the request came from, for the audit entry.</summary>
    public static AuthenticationContext Context(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        return new AuthenticationContext(
            http.Connection.RemoteIpAddress?.ToString(),
            http.Request.Headers.UserAgent.ToString(),
            http.TraceIdentifier);
    }

    /// <summary>Formats an identifier for an audit entry's target.</summary>
    public static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
}
