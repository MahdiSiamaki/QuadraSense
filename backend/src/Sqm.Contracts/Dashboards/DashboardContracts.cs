using System.Text.Json;

namespace Sqm.Contracts.Dashboards;

// The dashboard builder's API (ADR-016), under /api/v1/dashboards - plural, so it cannot collide with
// the analytics routes under /api/v1/dashboard. A definition travels as raw JSON and is checked
// against the widget contract by the server; nothing here carries a result except the export.

/// <summary>Who owns a dashboard, by display name only.</summary>
/// <param name="Id">The owner's user id.</param>
/// <param name="DisplayName">Their display name.</param>
public sealed record DashboardOwnerInfo(long Id, string DisplayName);

/// <summary>What the caller may do with one dashboard, resolved on the server for this request.</summary>
/// <param name="Level">owner, manager, editor or viewer.</param>
/// <param name="CanEdit">Change its widgets, layout, name and description.</param>
/// <param name="CanShare">Add, change and revoke grants.</param>
/// <param name="CanDelete">Delete it.</param>
/// <param name="CanDuplicate">Copy it into a dashboard of one's own.</param>
/// <param name="CanExportData">Export its data: data.export. Each widget still needs its own permission.</param>
public sealed record DashboardAccessInfo(
    string Level, bool CanEdit, bool CanShare, bool CanDelete, bool CanDuplicate, bool CanExportData);

/// <summary>One row of the switcher.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Description">Its description.</param>
/// <param name="Owner">Its owner.</param>
/// <param name="Access">What the caller may do with it.</param>
/// <param name="IsShared">Whether it has any grant.</param>
/// <param name="IsFavorite">Whether the caller marked it.</param>
/// <param name="IsDefault">Whether it is the caller's default.</param>
/// <param name="Revision">Its current revision.</param>
/// <param name="WidgetCount">How many widgets it holds.</param>
/// <param name="UpdatedAt">When it last changed.</param>
public sealed record DashboardSummary(
    long Id, string Name, string Description, DashboardOwnerInfo Owner, DashboardAccessInfo Access,
    bool IsShared, bool IsFavorite, bool IsDefault, int Revision, int WidgetCount, DateTimeOffset UpdatedAt);

/// <summary>What <c>/</c> opens for the caller: a dashboard, a template, or neither (Device Population).</summary>
/// <param name="DashboardId">A dashboard the caller can still open, or null.</param>
/// <param name="TemplateKey">A template key, or null.</param>
public sealed record DashboardDefaultInfo(long? DashboardId, string? TemplateKey);

/// <summary>The configured limits, so the client can say them before the server refuses.</summary>
public sealed record DashboardLimitsInfo(
    int MaxWidgets, int MaxNameLength, int MaxDescriptionLength, int MaxTitleLength, int MaxSubtitleLength,
    int MaxWidgetConfigBytes, int MaxDefinitionBytes, int MaxGrants);

/// <summary>The caller's dashboards and those shared with them, with their default.</summary>
/// <param name="Mine">Owned by the caller, most recently changed first.</param>
/// <param name="SharedWithMe">Shared with the caller directly or through a role.</param>
/// <param name="Others">
/// Everybody else's dashboards, only for dashboard.manage and only when asked for
/// (<c>?scope=all</c>); null otherwise. An administrator needs to find a dashboard to manage it, but
/// not to have every dashboard in the system in their switcher.
/// </param>
/// <param name="Default">What <c>/</c> opens.</param>
/// <param name="Limits">The configured limits.</param>
public sealed record DashboardListResponse(
    IReadOnlyList<DashboardSummary> Mine, IReadOnlyList<DashboardSummary> SharedWithMe,
    IReadOnlyList<DashboardSummary>? Others, DashboardDefaultInfo Default, DashboardLimitsInfo Limits);

/// <summary>One dashboard, whole.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Description">Its description.</param>
/// <param name="Owner">Its owner.</param>
/// <param name="Definition">The definition as stored: <c>{ schemaVersion, defaults, refreshMinutes, widgets[] }</c>.</param>
/// <param name="SchemaVersion">The definition's schema version.</param>
/// <param name="Revision">The revision an update must name.</param>
/// <param name="SourceTemplate">The template it was made from, for provenance only.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="UpdatedBy">Who changed it last, by display name.</param>
/// <param name="Access">What the caller may do with it.</param>
/// <param name="IsFavorite">Whether the caller marked it.</param>
/// <param name="IsDefault">Whether it is the caller's default.</param>
/// <param name="GrantCount">How many grants it has; the grants themselves are for those who can share.</param>
public sealed record DashboardDetail(
    long Id, string Name, string Description, DashboardOwnerInfo Owner, JsonElement Definition,
    int SchemaVersion, int Revision, string? SourceTemplate, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? UpdatedBy, DashboardAccessInfo Access, bool IsFavorite, bool IsDefault, int GrantCount);

/// <summary>A new dashboard of the caller's own.</summary>
/// <param name="Name">1 to 100 characters, unique among the caller's dashboards.</param>
/// <param name="Description">Up to 1,000 characters.</param>
/// <param name="Definition">Checked against the widget contract.</param>
/// <param name="SourceTemplate">The template it starts from, as <c>key@version</c>, or null.</param>
public sealed record CreateDashboardRequest(string Name, string? Description, JsonElement Definition, string? SourceTemplate);

/// <summary>A whole new version of a dashboard.</summary>
/// <param name="Name">1 to 100 characters.</param>
/// <param name="Description">Up to 1,000 characters.</param>
/// <param name="Definition">Checked against the widget contract.</param>
/// <param name="ExpectedRevision">The revision the change started from; a different current revision is a 409.</param>
public sealed record UpdateDashboardRequest(string Name, string? Description, JsonElement Definition, int ExpectedRevision);

/// <summary>A copy of a dashboard the caller can open, as a new private dashboard of their own.</summary>
/// <param name="Name">The copy's name; "Copy of …" when omitted, numbered if taken.</param>
public sealed record DuplicateDashboardRequest(string? Name);

/// <summary>One grant on a dashboard.</summary>
/// <param name="Id">The grant's id.</param>
/// <param name="SubjectType">user or role.</param>
/// <param name="SubjectId">The user's or role's id.</param>
/// <param name="SubjectName">Display name.</param>
/// <param name="SubjectDetail">For a user, the username; for a role, null.</param>
/// <param name="Level">viewer or editor.</param>
/// <param name="GrantedAt">When.</param>
/// <param name="GrantedBy">By whom, by display name.</param>
public sealed record DashboardGrantInfo(
    long Id, string SubjectType, long SubjectId, string SubjectName, string? SubjectDetail, string Level,
    DateTimeOffset GrantedAt, string? GrantedBy);

/// <summary>A dashboard's grants.</summary>
public sealed record DashboardGrantsResponse(IReadOnlyList<DashboardGrantInfo> Grants, int MaxGrants);

/// <summary>Shares a dashboard with a user or a role.</summary>
/// <param name="SubjectType">user or role.</param>
/// <param name="SubjectId">The user's or role's id.</param>
/// <param name="Level">viewer or editor.</param>
public sealed record CreateDashboardGrantRequest(string SubjectType, long SubjectId, string Level);

/// <summary>Changes a grant's level.</summary>
/// <param name="Level">viewer or editor.</param>
public sealed record UpdateDashboardGrantRequest(string Level);

/// <summary>Someone or some role a dashboard can be shared with. Nothing more than a picker needs.</summary>
/// <param name="Type">user or role.</param>
/// <param name="Id">Their id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Detail">For a user, the username, to tell two people of the same name apart.</param>
public sealed record ShareSubject(string Type, long Id, string Name, string? Detail);

/// <summary>Matches for a share picker: active users and roles.</summary>
public sealed record ShareDirectoryResponse(IReadOnlyList<ShareSubject> Users, IReadOnlyList<ShareSubject> Roles);

/// <summary>Sets what <c>/</c> opens: a dashboard, a template, or neither.</summary>
/// <param name="DashboardId">A dashboard the caller can open.</param>
/// <param name="TemplateKey">A template key.</param>
public sealed record SetDefaultDashboardRequest(long? DashboardId, string? TemplateKey);

/// <summary>Whether the caller may use one widget type, and if not, what they lack.</summary>
/// <param name="Type">The widget type.</param>
/// <param name="ConfigVersion">The config version this server accepts.</param>
/// <param name="Available">Whether the caller holds every fixed permission it needs.</param>
/// <param name="MissingPermissions">The fixed permissions the caller lacks.</param>
/// <param name="AvailableOptions">
/// For a type whose permission follows a config field (a risk list's rule, a lookup launcher's
/// target): the values of that field the caller may choose. Null for other types.
/// </param>
public sealed record WidgetTypeAvailability(
    string Type, int ConfigVersion, bool Available, IReadOnlyList<string> MissingPermissions,
    IReadOnlyList<string>? AvailableOptions);

/// <summary>Which widget types the caller may use.</summary>
public sealed record WidgetTypesResponse(int ContractVersion, IReadOnlyList<WidgetTypeAvailability> Types);

/// <summary>A dashboard's structure as a file: no results, no owner, no grants, no preferences.</summary>
/// <param name="Format">Always <c>quadrasense.dashboard</c>.</param>
/// <param name="FormatVersion">The file format's version, 1.</param>
/// <param name="ExportedAt">When it was exported.</param>
/// <param name="Name">The dashboard's name.</param>
/// <param name="Description">Its description.</param>
/// <param name="Definition">The definition.</param>
public sealed record DashboardConfigDocument(
    string Format, int FormatVersion, DateTimeOffset ExportedAt, string Name, string Description, JsonElement Definition);

/// <summary>Creates a private dashboard from a config file, with new widget ids.</summary>
/// <param name="Document">The file's content.</param>
/// <param name="Name">A name to use instead of the file's.</param>
public sealed record ImportDashboardRequest(DashboardConfigDocument Document, string? Name);

/// <summary>
/// The view a dashboard is looked at through: count unit, unknown devices, date range and device
/// filters. Never an MSISDN, IMSI or IMEI.
/// </summary>
/// <param name="CountBy">bindings, subscribers or handsets.</param>
/// <param name="IncludeUnknownDevice">Whether the 000000 population is counted.</param>
/// <param name="Range">all, last7 … last365, or <c>yyyy-MM-dd..yyyy-MM-dd</c>.</param>
/// <param name="Vendor">Vendor filter.</param>
/// <param name="DeviceType">Device type filter.</param>
/// <param name="OperatingSystem">Operating system filter.</param>
/// <param name="Tac">TAC filter, eight digits.</param>
public sealed record DashboardLens(
    string? CountBy = null, bool? IncludeUnknownDevice = null, string? Range = null, string? Vendor = null,
    string? DeviceType = null, string? OperatingSystem = null, string? Tac = null);

/// <summary>Asks the server to prepare a dashboard's data for an image or a printout.</summary>
/// <param name="DashboardId">The saved dashboard it shows, for the access check and the audit; null for a template or an unsaved draft.</param>
/// <param name="Name">The title the export carries.</param>
/// <param name="Definition">The definition being shown - checked like any other, and run under the caller's permissions.</param>
/// <param name="Lens">The URL lens in force.</param>
/// <param name="WidgetIds">Which widgets; all when null.</param>
/// <param name="Purpose">png or print.</param>
public sealed record DashboardSnapshotRequest(
    long? DashboardId, string? Name, JsonElement Definition, DashboardLens? Lens,
    IReadOnlyList<string>? WidgetIds, string Purpose);

/// <summary>One widget's prepared data.</summary>
/// <param name="Id">The instance id.</param>
/// <param name="Type">The widget type.</param>
/// <param name="Status">ok, forbidden, notReady, error, interactive (re-run through its own audited endpoint) or noData (a launcher).</param>
/// <param name="Reason">Why it is not ok, written for a person.</param>
/// <param name="Data">The same shape the widget's own endpoint returns.</param>
/// <param name="AsOf">What the data is as of, in the source's own terms; null when the source does not say.</param>
/// <param name="Notes">Caveats the export must carry: network-wide, estimated, capped, overridden.</param>
/// <param name="Effective">The lens values this widget was computed with.</param>
public sealed record WidgetSnapshot(
    string Id, string Type, string Status, string? Reason, object? Data, string? AsOf,
    IReadOnlyList<string> Notes, DashboardLens Effective);

/// <summary>A dashboard's data, prepared for an image or a printout.</summary>
/// <param name="DashboardId">The saved dashboard, if any.</param>
/// <param name="Name">The title.</param>
/// <param name="PreparedAt">When the server prepared it - not when the data was produced; each widget says that.</param>
/// <param name="Lens">The lens it was prepared under.</param>
/// <param name="Widgets">One entry per widget asked for, in definition order.</param>
public sealed record DashboardSnapshotResponse(
    long? DashboardId, string Name, DateTimeOffset PreparedAt, DashboardLens Lens, IReadOnlyList<WidgetSnapshot> Widgets);

/// <summary>Asks for widget data as CSV: one widget as a file, several as a ZIP.</summary>
/// <param name="DashboardId">The saved dashboard, if any.</param>
/// <param name="Name">The title, used in file names.</param>
/// <param name="Definition">The definition being shown.</param>
/// <param name="Lens">The URL lens in force.</param>
/// <param name="WidgetIds">At least one.</param>
public sealed record DashboardCsvRequest(
    long? DashboardId, string? Name, JsonElement Definition, DashboardLens? Lens, IReadOnlyList<string> WidgetIds);
