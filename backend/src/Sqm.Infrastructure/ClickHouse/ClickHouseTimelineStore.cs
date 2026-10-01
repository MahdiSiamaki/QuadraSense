using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sqm.Application.Timeline;
using Sqm.Domain.Bindings;
using Sqm.Domain.Timeline;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>Reads timelines from the binding history (analytics migration 022).</summary>
/// <remarks>
/// <para>
/// One key read on the copy of the history sorted by the entity's kind, joined to the same kind's
/// copy of current state - which decides "active", as everywhere else in the product. Measured on
/// a prototype: an IMEI with 218 bindings read 81,920 rows in 34 ms; read from the event log the
/// same timeline costs a granule per number per day, 65 s for the most-shared IMEI.
/// </para>
/// <para>
/// Through <see cref="ClickHouseJsonQuery"/>, so the server treats it as read-only and cancels it
/// with the caller, and under a budget the server enforces: a key read that turned into a scan
/// would be stopped rather than served slowly.
/// </para>
/// </remarks>
public sealed partial class ClickHouseTimelineStore : ITimelineStore
{
    [LoggerMessage(EventId = 2400, Level = LogLevel.Information,
        Message = "Timeline {Kind}: {Bindings} binding(s) of {Total}, {RowsRead} rows read, {ElapsedMs} ms")]
    private partial void LogRead(string kind, int bindings, long total, long rowsRead, long elapsedMs);

    [LoggerMessage(EventId = 2401, Level = LogLevel.Warning,
        Message = "Slow timeline {Kind}: {ElapsedMs} ms, {RowsRead} rows read - the history's key is not being used")]
    private partial void LogSlow(string kind, long elapsedMs, long rowsRead);

    /// <summary>A key read reads tens of thousands of rows; past this, something is scanning.</summary>
    private const long SlowRows = 5_000_000;

    private static readonly TimeSpan ReadinessFor = TimeSpan.FromMinutes(1);

    private readonly ClickHouseJsonQuery _query;
    private readonly ILogger<ClickHouseTimelineStore> _logger;
    private readonly TimeProvider _clock;
    private (DateTimeOffset At, HistoryReadiness Value)? _readiness;

    /// <summary>Creates the store over the analytics connection.</summary>
    public ClickHouseTimelineStore(
        IOptions<ClickHouseOptions> options, IHttpClientFactory httpClientFactory,
        ILogger<ClickHouseTimelineStore> logger, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);

        _query = new ClickHouseJsonQuery(options.Value.ConnectionString, httpClientFactory);
        _logger = logger;
        _clock = clock;
    }

    private static readonly IReadOnlyDictionary<string, string> NoParameters =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<HistoryReadiness> GetReadinessAsync(CancellationToken ct)
    {
        if (_readiness is { } cached && _clock.GetUtcNow() - cached.At < ReadinessFor)
        {
            return cached.Value;
        }

        var deployed = await _query.ExecuteAsync(
            "SELECT count() FROM system.tables WHERE database = currentDatabase() AND name = 'binding_history_day'",
            NoParameters, ct).ConfigureAwait(false);

        HistoryReadiness value;
        if (ClickHouseJsonResult.Int64(deployed.Rows[0], 0) == 0)
        {
            value = HistoryReadiness.NotDeployed;
        }
        else
        {
            // Complete means every day the event log holds is written, and the dump is loaded
            // (or there is no dump to load). Read from metadata and a 233-row ledger: milliseconds.
            var state = await _query.ExecuteAsync("""
                SELECT
                    (SELECT uniqExact(partition) FROM system.parts
                      WHERE database = currentDatabase() AND table = 'binding_event' AND active),
                    (SELECT count() FROM binding_history_day FINAL
                      WHERE state = 'done' AND toString(data_date) IN (
                          SELECT partition FROM system.parts
                          WHERE database = currentDatabase() AND table = 'binding_event' AND active)),
                    (SELECT count() FROM system.parts
                      WHERE database = currentDatabase() AND table = 'binding_history' AND partition = '0' AND active),
                    (SELECT count() FROM binding_snapshot)
                """, NoParameters, ct).ConfigureAwait(false);

            var row = state.Rows[0];
            var complete = ClickHouseJsonResult.Int64(row, 1) >= ClickHouseJsonResult.Int64(row, 0)
                && (ClickHouseJsonResult.Int64(row, 2) > 0 || ClickHouseJsonResult.Int64(row, 3) == 0);
            value = complete ? HistoryReadiness.Ready : HistoryReadiness.Building;
        }

        _readiness = (_clock.GetUtcNow(), value);
        return value;
    }

    /// <inheritdoc />
    public async Task<DateOnly?> DataThroughAsync(CancellationToken ct)
    {
        var result = await _query.ExecuteAsync(
            "SELECT max(toDate(partition)) FROM system.parts WHERE database = currentDatabase() AND table = 'binding_event' AND active",
            NoParameters, ct).ConfigureAwait(false);

        return result.Rows.Count > 0 && ClickHouseJsonResult.Date(result.Rows[0], 0) is { } day && day.Year > 1970 ? day : null;
    }

    private static (string History, string Current, string Column, string Type) Source(TimelineCentre centre) => centre switch
    {
        TimelineCentre.Msisdn => ("binding_history", "binding_current", "msisdn", "UInt64"),
        TimelineCentre.Imsi => ("binding_history_by_imsi", "binding_by_imsi", "imsi", "UInt64"),
        _ => ("binding_history_by_imei", "binding_by_imei", "imei", "String"),
    };

    /// <inheritdoc />
    public async Task<StoredTimeline> GetAsync(TimelineCentre centre, string digits, int limit, CancellationToken ct)
    {
        var (history, current, column, type) = Source(centre);

        // Most recently changed first, so a limit keeps what is newest. The dump's zero date sorts
        // a never-changed binding last, which is where an unconfirmed one belongs.
        var sql = $$"""
            SELECT
                h.msisdn, h.imsi, h.imei, h.tac, t.brand, t.model, h.in_dump, h.events, c.active, h.total
            FROM (
                SELECT msisdn, imsi, imei,
                       if(length(imei) = 14, substring(imei, 1, 8), '') AS tac,
                       max(in_dump)            AS in_dump,
                       groupArrayArray(events) AS events,
                       max(last_date)          AS last_date,
                       min(first_date)         AS first_date,
                       count() OVER ()         AS total
                FROM {{history}}
                WHERE {{column}} = {value:{{type}}}
                GROUP BY msisdn, imsi, imei
                ORDER BY last_date DESC, first_date DESC, msisdn, imsi, imei
                LIMIT {limit:UInt32}
            ) AS h
            LEFT JOIN (
                SELECT msisdn, imsi, imei, active FROM {{current}} FINAL WHERE {{column}} = {value:{{type}}}
            ) AS c USING (msisdn, imsi, imei)
            LEFT JOIN (
                SELECT tac,
                       nullIf(coalesce(nullIf(brandName, ''), manufacturer), '') AS brand,
                       nullIf(marketingName, '')                                 AS model
                FROM tac
            ) AS t ON t.tac = h.tac
            ORDER BY h.last_date DESC, h.first_date DESC, h.msisdn, h.imsi, h.imei
            SETTINGS join_use_nulls = 1
            """;

        var result = await _query.ExecuteAsync(
            sql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["value"] = digits,
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            },
            ct,
            new QueryBudget(MaxRowsToRead: 50_000_000, MaxExecutionSeconds: 30, MaxResultRows: limit + 1))
            .ConfigureAwait(false);

        var bindings = new List<StoredBinding>(result.Rows.Count);
        long total = 0;

        foreach (var row in result.Rows)
        {
            bindings.Add(new StoredBinding(
                ClickHouseJsonResult.UInt64(row, 0),
                ClickHouseJsonResult.UInt64(row, 1),
                ClickHouseJsonResult.Text(row, 2) ?? string.Empty,
                ClickHouseJsonResult.NullIfEmpty(row, 3),
                ClickHouseJsonResult.NullIfEmpty(row, 4),
                ClickHouseJsonResult.NullIfEmpty(row, 5),
                ClickHouseJsonResult.Int32(row, 6) == 1,
                Events(row[7]),
                row[8].ValueKind != JsonValueKind.Null && ClickHouseJsonResult.Int32(row, 8) == 1));

            total = ClickHouseJsonResult.Int64(row, 9);
        }

        var kind = TimelineBuilder.Kind(centre);
        LogRead(kind, bindings.Count, total, result.RowsRead, result.ElapsedMs);

        if (result.RowsRead > SlowRows)
        {
            LogSlow(kind, result.ElapsedMs, result.RowsRead);
        }

        return new StoredTimeline(bindings, total, result.ElapsedMs, result.RowsRead);
    }

    /// <inheritdoc />
    public async Task<EntitySeen?> GetSeenAsync(TimelineCentre centre, string digits, CancellationToken ct)
    {
        var (history, _, column, type) = Source(centre);

        var result = await _query.ExecuteAsync(
            $"SELECT count(), min(first_date), max(in_dump), max(last_date) FROM {history} WHERE {column} = {{value:{type}}}",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = digits },
            ct,
            new QueryBudget(MaxRowsToRead: 50_000_000, MaxExecutionSeconds: 30, MaxResultRows: 1))
            .ConfigureAwait(false);

        var row = result.Rows[0];
        if (ClickHouseJsonResult.Int64(row, 0) == 0)
        {
            return null;
        }

        var last = ClickHouseJsonResult.Date(row, 3);
        return new EntitySeen(
            ClickHouseJsonResult.Date(row, 1) ?? InitialDump.WindowStart,
            ClickHouseJsonResult.Int32(row, 2) == 1,
            last is { Year: > 1970 } ? last : null);
    }

    /// <summary>(date, seq, 1 = add | 2 = remove) tuples, as JSONCompact writes them: arrays.</summary>
    private static List<BindingEvent> Events(JsonElement array)
    {
        var events = new List<BindingEvent>(array.GetArrayLength());

        foreach (var e in array.EnumerateArray())
        {
            events.Add(new BindingEvent(
                DateOnly.Parse(e[0].GetString()!, CultureInfo.InvariantCulture),
                e[1].GetInt32(),
                e[2].GetInt32() == 1 ? ChangeLabel.Add : ChangeLabel.Remove));
        }

        return events;
    }
}
