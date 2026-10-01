using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Sqm.Api.Auth;
using Sqm.Application.Abstractions;
using Sqm.Application.Explorer;
using Sqm.Application.Identity;
using Sqm.Contracts.Explorer;
using Sqm.Domain.Identifiers;

namespace Sqm.Api.Endpoints;

/// <summary>The identifier to summarise.</summary>
/// <param name="Identifier">A number, SIM, handset or TAC. Length decides which.</param>
public sealed record ExplorerEntityRequest(string Identifier);

/// <summary>What current state holds for one number, SIM, handset or TAC.</summary>
/// <param name="Kind">Msisdn, Imsi, Imei or Tac.</param>
/// <param name="Identifier">As typed, normalised.</param>
/// <param name="Found">Whether any binding holds it.</param>
/// <param name="Bindings">Number + SIM + handset combinations ever seen with it.</param>
/// <param name="ActiveBindings">Of those, not yet removed by the feed.</param>
/// <param name="Numbers">Different phone numbers.</param>
/// <param name="ActiveNumbers">Of those, on an active binding.</param>
/// <param name="Sims">Different SIMs.</param>
/// <param name="ActiveSims">Of those, on an active binding.</param>
/// <param name="Handsets">Different 14-digit IMEIs - not physical devices: a dual-SIM phone has two.</param>
/// <param name="ActiveHandsets">Of those, on an active binding.</param>
/// <param name="LastChange">The latest day the feed changed any of its bindings.</param>
/// <param name="Tac">For a handset or TAC: the model allocation.</param>
/// <param name="Brand">For a handset or TAC: the GSMA brand.</param>
/// <param name="Model">For a handset or TAC: the GSMA marketing name.</param>
/// <param name="Plan">What the summary cost.</param>
public sealed record ExplorerEntitySummary(
    string Kind, string Identifier, bool Found,
    long Bindings, long ActiveBindings, long Numbers, long ActiveNumbers,
    long Sims, long ActiveSims, long Handsets, long ActiveHandsets,
    string? LastChange, string? Tac, string? Brand, string? Model, ExplorerPlanInfo Plan);

/// <summary>The Explorer: a query builder over current state and the event log, and entity summaries.</summary>
/// <remarks>
/// <para>
/// <b>Two layers of permission.</b> <c>explorer.query</c>, on the routes, admits the query builder
/// at all - composing queries over the whole population is bulk by design (product owner,
/// 2026-09-30). Then the identifier rules, checked per query in the handler: filtering on,
/// showing or grouping by numbers, SIMs or handsets needs <c>lookup.subscriber</c>,
/// <c>lookup.imsi</c> or <c>lookup.imei</c>, exactly as everywhere else. A query that asks for
/// what the caller may not see is refused whole, and the refusal names what is missing; quietly
/// dropping a column would return a different answer to the question asked.
/// </para>
/// <para>
/// <b>Every query and every refusal is audited</b> - with the dataset, the fields used and what it
/// cost, never with the values, so the audit log does not become a copy of what it polices. The
/// in-handler checks are outside the pipeline's automatic audit, so they write their own entries.
/// </para>
/// <para>
/// POST throughout: values in a query can be identifiers, and a query string reaches access logs.
/// </para>
/// </remarks>
public static class ExplorerEndpoints
{
    /// <summary>Registers the Explorer routes.</summary>
    public static IEndpointRouteBuilder MapExplorerEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var builder = app.MapGroup("/api/v1/explorer").WithTags("Explorer")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.ExplorerQuery);

        builder.MapGet("/catalogue", Catalogue)
            .WithName("GetExplorerCatalogue")
            .WithSummary("The datasets and fields a query can use, and the limits it runs within.");

        builder.MapPost("/plan", PlanAsync)
            .WithName("PlanExplorerQuery")
            .WithSummary("What a query would cost, from the server's own plan, without running it.");

        builder.MapPost("/query", QueryAsync)
            .WithName("RunExplorerQuery")
            .WithSummary("Runs a query within the budget and returns one page.");

        // Both permissions: explorer.query from the group, data.export for taking the rows away.
        builder.MapPost("/export", ExportAsync)
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DataExport)
            .WithName("ExportExplorerQuery")
            .WithSummary("The query's rows as CSV, up to the rows paging can reach.");

        builder.MapGet("/saved", ListSavedAsync)
            .WithName("ListSavedExplorerQueries")
            .WithSummary("My Queries: the caller's own saved queries.");

        builder.MapPost("/saved", CreateSavedAsync)
            .WithName("SaveExplorerQuery")
            .WithSummary("Saves a query under a name, private to the caller.");

        builder.MapPut("/saved/{id:long}", UpdateSavedAsync)
            .WithName("UpdateSavedExplorerQuery")
            .WithSummary("Renames or changes one of the caller's saved queries.");

        builder.MapDelete("/saved/{id:long}", DeleteSavedAsync)
            .WithName("DeleteSavedExplorerQuery")
            .WithSummary("Deletes one of the caller's saved queries.");

        // A summary of one identifier is a lookup, not a query over the population, so it follows
        // the lookup permission of its kind rather than explorer.query.
        app.MapPost("/api/v1/explorer/entity", EntityAsync)
            .RequireAuthorization()
            .WithTags("Explorer")
            .WithName("SummariseExplorerEntity")
            .WithSummary("What current state holds for one number, SIM, handset or TAC.");

        return app;
    }

    private static async Task<IResult> Catalogue(IOptions<ExplorerOptions> options, IExplorerEngine engine, CancellationToken ct)
    {
        // What every answer is as of, shown beside the results: decisions here are made on daily
        // data, and a reader must know which day's.
        var through = await engine.DataThroughAsync(ct).ConfigureAwait(false);

        return Results.Ok(new ExplorerCatalogueResponse(
            [.. ExplorerCatalogue.All.Select(d => new ExplorerDatasetInfo(
                d.Dataset.ToString(), d.Label, d.Description,
                [.. d.Fields.Values.Select(f => new ExplorerFieldInfo(
                    f.Name, f.Label, f.Type.ToString(), [.. f.Operators.Select(o => o.ToString()).Order(StringComparer.Ordinal)],
                    f.Groupable, f.Permission, f.Description, f.Labels))],
                d.DefaultColumns))],
            options.Value.MaxPageSize, options.Value.MaxReachableRows, options.Value.BudgetRows,
            through?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The rows as CSV: every row paging can reach, in one run, masked and audited as the page would be.
    /// </summary>
    private static async Task<IResult> ExportAsync(
        ExplorerQueryRequest request, HttpContext http, IExplorerEngine engine, IAuditLog audit,
        IOptions<ExplorerOptions> options, TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // One run of up to the reachable rows, rather than twenty pages of five hundred: the same
        // budget applies either way, and one run cannot see the data change between pages.
        var limits = options.Value;
        var exportLimits = new ExplorerOptions
        {
            MaxPageSize = limits.MaxReachableRows, MaxReachableRows = limits.MaxReachableRows,
            MaxConditions = limits.MaxConditions, MaxDepth = limits.MaxDepth, MaxInValues = limits.MaxInValues,
        };

        var (query, refusal) = await CheckAsync(
            request with { Page = 1, PageSize = limits.MaxReachableRows }, http, audit, exportLimits, ct).ConfigureAwait(false);
        if (query is null)
        {
            return refusal!;
        }

        var user = CurrentUser.Require(http);
        if (!UserSlots.TryEnter(user.UserId))
        {
            return Busy("You already have two Explorer queries running; wait for one to finish.");
        }

        try
        {
            var rows = await engine.RunAsync(query, ct).ConfigureAwait(false);
            var reveal = user.Can(Permissions.IdentifierReveal);

            var csv = Infrastructure.CsvExport.Write(
                [.. query.Columns.Select(c => c.Label)],
                reveal ? rows.Rows : rows.Rows.Select(r => Mask(r, query.Columns)));

            // The export is the act the security model says is always audited: who, what shape,
            // how many rows - and, as everywhere, not the values.
            var entry = Entry(user, http, AuditOutcome.Success, query, new()
            {
                ["rows"] = rows.Rows.Count,
                ["total"] = rows.Total,
                ["masked"] = !reveal,
                ["rowsRead"] = rows.RowsRead,
            }) with { Action = Permissions.DataExport };
            await audit.WriteAsync(entry, ct).ConfigureAwait(false);

            var stamp = clock.GetUtcNow().ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
            return Results.File(csv, "text/csv; charset=utf-8",
                $"explorer-{query.Definition.Dataset.ToString().ToLowerInvariant()}-{stamp}.csv");
        }
        catch (ExplorerRefusedException ex)
        {
            await audit.WriteAsync(Entry(user, http, AuditOutcome.Failure, query, new() { ["refused"] = ex.Message })
                with { Action = Permissions.DataExport }, ct).ConfigureAwait(false);

            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Over the query budget",
                detail: ex.Message, extensions: new Dictionary<string, object?> { ["plan"] = ex.Plan });
        }
        catch (ExplorerBusyException ex)
        {
            return Busy(ex.Message);
        }
        finally
        {
            UserSlots.Exit(user.UserId);
        }
    }

    // ------------------------------------------------------------------ My Queries

    private static async Task<IResult> ListSavedAsync(HttpContext http, IExplorerSavedQueryStore store, CancellationToken ct)
    {
        var user = CurrentUser.Require(http);
        var saved = await store.ListAsync(user.UserId, ct).ConfigureAwait(false);
        return Results.Ok(saved.Select(Info).ToList());
    }

    private static async Task<IResult> CreateSavedAsync(
        SaveExplorerQueryRequest request, HttpContext http, IExplorerSavedQueryStore store, IAuditLog audit,
        IOptions<ExplorerOptions> options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await CheckSaveAsync(request, http, audit, options.Value, ct).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        var user = CurrentUser.Require(http);
        var (outcome, saved) = await store.CreateAsync(
            user.UserId, request.Name, request.Description ?? string.Empty, request.Query, ct).ConfigureAwait(false);

        if (outcome == SavedQueryOutcome.NameTaken)
        {
            return NameTaken(request.Name);
        }

        await audit.WriteAsync(SavedEntry(user, http, "save", saved!), ct).ConfigureAwait(false);
        return Results.Created($"/api/v1/explorer/saved/{saved!.Id}", Info(saved));
    }

    private static async Task<IResult> UpdateSavedAsync(
        long id, SaveExplorerQueryRequest request, HttpContext http, IExplorerSavedQueryStore store, IAuditLog audit,
        IOptions<ExplorerOptions> options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await CheckSaveAsync(request, http, audit, options.Value, ct).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        var user = CurrentUser.Require(http);
        var (outcome, saved) = await store.UpdateAsync(
            user.UserId, id, request.Name, request.Description ?? string.Empty, request.Query, ct).ConfigureAwait(false);

        return outcome switch
        {
            // Another person's query is reported as not found, not forbidden: whether it exists is
            // itself something only its owner is told.
            SavedQueryOutcome.NotFound => Results.NotFound(),
            SavedQueryOutcome.NameTaken => NameTaken(request.Name),
            _ => await AuditedOk(saved!).ConfigureAwait(false),
        };

        async Task<IResult> AuditedOk(SavedExplorerQuery query)
        {
            await audit.WriteAsync(SavedEntry(user, http, "update", query), ct).ConfigureAwait(false);
            return Results.Ok(Info(query));
        }
    }

    private static async Task<IResult> DeleteSavedAsync(
        long id, HttpContext http, IExplorerSavedQueryStore store, IAuditLog audit, CancellationToken ct)
    {
        var user = CurrentUser.Require(http);
        var existing = await store.GetAsync(user.UserId, id, ct).ConfigureAwait(false);

        if (existing is null || !await store.DeleteAsync(user.UserId, id, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        await audit.WriteAsync(SavedEntry(user, http, "delete", existing), ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>
    /// A query is saved only if it could run for the person saving it: valid, and within what they
    /// may see. It is checked again every time it runs, because both can change.
    /// </summary>
    private static async Task<IResult?> CheckSaveAsync(
        SaveExplorerQueryRequest request, HttpContext http, IAuditLog audit, ExplorerOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100
            || (request.Description?.Length ?? 0) > 1000 || request.Query is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = ["A name of 1 to 100 characters, notes up to 1,000, and a query."],
            });
        }

        var (_, refusal) = await CheckAsync(request.Query, http, audit, options, ct).ConfigureAwait(false);
        return refusal;
    }

    private static SavedExplorerQueryInfo Info(SavedExplorerQuery q) =>
        new(q.Id, q.Name, q.Description, q.Query, q.CreatedAt, q.UpdatedAt);

    private static IResult NameTaken(string name) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict, title: "Name already used",
        detail: $"You already have a saved query called \"{name.Trim()}\".");

    /// <summary>Saved-query changes, audited by id and name - never the query, which can hold identifiers.</summary>
    private static AuditEntry SavedEntry(AuthenticatedUser user, HttpContext http, string what, SavedExplorerQuery query) =>
        new(user.Username, $"{Permissions.ExplorerQuery}.{what}", AuditCategory.Data, AuditOutcome.Success, user.UserId,
            "explorer-saved-query", query.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), query.Name,
            http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString(), http.TraceIdentifier,
            new Dictionary<string, object?> { ["dataset"] = query.Query.Dataset.ToString() });

    private static async Task<IResult> PlanAsync(
        ExplorerQueryRequest request, HttpContext http, IExplorerEngine engine, IAuditLog audit,
        IOptions<ExplorerOptions> options, CancellationToken ct)
    {
        var (query, refusal) = await CheckAsync(request, http, audit, options.Value, ct).ConfigureAwait(false);
        return query is null ? refusal! : Results.Ok(await engine.PlanAsync(query, ct).ConfigureAwait(false));
    }

    private static async Task<IResult> QueryAsync(
        ExplorerQueryRequest request, HttpContext http, IExplorerEngine engine, IAuditLog audit,
        IOptions<ExplorerOptions> options, CancellationToken ct)
    {
        var (query, refusal) = await CheckAsync(request, http, audit, options.Value, ct).ConfigureAwait(false);
        if (query is null)
        {
            return refusal!;
        }

        var user = CurrentUser.Require(http);

        // Two at a time per person, on top of the engine's limit across everybody: one person
        // with a slow query and an impatient refresh key should not take every slot.
        if (!UserSlots.TryEnter(user.UserId))
        {
            return Busy("You already have two Explorer queries running; wait for one to finish.");
        }

        try
        {
            var rows = await engine.RunAsync(query, ct).ConfigureAwait(false);
            var reveal = user.Can(Permissions.IdentifierReveal);

            await audit.WriteAsync(Entry(user, http, AuditOutcome.Success, query, new()
            {
                ["verdict"] = rows.Plan.Verdict,
                ["estimatedRows"] = rows.Plan.EstimatedRows,
                ["rowsRead"] = rows.RowsRead,
                ["returned"] = rows.Rows.Count,
                ["total"] = rows.Total,
                ["elapsedMs"] = rows.ElapsedMs,
            }), ct).ConfigureAwait(false);

            return Results.Ok(new ExplorerResultResponse(
                [.. query.Columns.Select(c => new ExplorerColumnInfo(c.Name, c.Label, c.Type.ToString(), !reveal && IsIdentifier(c.Type)))],
                reveal ? rows.Rows : [.. rows.Rows.Select(r => Mask(r, query.Columns))],
                rows.Total,
                Math.Min(rows.Total, options.Value.MaxReachableRows),
                query.Page,
                query.PageSize,
                rows.Plan,
                rows.ElapsedMs,
                rows.RowsRead));
        }
        catch (ExplorerRefusedException ex)
        {
            await audit.WriteAsync(Entry(user, http, AuditOutcome.Failure, query, new()
            {
                ["refused"] = ex.Message,
                ["estimatedRows"] = ex.Plan?.EstimatedRows,
            }), ct).ConfigureAwait(false);

            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Over the query budget",
                detail: ex.Message,
                extensions: new Dictionary<string, object?> { ["plan"] = ex.Plan });
        }
        catch (ExplorerBusyException ex)
        {
            return Busy(ex.Message);
        }
        finally
        {
            UserSlots.Exit(user.UserId);
        }
    }

    private static async Task<IResult> EntityAsync(
        ExplorerEntityRequest request, HttpContext http, IExplorerEngine engine, IDeviceAnalyticsStore devices,
        IAuditLog audit, IOptions<ExplorerOptions> options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = CurrentUser.Require(http);
        var term = DeviceSearchTerm.Classify(request.Identifier);

        var (field, permission) = term?.Kind switch
        {
            DeviceSearchKind.Msisdn => ("msisdn", Permissions.LookupSubscriber),
            DeviceSearchKind.Imsi => ("imsi", Permissions.LookupImsi),
            DeviceSearchKind.Imei => ("imei", Permissions.LookupImei),
            DeviceSearchKind.Tac => ("tac", Permissions.DeviceView),
            _ => (null, null),
        };

        if (field is null || permission is null || term is not { } parsed)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["identifier"] = ["A phone number (10 digits), a SIM (15), a handset (14) or a TAC (8)."],
            });
        }

        if (!user.Can(permission))
        {
            await audit.WriteAsync(new AuditEntry(
                user.Username, permission, AuditCategory.Data, AuditOutcome.Denied, user.UserId, "explorer-entity",
                Ip: http.Connection.RemoteIpAddress?.ToString(), UserAgent: http.Request.Headers.UserAgent.ToString(),
                CorrelationId: http.TraceIdentifier, Detail: new Dictionary<string, object?> { ["kind"] = field }), ct)
                .ConfigureAwait(false);

            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not permitted",
                detail: $"A summary of this {field} needs the \"{permission}\" permission.");
        }

        // The summary is a query like any other - the same engine, the same plan, the same budget.
        var (query, problems) = ExplorerValidator.Check(new ExplorerQueryRequest(
            ExplorerDataset.Bindings,
            Where: new ExplorerFilter(Field: field, Operator: ExplorerOperator.Equals, Values: [parsed.Digits]),
            Measures:
            [
                new("bindings", ExplorerAggregate.Count),
                new("activeBindings", ExplorerAggregate.Count, ActiveOnly: true),
                new("numbers", ExplorerAggregate.CountDistinct, "msisdn"),
                new("activeNumbers", ExplorerAggregate.CountDistinct, "msisdn", ActiveOnly: true),
                new("sims", ExplorerAggregate.CountDistinct, "imsi"),
                new("activeSims", ExplorerAggregate.CountDistinct, "imsi", ActiveOnly: true),
                new("handsets", ExplorerAggregate.CountDistinct, "imei"),
                new("activeHandsets", ExplorerAggregate.CountDistinct, "imei", ActiveOnly: true),
                new("lastChange", ExplorerAggregate.Max, "lastChangeDate"),
            ],
            PageSize: 1), options.Value);

        if (query is null)
        {
            return Results.ValidationProblem(problems.GroupBy(p => p.Path).ToDictionary(g => g.Key, g => g.Select(p => p.Message).ToArray()));
        }

        ExplorerRows rows;
        try
        {
            rows = await engine.RunAsync(query, ct).ConfigureAwait(false);
        }
        catch (ExplorerBusyException ex)
        {
            return Busy(ex.Message);
        }

        var values = rows.Rows.Count > 0 ? rows.Rows[0] : null;
        long Count(int i) => values?[i] is long n ? n : 0;

        var tac = field is "imei" or "tac" ? parsed.Digits[..8] : null;
        var identity = tac is null ? null : await devices.GetModelIdentityAsync(tac, ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry(
            user.Username, permission, AuditCategory.Data, AuditOutcome.Success, user.UserId, "explorer-entity",
            Ip: http.Connection.RemoteIpAddress?.ToString(), UserAgent: http.Request.Headers.UserAgent.ToString(),
            CorrelationId: http.TraceIdentifier,
            Detail: new Dictionary<string, object?> { ["kind"] = field, ["found"] = Count(0) > 0 }), ct).ConfigureAwait(false);

        return Results.Ok(new ExplorerEntitySummary(
            field, parsed.Digits, Count(0) > 0,
            Count(0), Count(1), Count(2), Count(3), Count(4), Count(5), Count(6), Count(7),
            values?[8] as string, tac, identity?.Brand, identity?.MarketingName, rows.Plan));
    }

    /// <summary>
    /// Checks a request: its shape, then what the caller may see. Either a query or the response
    /// that refuses it, audited when it is a permission.
    /// </summary>
    private static async Task<(CheckedExplorerQuery? Query, IResult? Refusal)> CheckAsync(
        ExplorerQueryRequest request, HttpContext http, IAuditLog audit, ExplorerOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (query, problems) = ExplorerValidator.Check(request, options);
        if (query is null)
        {
            return (null, Results.ValidationProblem(
                problems.GroupBy(p => p.Path).ToDictionary(g => g.Key, g => g.Select(p => p.Message).ToArray()),
                title: "The query cannot run as written."));
        }

        var user = CurrentUser.Require(http);
        var missing = query.Permissions.Where(p => !user.Can(p)).Order(StringComparer.Ordinal).ToList();

        if (missing.Count > 0)
        {
            await audit.WriteAsync(Entry(user, http, AuditOutcome.Denied, query, new()
            {
                ["missing"] = string.Join(",", missing),
            }), ct).ConfigureAwait(false);

            return (null, Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Not permitted",
                detail: "This query uses fields that need permissions you do not hold: " + string.Join(", ", missing)
                    + ". Remove those fields, or ask an administrator.",
                extensions: new Dictionary<string, object?> { ["missing"] = missing }));
        }

        return (query, null);
    }

    private static AuditEntry Entry(
        AuthenticatedUser user, HttpContext http, AuditOutcome outcome, CheckedExplorerQuery query,
        Dictionary<string, object?> extra)
    {
        // What the query was, never what it looked for: field names, not values.
        var detail = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dataset"] = query.Definition.Dataset.ToString(),
            ["fields"] = string.Join(",", Fields(query.Where).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            ["columns"] = string.Join(",", query.Columns.Select(c => c.Name)),
            ["groupBy"] = string.Join(",", query.GroupBy),
            ["page"] = query.Page,
        };

        foreach (var (key, value) in extra)
        {
            detail[key] = value;
        }

        return new AuditEntry(
            user.Username, Permissions.ExplorerQuery, AuditCategory.Data, outcome, user.UserId, "explorer",
            Ip: http.Connection.RemoteIpAddress?.ToString(), UserAgent: http.Request.Headers.UserAgent.ToString(),
            CorrelationId: http.TraceIdentifier, Detail: detail);
    }

    private static IEnumerable<string> Fields(CheckedFilter? filter) => filter switch
    {
        CheckedCondition c => [c.Field],
        CheckedGroup g => g.Children.SelectMany(Fields),
        _ => [],
    };

    private static bool IsIdentifier(ExplorerFieldType type) =>
        type is ExplorerFieldType.Msisdn or ExplorerFieldType.Imsi or ExplorerFieldType.Imei;

    /// <summary>A row with its identifiers redacted, for a caller without identifier.reveal.</summary>
    private static IReadOnlyList<object?> Mask(IReadOnlyList<object?> row, IReadOnlyList<CheckedColumn> columns) =>
        [.. row.Select((value, i) => value is string s ? columns[i].Type switch
        {
            ExplorerFieldType.Msisdn => IdentifierMask.Msisdn(s),
            ExplorerFieldType.Imsi => IdentifierMask.Imsi(s),
            ExplorerFieldType.Imei => IdentifierMask.Imei(s),
            _ => value,
        } : value)];

    private static IResult Busy(string message) => Results.Problem(
        statusCode: StatusCodes.Status429TooManyRequests, title: "Busy", detail: message);

    /// <summary>At most two running Explorer queries per person.</summary>
    private static class UserSlots
    {
        private const int PerUser = 2;
        private static readonly ConcurrentDictionary<long, int> Running = new();

        public static bool TryEnter(long userId)
        {
            while (true)
            {
                var current = Running.GetOrAdd(userId, 0);
                if (current >= PerUser)
                {
                    return false;
                }

                if (Running.TryUpdate(userId, current + 1, current))
                {
                    return true;
                }
            }
        }

        public static void Exit(long userId) => Running.AddOrUpdate(userId, 0, (_, n) => Math.Max(0, n - 1));
    }
}
