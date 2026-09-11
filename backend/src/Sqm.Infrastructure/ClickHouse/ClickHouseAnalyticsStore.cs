using System.Data.Common;
using System.Globalization;
using ClickHouse.Client.ADO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sqm.Application.Abstractions;
using Sqm.Contracts.Dashboard;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>Connection settings for the analytics store.</summary>
public sealed class ClickHouseOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ClickHouse";

    /// <summary>ADO connection string, e.g. <c>Host=localhost;Port=8123;Database=sqm;…</c>.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Server-side timeout applied to every query, in seconds.</summary>
    /// <remarks>
    /// Enforced at the database, not only in the application, so a runaway aggregation cannot pin a core
    /// after the client has given up. Measured worst case (the churn aggregate) is ~52 s, so 120 s leaves
    /// headroom without allowing a query to run indefinitely.
    /// </remarks>
    public int QueryTimeoutSeconds { get; set; } = 120;

    /// <summary>Hard cap on rows any single query may return to the API.</summary>
    public int MaxResultRows { get; set; } = 10_000;
}

/// <summary>ClickHouse-backed implementation of the analytics queries.</summary>
/// <remarks>
/// Logging uses source-generated <see cref="LoggerMessageAttribute"/> delegates rather than the
/// <c>ILogger.LogXxx</c> extensions: they avoid boxing and skip argument evaluation entirely when the
/// level is disabled. On a path that runs per dashboard widget per user, that is worth having.
/// </remarks>
public sealed partial class ClickHouseAnalyticsStore : IDeviceAnalyticsStore
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Subscriber lookup returned {BindingCount} bindings")]
    private partial void LogLookupCompleted(int bindingCount);

    private readonly ClickHouseOptions _options;
    private readonly ILogger<ClickHouseAnalyticsStore> _logger;

    public ClickHouseAnalyticsStore(
        IOptions<ClickHouseOptions> options,
        ILogger<ClickHouseAnalyticsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<KpiSummary> GetKpiSummaryAsync(DashboardFilter filter, CancellationToken ct)
    {
        var f = FilterBuilder.ForCurrentState(filter);

        // uniqExact would be correct but costs ~52 s on this cardinality (measured). uniq() is a
        // HyperLogLog estimate with ~0.5% error, which is invisible on a KPI card reading "73.3M" and
        // three orders of magnitude cheaper. Exact counts remain available via export.
        var sql = $"""
            SELECT
                count()                                        AS active_bindings,
                uniq(b.msisdn)                                 AS distinct_subscribers,
                uniq(b.imei)                                   AS distinct_devices,
                countIf(b.imei = '000000')                     AS unknown_device_bindings,
                round(100.0 * countIf(t.tac != '') / count(), 3) AS tac_coverage_pct
            FROM sqm.binding_current AS b
            LEFT JOIN sqm.tac AS t ON t.tac = b.tac
            LEFT JOIN sqm.tac_vendor_map AS v ON v.raw_manufacturer = t.manufacturer
            WHERE b.active = 1 AND {f.WhereClause}
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, sql);
        f.Bind(command);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new KpiSummary(0, 0, 0, 0, 0);
        }

        return new KpiSummary(
            ActiveBindings: GetInt64(reader, 0),
            DistinctSubscribers: GetInt64(reader, 1),
            DistinctDevices: GetInt64(reader, 2),
            UnknownDeviceBindings: GetInt64(reader, 3),
            TacCoveragePercent: GetDouble(reader, 4));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DimensionCount>> GetTopDimensionAsync(
        AnalyticsDimension dimension, DashboardFilter filter, int limit, CancellationToken ct)
    {
        var column = AnalyticsSchema.ColumnFor(dimension);
        var f = FilterBuilder.ForCurrentState(filter);
        var take = FilterBuilder.ClampLimit(limit);

        // The unknown-device population has no dimension value at all, so it is labelled rather than
        // silently excluded — it would otherwise be a 7% hole in every chart.
        // Note the doubled `$$`: in a raw string literal `{{ }}` marks interpolation, which leaves single
        // braces free to carry ClickHouse's own `{name:Type}` parameter syntax verbatim.
        var sql = $$"""
            SELECT
                multiIf(b.tac = '', {unknown_device:String},
                        t.tac = '',  {unknown_tac:String},
                        coalesce(nullIf({{column}}, ''), {unknown_tac:String})) AS dim_key,
                count() AS n
            FROM sqm.binding_current AS b
            LEFT JOIN sqm.tac AS t ON t.tac = b.tac
            LEFT JOIN sqm.tac_vendor_map AS v ON v.raw_manufacturer = t.manufacturer
            WHERE b.active = 1 AND {{f.WhereClause}}
            GROUP BY dim_key
            ORDER BY n DESC
            LIMIT {{take}}
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, sql);
        f.Bind(command);
        AddLabelParameters(command);

        var rows = new List<(string Key, long Count)>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add((reader.GetString(0), GetInt64(reader, 1)));
            }
        }

        var total = rows.Sum(r => r.Count);
        return rows
            .Select(r => new DimensionCount(r.Key, r.Count, Percent(r.Count, total)))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DistributionSlice>> GetDistributionAsync(
        AnalyticsDimension dimension, DashboardFilter filter, CancellationToken ct)
    {
        var top = await GetTopDimensionAsync(dimension, filter, 50, ct).ConfigureAwait(false);
        return top.Select(d => new DistributionSlice(d.Key, d.Count, d.Percent)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BindingRow>> GetBindingsForMsisdnAsync(
        ulong msisdn, CancellationToken ct)
    {
        // Measured at a median of 12 ms, reading 278K of 125.9M rows: the primary index on
        // (msisdn, imsi, imei) skips 99.8% of the table.
        const string Sql = """
            SELECT
                b.msisdn, b.imsi, b.imei, b.tac,
                t.manufacturer, t.marketingName, t.deviceType, b.active
            FROM sqm.binding_current AS b
            LEFT JOIN sqm.tac AS t ON t.tac = b.tac
            WHERE b.msisdn = {msisdn:UInt64}
            ORDER BY b.active DESC, b.imsi, b.imei
            LIMIT 1000
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, Sql);

        var p = command.CreateParameter();
        p.ParameterName = "msisdn";
        p.Value = msisdn;
        command.Parameters.Add(p);

        var results = new List<BindingRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var tac = reader.GetString(3);
            results.Add(new BindingRow(
                Msisdn: GetUInt64(reader, 0).ToString(CultureInfo.InvariantCulture),
                Imsi: GetUInt64(reader, 1).ToString(CultureInfo.InvariantCulture),
                Imei: reader.GetString(2),
                Tac: string.IsNullOrEmpty(tac) ? null : tac,
                Manufacturer: NullIfEmpty(reader, 4),
                MarketingName: NullIfEmpty(reader, 5),
                DeviceType: NullIfEmpty(reader, 6),
                IsActive: Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture) == 1));
        }

        // Deliberately logs only the count. The MSISDN itself is never written to a log: with UI masking
        // off, keeping identifiers out of logs is the control actually doing the work. The lookup is
        // recorded against the user in the audit log instead, which is access-controlled.
        LogLookupCompleted(results.Count);

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChangePoint>> GetChangeSeriesAsync(
        DashboardFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Reads the pre-aggregated mart, never the event log. Against 3B rows/year the raw query would
        // be minutes; the mart is ~95K rows/day and answers in milliseconds.
        var f = new FilterBuilder().WithSequenceRange(filter.SequenceFrom, filter.SequenceTo);

        var sql = $"""
            SELECT
                e.seq                                 AS seq,
                any(e.data_date)                      AS data_date,
                sumIf(e.n, e.label = 'add')           AS added,
                sumIf(e.n, e.label = 'remove')        AS removed
            FROM sqm.agg_change_daily AS e
            WHERE {f.WhereClause}
            GROUP BY seq
            ORDER BY seq
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, sql);
        f.Bind(command);

        var points = new List<ChangePoint>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var seq = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            DateOnly? date = reader.IsDBNull(1)
                ? null
                : DateOnly.FromDateTime(reader.GetDateTime(1));
            var added = GetInt64(reader, 2);
            var removed = GetInt64(reader, 3);

            points.Add(new ChangePoint(seq, date, added, removed, added - removed));
        }

        return points;
    }

    private ClickHouseConnection CreateConnection() => new(_options.ConnectionString);

    private ClickHouseCommand CreateCommand(ClickHouseConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        return command;
    }

    private static void AddLabelParameters(ClickHouseCommand command)
    {
        var unknownDevice = command.CreateParameter();
        unknownDevice.ParameterName = "unknown_device";
        unknownDevice.Value = AnalyticsSchema.UnknownDeviceLabel;
        command.Parameters.Add(unknownDevice);

        var unknownTac = command.CreateParameter();
        unknownTac.ParameterName = "unknown_tac";
        unknownTac.Value = AnalyticsSchema.UnknownTacLabel;
        command.Parameters.Add(unknownTac);
    }

    private static double Percent(long part, long total) =>
        total == 0 ? 0 : Math.Round(100.0 * part / total, 3);

    private static long GetInt64(DbDataReader reader, int ordinal) =>
        Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static ulong GetUInt64(DbDataReader reader, int ordinal) =>
        Convert.ToUInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static double GetDouble(DbDataReader reader, int ordinal) =>
        Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static string? NullIfEmpty(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetString(ordinal);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
