using Sqm.Application.Identity;
using Sqm.Contracts.Dashboards;

namespace Sqm.Application.Dashboards;

/// <summary>How a person stands towards one dashboard, strongest last.</summary>
public enum DashboardAccessLevel
{
    /// <summary>Cannot open it. Reported as 404, never 403: whether it exists is not theirs to know.</summary>
    None,

    /// <summary>A viewer grant, directly or through a role.</summary>
    Viewer,

    /// <summary>An editor grant, directly or through a role.</summary>
    Editor,

    /// <summary>Not theirs and not shared with them, but they hold dashboard.manage.</summary>
    Manager,

    /// <summary>They own it.</summary>
    Owner,
}

/// <summary>
/// Who may do what with a dashboard (ADR-016 section 9). Pure: decided from ownership, the reading
/// person's strongest grant and their permissions, all read fresh for the request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dashboard access and data access are two checks.</b> Nothing here lets anybody see data: every
/// widget still needs its own permission, checked by its own endpoint for the person looking.
/// </para>
/// <para>
/// <b>Each act needs its permission as well as the standing.</b> An editor grant edits only while the
/// person holds dashboard.edit; an owner shares only while holding dashboard.share. Sharing, revoking
/// and deleting belong to the owner and to dashboard.manage - an editor cannot widen access.
/// </para>
/// </remarks>
public static class DashboardAccess
{
    /// <summary>The person's standing towards a dashboard.</summary>
    /// <param name="ownerUserId">The dashboard's owner.</param>
    /// <param name="grantLevel">Their strongest grant on it - <c>editor</c>, <c>viewer</c> or null.</param>
    /// <param name="user">The person, with permissions resolved for this request.</param>
    public static DashboardAccessLevel Resolve(long ownerUserId, string? grantLevel, AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (ownerUserId == user.UserId)
        {
            return DashboardAccessLevel.Owner;
        }

        if (user.Can(Permissions.DashboardManage))
        {
            return DashboardAccessLevel.Manager;
        }

        return grantLevel switch
        {
            GrantLevels.Editor => DashboardAccessLevel.Editor,
            GrantLevels.Viewer => DashboardAccessLevel.Viewer,
            _ => DashboardAccessLevel.None,
        };
    }

    /// <summary>Whether the person may change its widgets, layout, name and description.</summary>
    public static bool CanEdit(DashboardAccessLevel level, AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return level switch
        {
            DashboardAccessLevel.Owner or DashboardAccessLevel.Editor => user.Can(Permissions.DashboardEdit),
            DashboardAccessLevel.Manager => true,
            _ => false,
        };
    }

    /// <summary>Whether the person may add, change and revoke its grants.</summary>
    public static bool CanShare(DashboardAccessLevel level, AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return level switch
        {
            DashboardAccessLevel.Owner => user.Can(Permissions.DashboardShare),
            DashboardAccessLevel.Manager => true,
            _ => false,
        };
    }

    /// <summary>Whether the person may delete it. An owner always may: removing one's own arrangement exposes nothing.</summary>
    public static bool CanDelete(DashboardAccessLevel level) =>
        level is DashboardAccessLevel.Owner or DashboardAccessLevel.Manager;

    /// <summary>Whether the person may copy it into a dashboard of their own.</summary>
    public static bool CanDuplicate(DashboardAccessLevel level, AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return level != DashboardAccessLevel.None && user.Can(Permissions.DashboardCreate);
    }

    /// <summary>Whether the person may export its data at all; each widget still needs its own permission.</summary>
    public static bool CanExportData(DashboardAccessLevel level, AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return level != DashboardAccessLevel.None && user.Can(Permissions.DataExport);
    }

    /// <summary>The access as the API describes it.</summary>
    public static DashboardAccessInfo Describe(DashboardAccessLevel level, AuthenticatedUser user) => new(
        level switch
        {
            DashboardAccessLevel.Owner => "owner",
            DashboardAccessLevel.Manager => "manager",
            DashboardAccessLevel.Editor => "editor",
            DashboardAccessLevel.Viewer => "viewer",
            _ => "none",
        },
        CanEdit(level, user),
        CanShare(level, user),
        CanDelete(level),
        CanDuplicate(level, user),
        CanExportData(level, user));
}

/// <summary>The two grant levels, as stored.</summary>
public static class GrantLevels
{
    /// <summary>Opens the dashboard.</summary>
    public const string Viewer = "viewer";

    /// <summary>Also changes it, while holding dashboard.edit.</summary>
    public const string Editor = "editor";

    /// <summary>Whether a value is one of the two.</summary>
    public static bool IsValid(string? level) => level is Viewer or Editor;
}
