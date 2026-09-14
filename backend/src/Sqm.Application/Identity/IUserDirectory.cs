namespace Sqm.Application.Identity;

/// <summary>Reads and writes user accounts, their roles and their permission overrides.</summary>
/// <remarks>
/// Every mutating method takes an <c>actor</c>. It is not a convenience: the audit entry and the
/// change are written in one transaction, so the log cannot disagree with the data - the same
/// guarantee the import platform gets by making the job queue and its audit trail one write.
/// </remarks>
public interface IUserDirectory
{
    // ---------------------------------------------------------------- reads
    /// <summary>A page of users matching the query.</summary>
    Task<UserPage> SearchAsync(UserQuery query, CancellationToken ct);

    /// <summary>Everything the user detail page needs, or <see langword="null"/> if no such user.</summary>
    Task<UserDetail?> GetAsync(long userId, CancellationToken ct);

    /// <summary>The same, by username. Case-insensitive.</summary>
    Task<UserDetail?> GetByUsernameAsync(string username, CancellationToken ct);

    /// <summary>
    /// A user's effective permissions, resolved the same way the request pipeline resolves them.
    /// </summary>
    Task<IReadOnlyList<PermissionResolution>> GetEffectivePermissionsAsync(
        long userId, CancellationToken ct);

    // --------------------------------------------------------------- writes
    /// <summary>Creates a local account with an initial password.</summary>
    /// <returns>The new user's id.</returns>
    /// <exception cref="DuplicateUsernameException">The username is already taken.</exception>
    Task<long> CreateAsync(
        NewUser user, string initialPassword, string actor, AuthenticationContext context,
        CancellationToken ct);

    /// <summary>Updates the fields an administrator may change.</summary>
    Task<bool> UpdateAsync(
        long userId, UserUpdate update, string actor, AuthenticationContext context,
        CancellationToken ct);

    /// <summary>Updates the fields a user may change about themselves.</summary>
    Task<bool> UpdateProfileAsync(
        long userId, ProfileUpdate update, AuthenticationContext context, CancellationToken ct);

    /// <summary>Activates or deactivates an account.</summary>
    /// <remarks>
    /// Deactivating revokes every live session for that user in the same transaction. An account
    /// that is disabled but still has a working browser tab is not disabled.
    /// </remarks>
    Task<bool> SetActiveAsync(
        long userId, bool isActive, string? reason, string actor, AuthenticationContext context,
        CancellationToken ct);

    /// <summary>Replaces a user's roles with exactly this set.</summary>
    /// <remarks>
    /// Replace rather than add/remove, because the UI presents a set of checkboxes and a
    /// per-item API would make two concurrent edits silently interleave into a third state
    /// neither administrator chose.
    /// </remarks>
    Task<bool> SetRolesAsync(
        long userId, IReadOnlyList<string> roleCodes, string actor, AuthenticationContext context,
        CancellationToken ct);

    /// <summary>Replaces a user's direct permission overrides with exactly this set.</summary>
    Task<bool> SetPermissionOverridesAsync(
        long userId, IReadOnlyList<PermissionOverride> permissionOverrides, string actor,
        AuthenticationContext context, CancellationToken ct);

    /// <summary>Sets a new password for a user, as an administrator.</summary>
    /// <remarks>Forces a change at next sign-in and revokes every session.</remarks>
    Task<bool> ResetPasswordAsync(
        long userId, string newPassword, string actor, AuthenticationContext context,
        CancellationToken ct);

    /// <summary>Changes a user's own password, after verifying the current one.</summary>
    /// <returns>
    /// <see langword="false"/> when the current password is wrong. The caller must not
    /// distinguish that from "no such user" in what it returns to the client.
    /// </returns>
    Task<bool> ChangeOwnPasswordAsync(
        long userId, string currentPassword, string newPassword, Guid currentSessionId,
        AuthenticationContext context, CancellationToken ct);

    /// <summary>Clears a lockout early.</summary>
    Task<bool> UnlockAsync(
        long userId, string actor, AuthenticationContext context, CancellationToken ct);

    /// <summary>
    /// Whether at least one active user holds every administration-critical permission.
    /// </summary>
    /// <remarks>
    /// Exposed for the bootstrap command and for tests. The guard itself runs inside each
    /// mutating transaction, where checking it is the only way it can be reliable.
    /// </remarks>
    Task<bool> AdministrationIsReachableAsync(CancellationToken ct);
}

/// <summary>Thrown when a username is already in use.</summary>
/// <remarks>
/// Raised from the unique index, not from a prior SELECT. A check-then-insert races: two
/// administrators creating the same username at the same moment both see it free.
/// </remarks>
public sealed class DuplicateUsernameException : InvalidOperationException
{
    /// <summary>The username that was already taken.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Creates the exception.</summary>
    public DuplicateUsernameException() : base("That username is already in use.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public DuplicateUsernameException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public DuplicateUsernameException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a specific username.</summary>
    public static DuplicateUsernameException For(string username, Exception? innerException = null)
    {
        var message = $"The username '{username}' is already in use.";
        return innerException is null
            ? new DuplicateUsernameException(message) { Username = username }
            : new DuplicateUsernameException(message, innerException) { Username = username };
    }
}

/// <summary>
/// Thrown when a change would leave no active user able to administer the system.
/// </summary>
/// <remarks>
/// The last administrator removing their own <c>user.manage</c> is not a hypothetical: it is one
/// mis-click on a checkbox grid, and the only recovery is a database session. The transaction is
/// rolled back and the caller gets a 409 explaining what would have happened.
/// </remarks>
public sealed class LastAdministratorException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public LastAdministratorException()
        : base("This change would leave no active user able to manage users and roles.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public LastAdministratorException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public LastAdministratorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
