using System.Collections.Frozen;

namespace Sqm.Application.Identity;

/// <summary>The identity the API resolves from a session cookie on every request.</summary>
/// <remarks>
/// Built fresh from the database each request rather than cached or carried in the cookie. That
/// is the whole reason for choosing server-side sessions: a role removed a second ago is gone on
/// the next request, with no revocation list and no window during which a disabled account still
/// works. See ADR-006.
/// </remarks>
/// <param name="UserId">Primary key in <c>auth.user_account</c>.</param>
/// <param name="Username">Login name, as stored.</param>
/// <param name="DisplayName">Human name, for the UI and the audit log.</param>
/// <param name="MustChangePassword">
/// When true the API refuses everything except the profile and change-password endpoints. A
/// forced password change that can be skipped by not visiting the page is not a control.
/// </param>
/// <param name="Permissions">The resolved effective set: roles, plus direct grants, minus denies.</param>
public sealed record AuthenticatedUser(
    long UserId,
    string Username,
    string DisplayName,
    bool MustChangePassword,
    FrozenSet<string> Permissions)
{
    /// <summary>Whether this user holds a permission.</summary>
    public bool Can(string permission) => Permissions.Contains(permission);
}

/// <summary>A role, as referenced from a user.</summary>
public sealed record RoleRef(long Id, string Code, string DisplayName);

/// <summary>One row of the user list.</summary>
public sealed record UserSummary(
    long Id,
    string Username,
    string DisplayName,
    string? Email,
    string? JobTitle,
    string Provider,
    bool IsActive,
    bool IsLocked,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<RoleRef> Roles);

/// <summary>A page of users, with the total so the client can render real paging.</summary>
public sealed record UserPage(IReadOnlyList<UserSummary> Items, int Total, int Page, int PageSize);

/// <summary>Search, filter and paging for the user list.</summary>
/// <remarks>
/// <c>Search</c> matches username, display name or email, case-insensitively. <c>IsActive</c>
/// null means both. <c>Page</c> is 1-based. <c>Sort</c> is an allow-list, never a column name
/// taken from the client.
/// </remarks>
public sealed record UserQuery(
    string? Search = null,
    string? RoleCode = null,
    bool? IsActive = null,
    int Page = 1,
    int PageSize = 25,
    UserSort Sort = UserSort.DisplayName);

/// <summary>Sort orders the user list offers. An allow-list, never a column name from the client.</summary>
public enum UserSort
{
    /// <summary>Alphabetical by display name.</summary>
    DisplayName,

    /// <summary>Most recently signed in first; never signed in last.</summary>
    LastLogin,

    /// <summary>Newest account first.</summary>
    Created,
}

/// <summary>Everything the user detail page shows.</summary>
public sealed record UserDetail(
    long Id,
    string Username,
    string DisplayName,
    string? Email,
    string? JobTitle,
    string? Phone,
    string Provider,
    bool IsActive,
    bool MustChangePassword,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? PreviousLoginAt,
    string? LastLoginIp,
    short FailedLoginCount,
    DateTimeOffset? LockedUntil,
    DateTimeOffset? PasswordUpdatedAt,
    DateTimeOffset? DeactivatedAt,
    string? DeactivatedBy,
    string? DeactivationReason,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy,
    IReadOnlyList<RoleRef> Roles,
    IReadOnlyList<PermissionResolution> Permissions,
    int ActiveSessions);

/// <summary>
/// One permission, and the full account of why this user does or does not have it.
/// </summary>
/// <remarks>
/// <para>
/// The provenance fields are not decoration. "Why can she export?" and "I removed her lookup
/// access, why does she still have it?" are the two questions asked about every RBAC system ever
/// built, and a screen that shows only the final answer cannot answer either. Every field here
/// is rendered on the user detail page.
/// </para>
/// <para>
/// <c>GrantedByRoles</c> holds the display names of the roles that carry it, empty if none do;
/// <c>GrantedDirectly</c> is a grant independent of any role; <c>DeniedDirectly</c> is a deny,
/// which overrides both.
/// </para>
/// </remarks>
public sealed record PermissionResolution(
    string Code,
    string Category,
    string DisplayName,
    string Description,
    bool IsDangerous,
    bool IsGranted,
    IReadOnlyList<string> GrantedByRoles,
    bool GrantedDirectly,
    bool DeniedDirectly,
    string? OverrideReason);

/// <summary>A permission as listed in the catalogue, for the matrix and the role editor.</summary>
public sealed record PermissionDefinition(
    string Code,
    string Category,
    string DisplayName,
    string Description,
    bool IsDangerous,
    int SortOrder);

/// <summary>A role with its permissions and how many users hold it.</summary>
public sealed record RoleDetail(
    long Id,
    string Code,
    string DisplayName,
    string Description,
    bool IsSystem,
    int MemberCount,
    IReadOnlyList<string> PermissionCodes,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

/// <summary>A live or historical session, as shown on the profile and user detail pages.</summary>
public sealed record SessionInfo(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset AbsoluteExpiresAt,
    DateTimeOffset? RevokedAt,
    string? RevokedReason,
    string? Ip,
    string? UserAgent,
    bool IsCurrent);

/// <summary>The result of creating a session: the cookie value, and when it dies.</summary>
/// <remarks>
/// <c>Token</c> is the only time the plaintext exists on the server. It is handed
/// straight to the cookie and never stored, logged or returned again - the database keeps its
/// SHA-256.
/// </remarks>
public sealed record SessionCreation(
    Guid SessionId,
    string Token,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset AbsoluteExpiresAt);

/// <summary>A session resolved from a cookie, with its user already loaded.</summary>
public sealed record ResolvedSession(
    Guid SessionId,
    AuthenticatedUser User,
    DateTimeOffset LastSeenAt,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset AbsoluteExpiresAt);

/// <summary>Why a sign-in did not succeed.</summary>
public enum LoginStatus
{
    /// <summary>Authenticated.</summary>
    Success,

    /// <summary>
    /// No such user, or the wrong password. One value for both on purpose: distinguishing them
    /// tells an attacker which usernames exist.
    /// </summary>
    InvalidCredentials,

    /// <summary>Too many consecutive failures. Try again after <c>LockedUntil</c>.</summary>
    AccountLocked,

    /// <summary>The account exists but an administrator has deactivated it.</summary>
    AccountDisabled,
}

/// <summary>The outcome of a sign-in attempt.</summary>
/// <remarks>
/// A record rather than a bool, because the login endpoint has to distinguish three things it
/// must NOT tell the client apart (no such user / wrong password / locked) from the one it must
/// (the password is correct but expired). The distinction lives here; what reaches the browser
/// is decided in the endpoint.
/// <para>
/// When TOTP is added this record gains a <c>MfaRequired</c> status and a challenge token, and
/// the endpoint gains a second step. Nothing else on this path changes - which is the point of
/// returning a status rather than a user or null.
/// </para>
/// </remarks>
public sealed record LoginOutcome(
    LoginStatus Status,
    long? UserId = null,
    string? Username = null,
    string? DisplayName = null,
    bool MustChangePassword = false,
    DateTimeOffset? LockedUntil = null)
{
    /// <summary>Shorthand for the success case.</summary>
    public bool Succeeded => Status == LoginStatus.Success;
}

/// <summary>Fields a user may change about themselves.</summary>
/// <remarks>
/// Username, roles, permissions and active state are absent by design: this record IS the
/// allow-list. A profile endpoint that accepts a user object and copies the fields it recognises
/// is one forgotten property away from letting anyone grant themselves a role.
/// </remarks>
public sealed record ProfileUpdate(string DisplayName, string? Email, string? JobTitle, string? Phone);

/// <summary>Fields an administrator may change about someone else.</summary>
public sealed record UserUpdate(
    string DisplayName,
    string? Email,
    string? JobTitle,
    string? Phone);

/// <summary>A new local account.</summary>
public sealed record NewUser(
    string Username,
    string DisplayName,
    string? Email,
    string? JobTitle,
    string? Phone,
    IReadOnlyList<string> RoleCodes,
    bool MustChangePassword = true);

/// <summary>A direct grant or deny on one user.</summary>
public sealed record PermissionOverride(string PermissionCode, PermissionEffect Effect, string? Reason);

/// <summary>Whether a direct override adds a permission or removes it.</summary>
public enum PermissionEffect
{
    /// <summary>Add this permission regardless of role membership.</summary>
    Grant,

    /// <summary>Remove it regardless of role membership. Wins over every grant.</summary>
    Deny,
}
