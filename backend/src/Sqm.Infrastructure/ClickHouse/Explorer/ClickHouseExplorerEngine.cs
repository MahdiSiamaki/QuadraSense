using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sqm.Application.Explorer;
using Sqm.Contracts.Explorer;

namespace Sqm.Infrastructure.ClickHouse.Explorer;

/// <summary>Plans and runs Explorer queries against ClickHouse, within a budget.</summary>
/// <remarks>
/// <para>
/// Every run is planned first: the compiled SQL goes through <c>EXPLAIN ESTIMATE</c>, which reads
/// only indexes and answers in about 0.3 s with the rows the query would read. Over the budget, the
/// query is refused with notes that say what would narrow it - before the server has spent
/// anything. Within it, the query runs with the same budget as server-enforced limits, so an
/// estimate that turns out optimistic is still stopped.
/// </para>
/// <para>
/// Through <see cref="ClickHouseJsonQuery"/>, so the server treats every Explorer query as
/// read-only and cancels it when the caller disconnects.
/// </para>
/// </remarks>
public sealed partial class ClickHouseExplorerEngine : IExplorerEngine, IDisposable
{
    [LoggerMessage(EventId = 2300, Level = LogLevel.Information,
        Message = "Explorer {Dataset} query: {Access}, estimated {EstimatedRows} rows ({Verdict}), read {RowsRead} in {ElapsedMs} ms, {Returned} row(s) returned")]
    private partial void LogRan(string dataset, string access, long estimatedRows, string verdict, long rowsRead, long elapsedMs, int returned);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Warning,
        Message = "Slow Explorer {Dataset} query: {ElapsedMs} ms, {RowsRead} rows read, {Access} on {Source}")]
    private partial void LogSlow(string dataset, long elapsedMs, long rowsRead, string access, string source);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Information,
        Message = "Explorer {Dataset} query refused: estimated {EstimatedRows} rows against a budget of {BudgetRows}")]
    private partial void LogRefused(string dataset, long estimatedRows, long budgetRows);

    [LoggerMessage(EventId = 2303, Level = LogLevel.Information,
        Message = "Explorer {Dataset} query stopped by the server at its {Limit} limit")]
    private partial void LogStopped(string dataset, string limit);

    /// <summary>A query past this is logged as slow.</summary>
    private const long SlowMs = 5_000;

    private readonly ClickHouseJsonQuery _query;
    private readonly ExplorerOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly ILogger<ClickHouseExplorerEngine> _logger;

    /// <summary>Creates the engine over the analytics connection.</summary>
    public ClickHouseExplorerEngine(
        IOptions<ClickHouseOptions> clickHouse,
        IOptions<ExplorerOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<ClickHouseExplorerEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(clickHouse);
        ArgumentNullException.ThrowIfNull(options);

        _query = new ClickHouseJsonQuery(clickHouse.Value.ConnectionString, httpClientFactory);
        _options = options.Value;
        _slots = new SemaphoreSlim(_options.MaxConcurrentQueries, _options.MaxConcurrentQueries);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ExplorerPlanInfo> PlanAsync(CheckedExplorerQuery query, CancellationToken ct) =>
        (await PrepareAsync(query, ct).ConfigureAwait(false)).Plan;

    /// <inheritdoc />
    public async Task<ExplorerRows> RunAsync(CheckedExplorerQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Fail fast rather than queue: a queued query holds the caller's request open while
        // others run, and the server's memory is the thing being protected.
        if (!await _slots.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new ExplorerBusyException();
        }

        try
        {
            var prepared = await PrepareAsync(query, ct).ConfigureAwait(false);
            var dataset = query.Definition.Dataset.ToString();

            if (prepared.Compiled is null)
            {
                return new ExplorerRows(prepared.Plan, [], 0, 0, 0);
            }

            if (prepared.Plan.Verdict == "Refused")
            {
                LogRefused(dataset, prepared.Plan.EstimatedRows, _options.BudgetRows);
                throw new ExplorerRefusedException(prepared.Plan,
                    string.Create(CultureInfo.InvariantCulture,
                        $"This query would read about {prepared.Plan.EstimatedRows:N0} rows; the limit is {_options.BudgetRows:N0}."));
            }

            ClickHouseJsonResult result;
            try
            {
                result = await _query.ExecuteAsync(prepared.Compiled.Sql, prepared.Compiled.Parameters, ct,
                    new QueryBudget(
                        MaxRowsToRead: _options.BudgetRows,
                        MaxExecutionSeconds: _options.BudgetSeconds,
                        MaxResultRows: query.PageSize,
                        MaxMemoryBytes: _options.QueryMemoryBytes,
                        MaxThreads: _options.QueryThreads)).ConfigureAwait(false);
            }
            catch (QueryBudgetExceededException ex)
            {
                LogStopped(dataset, ex.Limit);
                throw new ExplorerRefusedException(prepared.Plan,
                    $"The server stopped this query at its {ex.Limit} limit. Narrow it - a number, SIM, handset, model or a shorter date range.", ex);
            }

            var rows = new List<IReadOnlyList<object?>>(result.Rows.Count);
            long total = 0;

            foreach (var row in result.Rows)
            {
                var values = new object?[query.Columns.Count];
                for (var i = 0; i < query.Columns.Count; i++)
                {
                    values[i] = Read(row[i], query.Columns[i].Type);
                }

                rows.Add(values);
                total = ClickHouseJsonResult.Int64(row, query.Columns.Count);
            }

            LogRan(dataset, prepared.Plan.Access, prepared.Plan.EstimatedRows, prepared.Plan.Verdict,
                result.RowsRead, result.ElapsedMs, rows.Count);

            if (result.ElapsedMs > SlowMs)
            {
                LogSlow(dataset, result.ElapsedMs, result.RowsRead, prepared.Plan.Access, prepared.Plan.Source);
            }

            return new ExplorerRows(prepared.Plan, rows, total, result.ElapsedMs, result.RowsRead);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DateOnly?> DataThroughAsync(CancellationToken ct)
    {
        // Answered from partition metadata: the log is partitioned by day, so the newest day is
        // known without reading a row.
        var result = await _query.ExecuteAsync(
            "SELECT max(data_date) FROM binding_event", new Dictionary<string, string>(), ct).ConfigureAwait(false);

        return result.Rows.Count > 0 && ClickHouseJsonResult.Date(result.Rows[0], 0) is { } day && day.Year > 1970
            ? day
            : null;
    }

    /// <summary>Compiled SQL and its plan; no SQL when the query can match nothing.</summary>
    private sealed record Prepared(CompiledExplorerQuery? Compiled, ExplorerPlanInfo Plan);

    private async Task<Prepared> PrepareAsync(CheckedExplorerQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var notes = new List<string>();
        IReadOnlyList<string>? tacs = null;

        // A brand or model every row must match becomes the TACs it names - read from the GSMA
        // table first, a few thousand rows at most - so the handset-ordered copy can be read by range.
        var device = ExplorerSqlCompiler.ResolvableDeviceConditions(query);
        if (device.Count > 0)
        {
            var (sql, parameters) = ExplorerSqlCompiler.TacLookup(device, _options.MaxResolvedTacs);
            var found = await _query.ExecuteAsync(sql, parameters, ct).ConfigureAwait(false);

            if (found.Rows.Count == 0)
            {
                return new Prepared(null, new ExplorerPlanInfo(
                    "The GSMA model table", "KeyRead", 0, "Light", _options.BudgetRows,
                    ["No model in the GSMA database matches the device conditions, so nothing can match. A handset whose TAC GSMA does not know has no brand or model at all."]));
            }

            if (found.Rows.Count > _options.MaxResolvedTacs)
            {
                notes.Add($"The device conditions match more than {_options.MaxResolvedTacs:N0} TACs - too many to read as ranges - so they are applied as a filter instead.");
            }
            else
            {
                tacs = [.. found.Rows.Select(r => ClickHouseJsonResult.Text(r, 0) ?? string.Empty)];
            }
        }

        var compiled = ExplorerSqlCompiler.Compile(query, tacs);
        var estimate = await _query.EstimateAsync(compiled.Sql, compiled.Parameters, ct).ConfigureAwait(false);
        var verdict = ExplorerBudget.Verdict(estimate.Rows, _options);

        notes.AddRange(compiled.Notes);

        if (query.FromDate is { } from && query.ToDate is { } to)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{to.DayNumber - from.DayNumber + 1:N0} day(s) of the event log, {from:yyyy-MM-dd} to {to:yyyy-MM-dd}; each day is its own partition, so days outside the range are never opened."));
        }

        if (verdict == "Refused")
        {
            notes.Add(query.Definition.Dataset == ExplorerDataset.Events
                ? "To bring it within the limit: a number, SIM or handset, or a shorter date range."
                : "To bring it within the limit: a number, SIM, handset, TAC or model every row must match - at the top level, not inside an Or or a Not.");
        }

        return new Prepared(compiled, new ExplorerPlanInfo(
            compiled.Source.Description, compiled.Access, estimate.Rows, verdict, _options.BudgetRows, notes));
    }

    /// <summary>A value as the API returns it: identifiers and text as strings, dates as yyyy-MM-dd.</summary>
    private static object? Read(JsonElement value, ExplorerFieldType type)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return type switch
        {
            ExplorerFieldType.Boolean => value.ValueKind == JsonValueKind.True
                || (value.ValueKind == JsonValueKind.Number && value.GetInt32() != 0)
                || (value.ValueKind == JsonValueKind.String && value.GetString() is "1" or "true"),
            ExplorerFieldType.Number => value.ValueKind == JsonValueKind.String
                ? long.Parse(value.GetString()!, CultureInfo.InvariantCulture)
                : value.GetInt64(),

            // 64-bit integers arrive as JSON strings; either way an identifier leaves as text.
            _ => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
        };
    }

    /// <inheritdoc />
    public void Dispose() => _slots.Dispose();
}
