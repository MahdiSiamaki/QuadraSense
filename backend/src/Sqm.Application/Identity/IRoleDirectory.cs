namespace Sqm.Application.Identity;

/// <summary>Reads and writes roles, and reads the permission catalogue.</summary>
/// <remarks>
/// The catalogue is read-only through the application. Adding a permission means adding the code
/// that enforces it, so it belongs in a migration reviewed alongside that code - not in a form an
/// administrator can fill in to create a permission nothing checks.
/// </remarks>
public interface IRoleDirectory
{
    /// <summary>Every permission in the catalogue, ordered for display.</summary>
    Task<IReadOnlyList<PermissionDefinition>> GetPermissionCatalogueAsync(CancellationToken ct);

    /// <summary>Every role, with member counts and permission codes.</summary>
    Task<IReadOnlyList<RoleDetail>> GetRolesAsync(CancellationToken ct);

    /// <summary>One role, or <see langword="null"/>.</summary>
    Task<RoleDetail?> GetRoleAsync(long roleId, CancellationToken ct);

    /// <summary>The users holding a role.</summary>
    Task<IReadOnlyList<UserSummary>> GetMembersAsync(long roleId, CancellationToken ct);

    /// <summary>Creates a role.</summary>
    Task<long> CreateRoleAsync(
        string code, string displayName, string description, IReadOnlyList<string> permissionCodes,
        string actor, AuthenticationContext context, CancellationToken ct);

    /// <summary>Renames a role and rewrites its description.</summary>
    /// <remarks>
    /// The code is immutable, for system and custom roles alike. It is what audit entries,
    /// deployment notes and any future directory-group mapping refer to, and a renamed code makes
    /// every one of those silently wrong rather than loudly broken.
    /// </remarks>
    Task<bool> UpdateRoleAsync(
        long roleId, string displayName, string description, string actor,
        AuthenticationContext context, CancellationToken ct);

    /// <summary>Replaces a role's permissions with exactly this set.</summary>
    /// <exception cref="LastAdministratorException">
    /// The change would leave nobody able to administer the system.
    /// </exception>
    Task<bool> SetRolePermissionsAsync(
        long roleId, IReadOnlyList<string> permissionCodes, string actor,
        AuthenticationContext context, CancellationToken ct);

    /// <summary>Deletes a role.</summary>
    /// <returns>
    /// <see langword="false"/> if the role does not exist. Throws if it is a system role or still
    /// has members - both are refusals with a reason, not silent no-ops.
    /// </returns>
    Task<bool> DeleteRoleAsync(
        long roleId, string actor, AuthenticationContext context, CancellationToken ct);
}

/// <summary>Thrown when a role cannot be changed the way the caller asked.</summary>
public sealed class RoleChangeRefusedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public RoleChangeRefusedException() : base("That change to the role is not allowed.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public RoleChangeRefusedException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public RoleChangeRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
