namespace Sqm.Application.Dashboards;

/// <summary>A dashboard as stored, with what the reading person's grants and preferences say about it.</summary>
/// <param name="Id">Its id.</param>
/// <param name="OwnerUserId">Its owner.</param>
/// <param name="OwnerDisplayName">The owner's display name.</param>
/// <param name="Name">Its name.</param>
/// <param name="Description">Its description.</param>
/// <param name="DefinitionJson">The definition, as stored.</param>
/// <param name="SchemaVersion">The definition's schema version.</param>
/// <param name="Revision">The current revision.</param>
/// <param name="SourceTemplate">The template it was made from, or null.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="UpdatedByDisplayName">Who changed it last.</param>
/// <param name="GrantLevel">
/// The highest grant the reading person holds on it, directly or through one of their roles:
/// <c>editor</c>, <c>viewer</c>, or null. Ownership and dashboard.manage are decided from the other
/// fields, not from this.
/// </param>
/// <param name="IsFavorite">Whether the reading person marked it.</param>
/// <param name="IsDefault">Whether it is the reading person's default.</param>
/// <param name="GrantCount">How many grants it has.</param>
/// <param name="WidgetCount">How many widgets its definition holds.</param>
public sealed record DashboardRecord(
    long Id, long OwnerUserId, string OwnerDisplayName, string Name, string Description, string DefinitionJson,
    int SchemaVersion, int Revision, string? SourceTemplate, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? UpdatedByDisplayName, string? GrantLevel, bool IsFavorite, bool IsDefault, int GrantCount, int WidgetCount);

/// <summary>A dashboard to create.</summary>
/// <param name="OwnerUserId">Who will own it.</param>
/// <param name="Name">Its name, already checked.</param>
/// <param name="Description">Its description, already checked.</param>
/// <param name="DefinitionJson">The normalised, validated definition.</param>
/// <param name="SchemaVersion">The definition's schema version.</param>
/// <param name="SourceTemplate">The template it starts from, or null.</param>
public sealed record NewDashboard(
    long OwnerUserId, string Name, string Description, string DefinitionJson, int SchemaVersion, string? SourceTemplate);

/// <summary>What a write did.</summary>
public enum DashboardWriteOutcome
{
    /// <summary>Written.</summary>
    Saved,

    /// <summary>The owner already has a dashboard by that name.</summary>
    NameTaken,

    /// <summary>No such dashboard.</summary>
    NotFound,

    /// <summary>Somebody saved a newer revision first.</summary>
    Conflict,
}

/// <summary>The revision that won, for a 409.</summary>
/// <param name="CurrentRevision">The revision now stored.</param>
/// <param name="UpdatedAt">When it was saved.</param>
/// <param name="UpdatedByDisplayName">Who saved it.</param>
public sealed record DashboardConflict(int CurrentRevision, DateTimeOffset UpdatedAt, string? UpdatedByDisplayName);

/// <summary>A grant on a dashboard.</summary>
/// <param name="Id">The grant's id.</param>
/// <param name="DashboardId">The dashboard.</param>
/// <param name="SubjectType"><c>user</c> or <c>role</c>.</param>
/// <param name="SubjectId">The user's or role's id.</param>
/// <param name="SubjectName">Display name.</param>
/// <param name="SubjectDetail">A user's username; null for a role.</param>
/// <param name="Level"><c>viewer</c> or <c>editor</c>.</param>
/// <param name="GrantedAt">When.</param>
/// <param name="GrantedByDisplayName">By whom.</param>
public sealed record DashboardGrantRecord(
    long Id, long DashboardId, string SubjectType, long SubjectId, string SubjectName, string? SubjectDetail,
    string Level, DateTimeOffset GrantedAt, string? GrantedByDisplayName);

/// <summary>What a grant write did.</summary>
public enum GrantWriteOutcome
{
    /// <summary>Written.</summary>
    Saved,

    /// <summary>That user or role already has a grant on this dashboard.</summary>
    Duplicate,

    /// <summary>No such active user, or no such role.</summary>
    SubjectNotFound,

    /// <summary>The dashboard already has the most grants allowed.</summary>
    LimitReached,

    /// <summary>No such grant on this dashboard, or no such dashboard.</summary>
    NotFound,

    /// <summary>A grant to the dashboard's own owner, which would mean nothing.</summary>
    SubjectIsOwner,
}

/// <summary>A user or role a share picker may offer.</summary>
/// <param name="Type"><c>user</c> or <c>role</c>.</param>
/// <param name="Id">Their id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Detail">A user's username; null for a role.</param>
public sealed record ShareDirectoryEntry(string Type, long Id, string Name, string? Detail);

/// <summary>What <c>/</c> opens for a person.</summary>
/// <param name="DashboardId">A dashboard id, or null.</param>
/// <param name="TemplateKey">A template key, or null.</param>
public sealed record DashboardDefaultPreference(long? DashboardId, string? TemplateKey);

/// <summary>
/// Dashboards, their grants and each person's preferences, in PostgreSQL (ADR-016).
/// </summary>
/// <remarks>
/// <para>
/// <b>The store decides nothing about access.</b> It answers who owns a dashboard and which grant the
/// reading person holds on it - directly or through any role they have at the moment of the read - and
/// <see cref="DashboardAccess"/> turns that into what they may do. Every write is scoped by id, and the
/// caller has checked access first.
/// </para>
/// <para>
/// Reads resolve roles from <c>auth.user_role</c> at the time of the read, so a revoked grant, a removed
/// role or a deactivated account takes effect on the next request.
/// </para>
/// </remarks>
public interface IDashboardStore
{
    /// <summary>The person's own dashboards, most recently changed first.</summary>
    Task<IReadOnlyList<DashboardRecord>> ListOwnedAsync(long userId, CancellationToken ct);

    /// <summary>Dashboards shared with the person directly or through a role, excluding their own.</summary>
    Task<IReadOnlyList<DashboardRecord>> ListSharedWithAsync(long userId, CancellationToken ct);

    /// <summary>Everybody else's dashboards that are not shared with the person - for dashboard.manage only.</summary>
    Task<IReadOnlyList<DashboardRecord>> ListOthersAsync(long userId, CancellationToken ct);

    /// <summary>One dashboard, as seen by the reading person; null when it does not exist.</summary>
    Task<DashboardRecord?> GetAsync(long id, long readerUserId, CancellationToken ct);

    /// <summary>Creates a dashboard.</summary>
    Task<(DashboardWriteOutcome Outcome, DashboardRecord? Dashboard)> CreateAsync(NewDashboard dashboard, CancellationToken ct);

    /// <summary>
    /// Replaces name, description and definition when the stored revision is <paramref name="expectedRevision"/>,
    /// and increments it, in one statement.
    /// </summary>
    Task<(DashboardWriteOutcome Outcome, DashboardRecord? Dashboard, DashboardConflict? Conflict)> UpdateAsync(
        long id, long actorUserId, string name, string description, string definitionJson, int schemaVersion,
        int expectedRevision, CancellationToken ct);

    /// <summary>Deletes a dashboard with its grants and preferences; false when there was none.</summary>
    Task<bool> DeleteAsync(long id, CancellationToken ct);

    /// <summary>Whether the owner already has a dashboard of this name, ignoring case and surrounding spaces.</summary>
    Task<bool> NameExistsAsync(long ownerUserId, string name, CancellationToken ct);

    /// <summary>A dashboard's grants, people first, then roles, each by name.</summary>
    Task<IReadOnlyList<DashboardGrantRecord>> ListGrantsAsync(long dashboardId, CancellationToken ct);

    /// <summary>Adds a grant, refusing a duplicate, an unknown or inactive subject, the owner, and more than <paramref name="maxGrants"/>.</summary>
    Task<(GrantWriteOutcome Outcome, DashboardGrantRecord? Grant)> AddGrantAsync(
        long dashboardId, string subjectType, long subjectId, string level, long actorUserId, int maxGrants, CancellationToken ct);

    /// <summary>Changes a grant's level.</summary>
    Task<(GrantWriteOutcome Outcome, DashboardGrantRecord? Grant)> UpdateGrantAsync(
        long dashboardId, long grantId, string level, CancellationToken ct);

    /// <summary>Removes a grant; the removed grant, or null when there was none.</summary>
    Task<DashboardGrantRecord?> RemoveGrantAsync(long dashboardId, long grantId, CancellationToken ct);

    /// <summary>Marks or unmarks a favourite. Idempotent.</summary>
    Task SetFavoriteAsync(long userId, long dashboardId, bool favorite, CancellationToken ct);

    /// <summary>What <c>/</c> opens for the person, as stored; the caller checks it can still be opened.</summary>
    Task<DashboardDefaultPreference> GetDefaultAsync(long userId, CancellationToken ct);

    /// <summary>Sets what <c>/</c> opens: one of the two, or neither.</summary>
    Task SetDefaultAsync(long userId, long? dashboardId, string? templateKey, CancellationToken ct);

    /// <summary>Active users and roles whose name (or username) contains the term, at most <paramref name="limit"/> of each.</summary>
    /// <remarks>Users only when the term has at least <paramref name="minUserQuery"/> characters; roles always.</remarks>
    Task<(IReadOnlyList<ShareDirectoryEntry> Users, IReadOnlyList<ShareDirectoryEntry> Roles)> SearchShareDirectoryAsync(
        string term, int limit, int minUserQuery, CancellationToken ct);
}
