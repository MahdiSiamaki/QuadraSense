using System.Globalization;
using Sqm.Application.Identity;
using Sqm.Contracts.Dashboards;

namespace Sqm.Application.Dashboards;

/// <summary>One widget's part of a CSV export.</summary>
/// <param name="Id">The instance id.</param>
/// <param name="Title">Its title, or its type's name.</param>
/// <param name="Type">Its type.</param>
/// <param name="Status">ok, or why it is not in the file.</param>
/// <param name="Reason">Why not, written for a person.</param>
/// <param name="Table">Its rows, when ok.</param>
/// <param name="Notes">Caveats the file must carry.</param>
public sealed record WidgetCsvPart(
    string Id, string Title, string Type, string Status, string? Reason, WidgetTable? Table, IReadOnlyList<string> Notes);

/// <summary>What a CSV export holds, before it is written as one file or a ZIP.</summary>
/// <param name="Name">The dashboard's title.</param>
/// <param name="PreparedAt">When the server prepared it.</param>
/// <param name="Lens">The lens it was prepared under.</param>
/// <param name="Parts">One per widget asked for, in definition order.</param>
public sealed record DashboardCsvExport(string Name, DateTimeOffset PreparedAt, DashboardLens Lens, IReadOnlyList<WidgetCsvPart> Parts);

/// <summary>
/// Dashboard exports - an image, a printout or CSV - prepared on the server from the same data the
/// cards draw, for the person exporting, and audited (ADR-016 §11).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two checks, as everywhere on a dashboard.</b> Opening a saved dashboard decides nothing about the
/// data: each widget is loaded only when the exporter holds its permissions, and otherwise appears as
/// "forbidden" with nothing in it. An export never shows more than the same person sees live.
/// </para>
/// <para>
/// <b>The definition is checked like any other</b>, so a template or an unsaved draft can be exported
/// too; a saved dashboard's id is used for the access check and the audit. A dashboard the caller
/// cannot open answers as one that does not exist.
/// </para>
/// <para>
/// <b>Audited by id, name and counts, never by content</b> - not the lens, not a figure, not a row.
/// </para>
/// </remarks>
public sealed class DashboardExportService
{
    private readonly IWidgetContract _contract;
    private readonly IDashboardDefinitionValidator _validator;
    private readonly DashboardService _dashboards;
    private readonly IWidgetDataLoader _loader;
    private readonly IAuditLog _audit;
    private readonly DashboardOptions _options;
    private readonly TimeProvider _clock;
    private readonly Action<string, Exception> _reportFailure;

    /// <summary>Creates the service.</summary>
    /// <remarks>A widget that fails to load becomes "error" in the export; <c>reportFailure</c> logs why.</remarks>
    public DashboardExportService(
        IWidgetContract contract, IDashboardDefinitionValidator validator, DashboardService dashboards, IWidgetDataLoader loader,
        IAuditLog audit, DashboardOptions options, TimeProvider clock, Action<string, Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(dashboards);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(reportFailure);

        _contract = contract;
        _validator = validator;
        _dashboards = dashboards;
        _loader = loader;
        _audit = audit;
        _options = options;
        _clock = clock;
        _reportFailure = reportFailure;
    }

    /// <summary>A dashboard's data for an image or a printout.</summary>
    public async Task<DashboardResult<DashboardSnapshotResponse>> SnapshotAsync(
        AuthenticatedUser user, DashboardSnapshotRequest request, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (request.Purpose is not ("png" or "print"))
        {
            return Invalid<DashboardSnapshotResponse>("purpose", "png or print.");
        }

        var prepared = await PrepareAsync(user, request.DashboardId, request.Definition, request.Lens, request.WidgetIds, ct).ConfigureAwait(false);
        if (prepared.Refusal is { } refusal)
        {
            return new DashboardResult<DashboardSnapshotResponse>(refusal.Outcome, Errors: refusal.Errors, Message: refusal.Message);
        }

        var (name, lens, loaded) = prepared.Value!.Value;
        var widgets = loaded.Select(w => new WidgetSnapshot(
            w.Widget.Id, w.Widget.Type, w.Result.Status, w.Result.Reason, w.Result.Data, w.Result.AsOf, w.Result.Notes, w.Effective)).ToList();

        await AuditAsync(user, context, request.DashboardId, name, request.Purpose, loaded, null, ct).ConfigureAwait(false);
        return new(DashboardOutcome.Ok, new DashboardSnapshotResponse(
            request.DashboardId, name ?? request.Name ?? "Dashboard", _clock.GetUtcNow(), lens, widgets));
    }

    /// <summary>Widgets' rows for CSV: what the cards show, each with its scope.</summary>
    public async Task<DashboardResult<DashboardCsvExport>> CsvAsync(
        AuthenticatedUser user, DashboardCsvRequest request, AuthenticationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (request.WidgetIds is not { Count: > 0 })
        {
            return Invalid<DashboardCsvExport>("widgetIds", "Choose at least one widget.");
        }

        var prepared = await PrepareAsync(user, request.DashboardId, request.Definition, request.Lens, request.WidgetIds, ct).ConfigureAwait(false);
        if (prepared.Refusal is { } refusal)
        {
            return new DashboardResult<DashboardCsvExport>(refusal.Outcome, Errors: refusal.Errors, Message: refusal.Message);
        }

        var (name, lens, loaded) = prepared.Value!.Value;

        // One widget asked for and refused: say so, rather than send a file with nothing in it.
        if (loaded.Count == 1 && loaded[0].Result.Status != "ok")
        {
            var only = loaded[0].Result;
            return only.Status == "forbidden"
                ? new(DashboardOutcome.Forbidden, Message: only.Reason)
                : Invalid<DashboardCsvExport>("widgetIds", only.Reason ?? "This widget has no rows to export.");
        }

        var parts = loaded.Select(w => new WidgetCsvPart(
            w.Widget.Id, w.Widget.Title ?? w.Widget.Type, w.Widget.Type, w.Result.Status, w.Result.Reason,
            w.Result.Status == "ok" ? w.Result.Table : null, w.Result.Notes)).ToList();

        var scopes = string.Join(",", parts.Where(p => p.Table is not null).Select(p => p.Table!.Scope).Distinct().Order(StringComparer.Ordinal));
        await AuditAsync(user, context, request.DashboardId, name, "csv", loaded, scopes, ct).ConfigureAwait(false);
        return new(DashboardOutcome.Ok, new DashboardCsvExport(name ?? request.Name ?? "Dashboard", _clock.GetUtcNow(), lens, parts));
    }

    private sealed record Loaded(ValidatedWidget Widget, DashboardLens Effective, WidgetDataResult Result);

    private sealed record Refusal(DashboardOutcome Outcome, IReadOnlyList<DefinitionError>? Errors, string? Message);

    private sealed record Prepared((string? Name, DashboardLens Lens, IReadOnlyList<Loaded> Widgets)? Value, Refusal? Refusal);

    private async Task<Prepared> PrepareAsync(
        AuthenticatedUser user, long? dashboardId, System.Text.Json.JsonElement definition, DashboardLens? url,
        IReadOnlyList<string>? widgetIds, CancellationToken ct)
    {
        string? name = null;
        if (dashboardId is { } id)
        {
            var (record, _) = await _dashboards.OpenAsync(user, id, ct).ConfigureAwait(false);
            if (record is null)
            {
                return new(null, new Refusal(DashboardOutcome.NotFound, null, null));
            }

            name = record.Name;
        }

        var check = _validator.Check(definition);
        if (!check.IsValid)
        {
            return new(null, new Refusal(DashboardOutcome.Invalid, check.Errors, "The dashboard's definition was refused."));
        }

        var all = check.Definition!.Widgets;
        IReadOnlyList<ValidatedWidget> chosen = all;
        if (widgetIds is not null)
        {
            var unknown = widgetIds.Where(w => all.All(x => x.Id != w)).ToList();
            if (unknown.Count > 0)
            {
                return new(null, new Refusal(DashboardOutcome.Invalid, [new DefinitionError("widgetIds", "A widget asked for is not on this dashboard.")], null));
            }

            var wanted = widgetIds.ToHashSet(StringComparer.Ordinal);
            chosen = [.. all.Where(w => wanted.Contains(w.Id))];
        }

        if (chosen.Count > _options.MaxExportWidgets)
        {
            return new(null, new Refusal(DashboardOutcome.Invalid,
                [new DefinitionError("widgetIds", $"At most {_options.MaxExportWidgets} widgets in one export.")], null));
        }

        var lens = DashboardLensResolver.Sanitize(url, _contract.Lens);
        var through = await _loader.DataThroughAsync(ct).ConfigureAwait(false);
        var load = new WidgetLoadContext(user, through, DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime));

        var loaded = new List<Loaded>(chosen.Count);
        foreach (var widget in chosen)
        {
            var spec = _contract.Types[widget.Type];
            var effective = DashboardLensResolver.Effective(spec, check.Definition.Defaults, lens, widget.Overrides);
            var missing = WidgetPermissions.Missing(spec, widget.Config, user);
            if (missing.Count > 0)
            {
                loaded.Add(new Loaded(widget, effective, new WidgetDataResult(
                    "forbidden", $"Needs the {string.Join(", ", missing)} permission{(missing.Count == 1 ? "" : "s")}, which you do not have.",
                    null, null, [], null)));
                continue;
            }

            WidgetDataResult result;
            try
            {
                result = await _loader.LoadAsync(widget, spec, effective, load, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _reportFailure($"Dashboard export: widget {widget.Id} ({widget.Type}) failed to load.", ex);
                result = new WidgetDataResult("error", "The server could not prepare this widget.", null, null, [], null);
            }

            loaded.Add(new Loaded(widget, effective, result));
        }

        return new((name, lens, loaded), null);
    }

    private Task AuditAsync(
        AuthenticatedUser user, AuthenticationContext context, long? dashboardId, string? name, string format,
        IReadOnlyList<Loaded> loaded, string? scopes, CancellationToken ct)
    {
        var detail = new Dictionary<string, object?>
        {
            ["format"] = format,
            ["widgets"] = loaded.Count,
            ["included"] = loaded.Count(w => w.Result.Status == "ok"),
            ["forbidden"] = loaded.Count(w => w.Result.Status == "forbidden"),
            ["source"] = dashboardId is null ? "template or unsaved" : "saved",
        };
        if (scopes is not null)
        {
            detail["scope"] = scopes;
        }

        return _audit.WriteAsync(new AuditEntry(
            user.Username, "data.export", AuditCategory.Data, AuditOutcome.Success, user.UserId,
            "dashboard", dashboardId?.ToString(CultureInfo.InvariantCulture), name,
            context.Ip, context.UserAgent, context.CorrelationId, detail), ct);
    }

    private static DashboardResult<T> Invalid<T>(string path, string message) =>
        new(DashboardOutcome.Invalid, Errors: [new DefinitionError(path, message)]);
}
