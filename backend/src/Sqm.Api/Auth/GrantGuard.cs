using Sqm.Application.Identity;

namespace Sqm.Api.Auth;

/// <summary>
/// Nobody can hand out a permission they do not hold, and nobody can change their own access.
/// </summary>
/// <remarks>
/// <para>
/// Without this, <c>user.manage</c> and <c>role.manage</c> were each equivalent to everything: a
/// holder of either could give themselves <c>system.admin</c> - directly, through a role, or by
/// adding it to a role they already belong to - and every later check would believe it. The rule
/// is the product owner's: a manager grants only what they hold, and never to themselves.
/// </para>
/// <para>
/// Only what a change <em>adds</em> is checked. Keeping a role the target already has, or taking
/// something away, needs nothing more than the manage permission itself; otherwise a manager
/// could not remove access they do not happen to hold.
/// </para>
/// </remarks>
public static class GrantGuard
{
    /// <summary>The codes in <paramref name="codes"/> that <paramref name="actor"/> does not hold.</summary>
    public static IReadOnlyList<string> NotHeld(AuthenticatedUser actor, IEnumerable<string> codes)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(codes);

        return [.. codes
            .Where(c => !actor.Can(c))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>The permissions carried by roles that are in the request but not already held.</summary>
    /// <param name="currentRoleCodes">Roles the target has now.</param>
    /// <param name="requestedRoleCodes">Roles the request asks for.</param>
    /// <param name="permissionsByRole">Every role's permission codes, by role code.</param>
    /// <remarks>An unknown role code contributes nothing here; the directory refuses it with its own
    /// message.</remarks>
    public static IEnumerable<string> AddedByRoles(
        IEnumerable<string> currentRoleCodes,
        IEnumerable<string> requestedRoleCodes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> permissionsByRole)
    {
        ArgumentNullException.ThrowIfNull(permissionsByRole);

        var current = currentRoleCodes.ToHashSet(StringComparer.Ordinal);

        return requestedRoleCodes
            .Where(code => !current.Contains(code))
            .SelectMany(code => permissionsByRole.TryGetValue(code, out var p) ? p : []);
    }

    /// <summary>
    /// Every permission whose direct override the request loosens: a grant that was not there
    /// before, or a deny that is no longer there.
    /// </summary>
    /// <remarks>
    /// Removing a deny counts because it is a grant whenever a role carries the permission.
    /// Tightening - dropping a grant, adding a deny - is not listed.
    /// </remarks>
    public static IEnumerable<string> LoosenedByOverrides(
        IEnumerable<PermissionResolution> current,
        IEnumerable<PermissionOverride> requested)
    {
        var before = new Dictionary<string, PermissionEffect>(StringComparer.Ordinal);
        foreach (var p in current)
        {
            if (p.GrantedDirectly)
            {
                before[p.Code] = PermissionEffect.Grant;
            }
            else if (p.DeniedDirectly)
            {
                before[p.Code] = PermissionEffect.Deny;
            }
        }

        var after = new Dictionary<string, PermissionEffect>(StringComparer.Ordinal);
        foreach (var o in requested)
        {
            after[o.PermissionCode] = o.Effect;
        }

        bool Is(Dictionary<string, PermissionEffect> map, string code, PermissionEffect effect) =>
            map.TryGetValue(code, out var e) && e == effect;

        return before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(code =>
                (Is(after, code, PermissionEffect.Grant) && !Is(before, code, PermissionEffect.Grant))
                || (Is(before, code, PermissionEffect.Deny) && !Is(after, code, PermissionEffect.Deny)));
    }

    /// <summary>The refusal for a grant the actor could not make themselves.</summary>
    public static IResult Refuse(IReadOnlyList<string> notHeld) => Results.Problem(
        title: "You cannot grant a permission you do not hold",
        detail: "This change would give access you do not have yourself: "
                + string.Join(", ", notHeld) + ". Ask someone who holds it.",
        statusCode: StatusCodes.Status403Forbidden);

    /// <summary>The refusal for changing one's own access.</summary>
    public static IResult RefuseSelf() => Results.Problem(
        title: "You cannot change your own roles or permissions",
        detail: "Ask another administrator to do it.",
        statusCode: StatusCodes.Status403Forbidden);
}
