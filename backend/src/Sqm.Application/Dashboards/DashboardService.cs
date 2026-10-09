using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sqm.Application.Identity;
using Sqm.Contracts.Dashboards;

namespace Sqm.Application.Dashboards;

/// <summary>What a dashboard operation came to, for the API to turn into a status code.</summary>
public enum DashboardOutcome
{
    /// <summary>Done.</summary>
    Ok,

    /// <summary>No such dashboard, or one the caller cannot open - the two are not told apart (404).</summary>
    NotFound,

    /// <summary>The caller can open it but may not do this (403).</summary>
    Forbidden,

    /// <summary>The request or the definition is invalid (400).</summary>
    Invalid,

    /// <summary>The caller already has a dashboard of that name (409).</summary>
    NameTaken,

    /// <summary>Somebody saved a newer revision first (409).</summary>
    Conflict,

    /// <summary>The grant cannot be made: duplicate, unknown subject, the owner, or past the limit (409/400).</summary>
    GrantRefused,
}

/// <summary>The result of a dashboard operation.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Value">The result, when there is one.</param>
/// <param name="Errors">For Invalid: what is wrong, by path.</param>
/// <param name="Conflict">For Conflict: the revision that won.</param>
/// <param name="Message">For a refusal: why, written for a person.</param>
public sealed record DashboardResult<T>(
    DashboardOutcome Outcome, T? Value = default, IReadOnlyList<DefinitionError>? Errors = null,
    DashboardConflict? Conflict = null, string? Message = null);

/// <summary>
/// Dashboards: who may do what, checked on every call, and an audit of every change (ADR-016 §9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Access is resolved from the database for each request</b> - ownership, the caller's strongest
/// grant through themselves or a role they hold now, and their permissions - and a dashboard they
/// cannot open is reported exactly as one that does not exist. Changing the id in a request reaches
/// nothing.
/// </para>
/// <para>
/// <b>Audited by id and name, never by content.</b> A definition can hold a filter value, and the
/// audit log must not become a copy of what people looked at.
/// </para>
/// </remarks>
public sealed class DashboardService
{
    /// <summary>The config file format this build writes and reads.</summary>
    public const string ConfigFormat = "quadrasense.dashboard";

    /// <summary>The config file format version.</summary>
    public const int ConfigFormatVersion = 1;

    private static readonly Regex TemplateKey = new("^[a-z][a-z0-9-]{1,47}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SourceTemplate = new("^[a-z][a-z0-9-]{1,47}@[0-9]{1,4}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private readonly IDashboardStore _store;
    private readonly IDashboardDefinitionValidator _validator;
    private readonly IWidgetContract _contract;
    private readonly DashboardOptions _options;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;

    /// <summary>Creates the service.</summary>
    public DashboardService(
        IDashboardStore store, IDashboardDefinitionValidator validator, IWidgetContract contract,
        DashboardOptions options, IAuditLog audit, TimeProvider clock)
    {
        _store = store;
        _validator = validator;
        _contract = contract;
        _options = options;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>The limits the client is told.</summary>
    public DashboardLimitsInfo Limits => new(
        _options.MaxWidgets, _options.MaxNameLength, _options.MaxDescriptionLength, _options.MaxTitleLength,
        _options.MaxSubtitleLength, _options.MaxWidgetConfigBytes, _options.MaxDefinitionBytes, _options.MaxGrants);

    // -------------------------------------------------------------------- read

    /// <summary>The caller's dashboards, those shared with them, and - for dashboard.manage, when asked - everybody else's.</summary>
    public async Task<DashboardListResponse> ListAsync(AuthenticatedUser user, bool includeOthers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        var mine = await _store.ListOwnedAsync(user.UserId, ct).ConfigureAwait(false);
        var shared = await _store.ListSharedWithAsync(user.UserId, ct).ConfigureAwait(false);
        IReadOnlyList<DashboardSummary>? others = null;
        if (includeOthers && user.Can(Permissions.DashboardManage))
        {
            others = [.. (await _store.ListOthersAsync(user.UserId, ct).ConfigureAwait(false)).Select(r => Summary(r, user))];
        }

        return new DashboardListResponse(
            [.. mine.Select(r => Summary(r, user))],
            [.. shared.Select(r => Summary(r, user))],
            others,
            await DefaultAsync(user, ct).ConfigureAwait(false),
            Limits);
    }

    /// <summary>One dashboard, if the caller can open it.</summary>
    public async Task<DashboardResult<DashboardDetail>> GetAsync(AuthenticatedUser user, long id, CancellationToken ct)
    {
        var (record, level) = await OpenAsync(user, id, ct).ConfigureAwait(false);
        return record is null ? NotFound<DashboardDetail>() : Done<DashboardDetail>(Detail(record, level, user));
    }

    /// <summary>Which widget types the caller may use. The catalogue reflects it; the server decides it.</summary>
    public WidgetTypesResponse WidgetTypes(AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new WidgetTypesResponse(_contract.ContractVersion, [.. _contract.Types.Values
            .OrderBy(t => t.Type, StringComparer.Ordinal)
            .Select(t =>
            {
                var missing = t.Permissions.Where(p => !user.Can(p)).ToList();
                var options = WidgetPermissions.AvailableOptions(t, user);
                var available = missing.Count == 0 && (options is null || options.Count > 0);
                return new WidgetTypeAvailability(t.Type, t.ConfigVersion, available, missing, options);
            })]);
    }

    // ------------------------------------------------------------------- write

    /// <summary>A new dashboard of the caller's own. The route requires dashboard.create.</summary>
    public async Task<DashboardResult<DashboardDetail>> CreateAsync(
        AuthenticatedUser user, CreateDashboardRequest request, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        if (Describe(request.Name, request.Description) is { } refusal)
        {
            return Invalid<DashboardDetail>(refusal);
        }

        if (request.SourceTemplate is { } source && !SourceTemplate.IsMatch(source))
        {
            return Invalid<DashboardDetail>(new DefinitionError("sourceTemplate", "A template reference is key@version."));
        }

        var check = _validator.Check(request.Definition);
        if (!check.IsValid)
        {
            return new DashboardResult<DashboardDetail>(DashboardOutcome.Invalid, Errors: check.Errors);
        }

        return await CreateCheckedAsync(user, request.Name, request.Description ?? string.Empty, check.Definition!,
            request.SourceTemplate, "dashboard.create", context, ct).ConfigureAwait(false);
    }

    /// <summary>Replaces a dashboard's name, description and definition, if nobody saved since <c>ExpectedRevision</c>.</summary>
    public async Task<DashboardResult<DashboardDetail>> UpdateAsync(
        AuthenticatedUser user, long id, UpdateDashboardRequest request, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        var (record, level) = await OpenAsync(user, id, ct).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound<DashboardDetail>();
        }

        if (!DashboardAccess.CanEdit(level, user))
        {
            return Forbidden<DashboardDetail>("You can open this dashboard but not change it.");
        }

        if (Describe(request.Name, request.Description) is { } refusal)
        {
            return Invalid<DashboardDetail>(refusal);
        }

        var check = _validator.Check(request.Definition);
        if (!check.IsValid)
        {
            return new DashboardResult<DashboardDetail>(DashboardOutcome.Invalid, Errors: check.Errors);
        }

        var definition = check.Definition!;
        var (outcome, saved, conflict) = await _store.UpdateAsync(
            id, user.UserId, request.Name.Trim(), request.Description ?? string.Empty, definition.NormalizedJson,
            definition.SchemaVersion, request.ExpectedRevision, ct).ConfigureAwait(false);

        switch (outcome)
        {
            case DashboardWriteOutcome.NotFound:
                return NotFound<DashboardDetail>();
            case DashboardWriteOutcome.NameTaken:
                return NameTaken<DashboardDetail>(request.Name);
            case DashboardWriteOutcome.Conflict:
                return new DashboardResult<DashboardDetail>(DashboardOutcome.Conflict, Conflict: conflict,
                    Message: "Somebody saved this dashboard after you started editing it.");
        }

        await AuditAsync(user, context, "dashboard.update", saved!, new()
        {
            ["revision"] = saved!.Revision,
            ["widgets"] = definition.Widgets.Count,
            ["renamed"] = !string.Equals(record.Name, saved.Name, StringComparison.Ordinal),
        }, ct).ConfigureAwait(false);

        return Done<DashboardDetail>(Detail(saved, level, user));
    }

    /// <summary>Deletes a dashboard - the arrangement only; no data is touched. Owner or dashboard.manage.</summary>
    public async Task<DashboardResult<bool>> DeleteAsync(AuthenticatedUser user, long id, AuthenticationContext context, CancellationToken ct)
    {
        var (record, level) = await OpenAsync(user, id, ct).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound<bool>();
        }

        if (!DashboardAccess.CanDelete(level))
        {
            return Forbidden<bool>("Only its owner can delete a dashboard.");
        }

        if (!await _store.DeleteAsync(id, ct).ConfigureAwait(false))
        {
            return NotFound<bool>();
        }

        await AuditAsync(user, context, "dashboard.delete", record, new() { ["owner"] = record.OwnerUserId }, ct).ConfigureAwait(false);
        return Done<bool>(true);
    }

    /// <summary>A private copy of a dashboard the caller can open. Grants are not copied. The route requires dashboard.create.</summary>
    public async Task<DashboardResult<DashboardDetail>> DuplicateAsync(
        AuthenticatedUser user, long id, DuplicateDashboardRequest request, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        var (record, level) = await OpenAsync(user, id, ct).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound<DashboardDetail>();
        }

        if (!DashboardAccess.CanDuplicate(level, user))
        {
            return Forbidden<DashboardDetail>("Copying a dashboard needs dashboard.create.");
        }

        // Re-checked, not trusted: the contract may have changed since it was saved.
        using var document = JsonDocument.Parse(record.DefinitionJson);
        var check = _validator.Check(document.RootElement);
        if (!check.IsValid)
        {
            return new DashboardResult<DashboardDetail>(DashboardOutcome.Invalid, Errors: check.Errors,
                Message: "This dashboard holds widgets this version cannot copy. Open it, remove them, and copy again.");
        }

        var name = string.IsNullOrWhiteSpace(request.Name)
            ? await FreeNameAsync(user.UserId, $"Copy of {record.Name}", ct).ConfigureAwait(false)
            : request.Name;

        if (Describe(name, record.Description) is { } refusal)
        {
            return Invalid<DashboardDetail>(refusal);
        }

        return await CreateCheckedAsync(user, name, record.Description, check.Definition!, record.SourceTemplate,
            "dashboard.duplicate", context, ct, new() { ["from"] = record.Id }).ConfigureAwait(false);
    }

    /// <summary>A private dashboard from a config file, with new widget ids. Carries no data and no access.</summary>
    public async Task<DashboardResult<DashboardDetail>> ImportAsync(
        AuthenticatedUser user, ImportDashboardRequest request, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        var document = request.Document;
        if (document is null || document.Format != ConfigFormat)
        {
            return Invalid<DashboardDetail>(new DefinitionError("document.format", $"Not a dashboard file ({ConfigFormat})."));
        }

        if (document.FormatVersion != ConfigFormatVersion)
        {
            return Invalid<DashboardDetail>(new DefinitionError("document.formatVersion", $"This version reads dashboard files of version {ConfigFormatVersion}."));
        }

        if (document.Definition.ValueKind != JsonValueKind.Object)
        {
            return Invalid<DashboardDetail>(new DefinitionError("document.definition", "The file holds no dashboard."));
        }

        var check = _validator.Check(WithNewIds(document.Definition));
        if (!check.IsValid)
        {
            return new DashboardResult<DashboardDetail>(DashboardOutcome.Invalid, Errors: check.Errors);
        }

        var name = string.IsNullOrWhiteSpace(request.Name)
            ? await FreeNameAsync(user.UserId, string.IsNullOrWhiteSpace(document.Name) ? "Imported dashboard" : document.Name, ct).ConfigureAwait(false)
            : request.Name;

        var description = document.Description ?? string.Empty;
        if (Describe(name, description) is { } refusal)
        {
            return Invalid<DashboardDetail>(refusal);
        }

        return await CreateCheckedAsync(user, name, description, check.Definition!, null, "dashboard.import", context, ct).ConfigureAwait(false);
    }

    /// <summary>A dashboard's structure as a file: no results, owner, grants or preferences.</summary>
    public async Task<DashboardResult<DashboardConfigDocument>> ExportConfigAsync(
        AuthenticatedUser user, long id, AuthenticationContext context, CancellationToken ct)
    {
        var (record, _) = await OpenAsync(user, id, ct).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound<DashboardConfigDocument>();
        }

        using var parsed = JsonDocument.Parse(record.DefinitionJson);
        var result = new DashboardConfigDocument(
            ConfigFormat, ConfigFormatVersion, _clock.GetUtcNow(), record.Name, record.Description, parsed.RootElement.Clone());

        await AuditAsync(user, context, "dashboard.export.config", record, new() { ["widgets"] = record.WidgetCount }, ct).ConfigureAwait(false);
        return Done<DashboardConfigDocument>(result);
    }

    // ------------------------------------------------------------- preferences

    /// <summary>Marks or unmarks a favourite on a dashboard the caller can open.</summary>
    public async Task<DashboardResult<bool>> SetFavoriteAsync(AuthenticatedUser user, long id, bool favorite, CancellationToken ct)
    {
        var (record, _) = await OpenAsync(user, id, ct).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound<bool>();
        }

        await _store.SetFavoriteAsync(user.UserId, id, favorite, ct).ConfigureAwait(false);
        return Done<bool>(true);
    }

    /// <summary>Sets what <c>/</c> opens: a dashboard the caller can open, a template, or neither.</summary>
    public async Task<DashboardResult<bool>> SetDefaultAsync(AuthenticatedUser user, SetDefaultDashboardRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        if (request.DashboardId is not null && request.TemplateKey is not null)
        {
            return Invalid<bool>(new DefinitionError("$", "A default is a dashboard or a template, not both."));
        }

        if (request.TemplateKey is { } key && !TemplateKey.IsMatch(key))
        {
            return Invalid<bool>(new DefinitionError("templateKey", "Not a template key."));
        }

        if (request.DashboardId is { } id && (await OpenAsync(user, id, ct).ConfigureAwait(false)).Record is null)
        {
            return NotFound<bool>();
        }

        await _store.SetDefaultAsync(user.UserId, request.DashboardId, request.TemplateKey, ct).ConfigureAwait(false);
        return Done<bool>(true);
    }

    // ------------------------------------------------------------------ grants

    /// <summary>A dashboard's grants, for whoever may share it.</summary>
    public async Task<DashboardResult<DashboardGrantsResponse>> ListGrantsAsync(AuthenticatedUser user, long id, CancellationToken ct)
    {
        var (record, refusal) = await ForSharingAsync<DashboardGrantsResponse>(user, id, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        var grants = await _store.ListGrantsAsync(record!.Id, ct).ConfigureAwait(false);
        return Done<DashboardGrantsResponse>(new DashboardGrantsResponse([.. grants.Select(Grant)], _options.MaxGrants));
    }

    /// <summary>Shares a dashboard with a user or a role.</summary>
    public async Task<DashboardResult<DashboardGrantInfo>> AddGrantAsync(
        AuthenticatedUser user, long id, CreateDashboardGrantRequest request, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (record, refusal) = await ForSharingAsync<DashboardGrantInfo>(user, id, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (request.SubjectType is not ("user" or "role"))
        {
            return Invalid<DashboardGrantInfo>(new DefinitionError("subjectType", "user or role."));
        }

        if (!GrantLevels.IsValid(request.Level))
        {
            return Invalid<DashboardGrantInfo>(new DefinitionError("level", "viewer or editor."));
        }

        var (outcome, grant) = await _store.AddGrantAsync(
            record!.Id, request.SubjectType, request.SubjectId, request.Level, user.UserId, _options.MaxGrants, ct).ConfigureAwait(false);

        switch (outcome)
        {
            case GrantWriteOutcome.NotFound:
                return NotFound<DashboardGrantInfo>();
            case GrantWriteOutcome.Duplicate:
                return GrantRefused<DashboardGrantInfo>("This dashboard is already shared with them; change the existing grant instead.");
            case GrantWriteOutcome.SubjectNotFound:
                return GrantRefused<DashboardGrantInfo>(request.SubjectType == "user" ? "No active user with that id." : "No role with that id.");
            case GrantWriteOutcome.SubjectIsOwner:
                return GrantRefused<DashboardGrantInfo>("The owner already has every right to their dashboard.");
            case GrantWriteOutcome.LimitReached:
                return GrantRefused<DashboardGrantInfo>($"A dashboard can be shared at most {_options.MaxGrants} times. Share with a role instead.");
        }

        await AuditAsync(user, context, "dashboard.share.grant", record, GrantDetail(grant!), ct).ConfigureAwait(false);
        return Done<DashboardGrantInfo>(Grant(grant!));
    }

    /// <summary>Changes a grant's level.</summary>
    public async Task<DashboardResult<DashboardGrantInfo>> UpdateGrantAsync(
        AuthenticatedUser user, long id, long grantId, UpdateDashboardGrantRequest request, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (record, refusal) = await ForSharingAsync<DashboardGrantInfo>(user, id, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!GrantLevels.IsValid(request.Level))
        {
            return Invalid<DashboardGrantInfo>(new DefinitionError("level", "viewer or editor."));
        }

        var (outcome, grant) = await _store.UpdateGrantAsync(record!.Id, grantId, request.Level, ct).ConfigureAwait(false);
        if (outcome != GrantWriteOutcome.Saved)
        {
            return NotFound<DashboardGrantInfo>();
        }

        await AuditAsync(user, context, "dashboard.share.update", record, GrantDetail(grant!), ct).ConfigureAwait(false);
        return Done<DashboardGrantInfo>(Grant(grant!));
    }

    /// <summary>Revokes a grant. Takes effect on the grantee's next request.</summary>
    public async Task<DashboardResult<bool>> RemoveGrantAsync(
        AuthenticatedUser user, long id, long grantId, AuthenticationContext context, CancellationToken ct)
    {
        var (record, refusal) = await ForSharingAsync<bool>(user, id, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        var removed = await _store.RemoveGrantAsync(record!.Id, grantId, ct).ConfigureAwait(false);
        if (removed is null)
        {
            return NotFound<bool>();
        }

        await AuditAsync(user, context, "dashboard.share.revoke", record, GrantDetail(removed), ct).ConfigureAwait(false);
        return Done<bool>(true);
    }

    /// <summary>
    /// Users and roles to share with, for whoever may share: names and usernames only. Not
    /// user.view: a share picker must not become a way to list accounts with their details.
    /// </summary>
    public async Task<DashboardResult<ShareDirectoryResponse>> SearchDirectoryAsync(AuthenticatedUser user, string? term, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!user.Can(Permissions.DashboardShare) && !user.Can(Permissions.DashboardManage))
        {
            return Forbidden<ShareDirectoryResponse>("Sharing dashboards needs dashboard.share.");
        }

        var trimmed = (term ?? string.Empty).Trim();
        if (trimmed.Length > 100)
        {
            return Invalid<ShareDirectoryResponse>(new DefinitionError("q", "At most 100 characters."));
        }

        var (users, roles) = await _store.SearchShareDirectoryAsync(
            trimmed, _options.ShareDirectoryLimit, _options.ShareDirectoryMinQuery, ct).ConfigureAwait(false);

        return Done<ShareDirectoryResponse>(new ShareDirectoryResponse(
            [.. users.Where(u => u.Id != user.UserId).Select(Subject)],
            [.. roles.Select(Subject)]));
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// The dashboard and the caller's standing towards it, or (null, None) when they cannot open it.
    /// Exports and other callers outside this class use it to apply the same rule.
    /// </summary>
    public async Task<(DashboardRecord? Record, DashboardAccessLevel Level)> OpenAsync(AuthenticatedUser user, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        var record = await _store.GetAsync(id, user.UserId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return (null, DashboardAccessLevel.None);
        }

        var level = DashboardAccess.Resolve(record.OwnerUserId, record.GrantLevel, user);
        return level == DashboardAccessLevel.None ? (null, DashboardAccessLevel.None) : (record, level);
    }

    private async Task<(DashboardRecord? Record, DashboardResult<T>? Refusal)> ForSharingAsync<T>(AuthenticatedUser user, long id, CancellationToken ct)
    {
        var (record, level) = await OpenAsync(user, id, ct).ConfigureAwait(false);
        if (record is null)
        {
            return (null, NotFound<T>());
        }

        return DashboardAccess.CanShare(level, user)
            ? (record, null)
            : (null, Forbidden<T>("Only the owner, with dashboard.share, can share this dashboard."));
    }

    private async Task<DashboardResult<DashboardDetail>> CreateCheckedAsync(
        AuthenticatedUser user, string name, string description, ValidatedDefinition definition, string? sourceTemplate,
        string action, AuthenticationContext context, CancellationToken ct, Dictionary<string, object?>? extra = null)
    {
        var (outcome, created) = await _store.CreateAsync(
            new NewDashboard(user.UserId, name.Trim(), description, definition.NormalizedJson, definition.SchemaVersion, sourceTemplate), ct).ConfigureAwait(false);

        if (outcome == DashboardWriteOutcome.NameTaken)
        {
            return NameTaken<DashboardDetail>(name);
        }

        var detail = new Dictionary<string, object?> { ["widgets"] = definition.Widgets.Count };
        if (sourceTemplate is not null)
        {
            detail["template"] = sourceTemplate;
        }

        foreach (var (key, value) in extra ?? [])
        {
            detail[key] = value;
        }

        await AuditAsync(user, context, action, created!, detail, ct).ConfigureAwait(false);
        return Done<DashboardDetail>(Detail(created!, DashboardAccessLevel.Owner, user));
    }

    /// <summary>"Copy of X", or "Copy of X (2)" and so on when taken.</summary>
    private async Task<string> FreeNameAsync(long owner, string wanted, CancellationToken ct)
    {
        var name = Truncate(wanted.Trim(), _options.MaxNameLength);
        for (var n = 2; n < 100 && await _store.NameExistsAsync(owner, name, ct).ConfigureAwait(false); n++)
        {
            var suffix = string.Create(CultureInfo.InvariantCulture, $" ({n})");
            name = Truncate(wanted.Trim(), _options.MaxNameLength - suffix.Length) + suffix;
        }

        return name;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length].TrimEnd();

    private DefinitionError? Describe(string? name, string? description)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > _options.MaxNameLength)
        {
            return new DefinitionError("name", $"A name of 1 to {_options.MaxNameLength} characters.");
        }

        return (description?.Length ?? 0) > _options.MaxDescriptionLength
            ? new DefinitionError("description", $"At most {_options.MaxDescriptionLength} characters.")
            : null;
    }

    /// <summary>Every widget gets a new id: an imported file must never share ids with what it came from.</summary>
    private static JsonElement WithNewIds(JsonElement definition)
    {
        var node = JsonNode.Parse(definition.GetRawText())!;
        if (node["widgets"] is JsonArray widgets)
        {
            foreach (var widget in widgets.OfType<JsonObject>())
            {
                widget["id"] = NewWidgetId();
            }
        }

        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>w_ and ten random base-36 characters, the same shape the client makes.</summary>
    public static string NewWidgetId()
    {
        const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<char> id = stackalloc char[10];
        for (var i = 0; i < id.Length; i++)
        {
            id[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return $"w_{id}";
    }

    private async Task<DashboardDefaultInfo> DefaultAsync(AuthenticatedUser user, CancellationToken ct)
    {
        var stored = await _store.GetDefaultAsync(user.UserId, ct).ConfigureAwait(false);

        // A default the person can no longer open is ignored, not deleted: access is decided when
        // read, and the grant may come back.
        if (stored.DashboardId is { } id && (await OpenAsync(user, id, ct).ConfigureAwait(false)).Record is null)
        {
            return new DashboardDefaultInfo(null, null);
        }

        return new DashboardDefaultInfo(stored.DashboardId, stored.TemplateKey);
    }

    private static DashboardSummary Summary(DashboardRecord r, AuthenticatedUser user)
    {
        var level = DashboardAccess.Resolve(r.OwnerUserId, r.GrantLevel, user);
        return new DashboardSummary(
            r.Id, r.Name, r.Description, new DashboardOwnerInfo(r.OwnerUserId, r.OwnerDisplayName),
            DashboardAccess.Describe(level, user), r.GrantCount > 0, r.IsFavorite, r.IsDefault, r.Revision, r.WidgetCount, r.UpdatedAt);
    }

    private static DashboardDetail Detail(DashboardRecord r, DashboardAccessLevel level, AuthenticatedUser user)
    {
        using var document = JsonDocument.Parse(r.DefinitionJson);
        return new DashboardDetail(
            r.Id, r.Name, r.Description, new DashboardOwnerInfo(r.OwnerUserId, r.OwnerDisplayName), document.RootElement.Clone(),
            r.SchemaVersion, r.Revision, r.SourceTemplate, r.CreatedAt, r.UpdatedAt, r.UpdatedByDisplayName,
            DashboardAccess.Describe(level, user), r.IsFavorite, r.IsDefault, r.GrantCount);
    }

    private static DashboardGrantInfo Grant(DashboardGrantRecord g) =>
        new(g.Id, g.SubjectType, g.SubjectId, g.SubjectName, g.SubjectDetail, g.Level, g.GrantedAt, g.GrantedByDisplayName);

    private static ShareSubject Subject(ShareDirectoryEntry e) => new(e.Type, e.Id, e.Name, e.Detail);

    private static Dictionary<string, object?> GrantDetail(DashboardGrantRecord g) => new()
    {
        ["subjectType"] = g.SubjectType,
        ["subjectId"] = g.SubjectId,
        ["subjectName"] = g.SubjectName,
        ["level"] = g.Level,
    };

    /// <summary>Audited by id and name, never by content.</summary>
    private Task AuditAsync(
        AuthenticatedUser user, AuthenticationContext context, string action, DashboardRecord dashboard,
        Dictionary<string, object?> detail, CancellationToken ct) =>
        _audit.WriteAsync(new AuditEntry(
            user.Username, action, AuditCategory.Data, AuditOutcome.Success, user.UserId,
            "dashboard", dashboard.Id.ToString(CultureInfo.InvariantCulture), dashboard.Name,
            context.Ip, context.UserAgent, context.CorrelationId, detail), ct);

    private static DashboardResult<T> Done<T>(T value) => new(DashboardOutcome.Ok, value);

    private static DashboardResult<T> NotFound<T>() => new(DashboardOutcome.NotFound);

    private static DashboardResult<T> Forbidden<T>(string message) => new(DashboardOutcome.Forbidden, Message: message);

    private static DashboardResult<T> Invalid<T>(DefinitionError error) => new(DashboardOutcome.Invalid, Errors: [error]);

    private static DashboardResult<T> NameTaken<T>(string name) =>
        new(DashboardOutcome.NameTaken, Message: $"You already have a dashboard called \"{name.Trim()}\".");

    private static DashboardResult<T> GrantRefused<T>(string message) => new(DashboardOutcome.GrantRefused, Message: message);
}
