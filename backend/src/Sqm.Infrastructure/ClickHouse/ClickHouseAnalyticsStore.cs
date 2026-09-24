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
/// <para>
/// Logging uses source-generated <see cref="LoggerMessageAttribute"/> delegates rather than the
/// <c>ILogger.LogXxx</c> extensions: they avoid boxing and skip argument evaluation entirely when the
/// level is disabled. On a path that runs per dashboard widget per user, that is worth having.
/// </para>
/// <para>
/// Every read of <c>binding_current</c> uses <c>FINAL</c>. It is a ReplacingMergeTree keyed on
/// (msisdn, imsi, imei) and versioned by the last change sequence, and a daily fold inserts a
/// fresh row for every binding that day touched. Until a background merge collapses them - which
/// happens when ClickHouse decides, not when a query runs - both rows are present, and a read
/// without FINAL counts the binding twice. That produces no error and no warning, just quietly
/// larger numbers on a screen whose whole job is to be believed.
/// </para>
/// <para>
/// These are the fallback paths that read raw rows; the marts, which the dashboard uses for
/// almost everything, are built from FINAL reads in the refresh job for the same reason.
/// </para>
/// <para>
/// Every mart read picks its delivery from <c>sqm.mart_ready</c> rather than from
/// <c>max(seq)</c> of the mart itself. A refresh writes fifteen statements across six marts, so
/// a partly-failed run leaves the new sequence present in every one of them while several
/// slices are missing - and <c>max(seq)</c> would then select that half-built delivery and
/// return empty charts. <c>mart_ready</c> holds only sequences whose refresh finished, so the
/// dashboard serves the newest delivery that is actually whole.
/// </para>
/// </remarks>
public sealed partial class ClickHouseAnalyticsStore : IDeviceAnalyticsStore
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Subscriber lookup returned {BindingCount} bindings")]
    private partial void LogLookupCompleted(int bindingCount);

    /// <summary>Name of the pooled <see cref="HttpClient"/> used for every ClickHouse call.</summary>
    public const string HttpClientName = "clickhouse";

    private readonly ClickHouseOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    // Named `_logger` because the LoggerMessage source generator looks for an ILogger field
    // on the containing type to emit the partial method body against.
    private readonly ILogger<ClickHouseAnalyticsStore> _logger;

    public ClickHouseAnalyticsStore(
        IOptions<ClickHouseOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<ClickHouseAnalyticsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<KpiSummary> GetKpiSummaryAsync(DashboardFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // The mart counts the unknown-device population, so "hide unknown devices" needs the raw
        // path too. Without it the KPI row went on counting them while every chart beside it,
        // under the same toggle, did not.
        var unfiltered =
            MartRouting.CanUseMart(filter) &&
            filter.IncludeUnknownDevice &&
            string.IsNullOrWhiteSpace(filter.Manufacturer) &&
            string.IsNullOrWhiteSpace(filter.VendorCanonical) &&
            string.IsNullOrWhiteSpace(filter.DeviceType) &&
            string.IsNullOrWhiteSpace(filter.OperatingSystem) &&
            string.IsNullOrWhiteSpace(filter.Tac);

        return unfiltered
            ? await GetKpiFromMartAsync(ct).ConfigureAwait(false)
            : await GetKpiFromRawAsync(filter, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The unfiltered KPI row, read straight from the pre-computed mart.
    /// </summary>
    /// <remarks>
    /// The distinct counts here cannot be derived from the TAC rollup — one subscriber holds bindings
    /// across several TACs, so distinct MSISDNs per TAC do not sum to distinct MSISDNs overall. They are
    /// computed once at ingest over the whole population and stored, which is why this table exists
    /// separately from <c>agg_device_daily</c>.
    /// </remarks>
    private async Task<KpiSummary> GetKpiFromMartAsync(CancellationToken ct)
    {
        const string Sql = """
            SELECT
                active_bindings,
                distinct_subscribers,
                distinct_sims,
                distinct_devices,
                unknown_device_bindings,
                malformed_imei_bindings,
                tac_matched_bindings,
                -- Zero when there is nothing to cover, not NULL: the reader takes a number.
                ifNull(round(100.0 * tac_matched_bindings / nullIf(active_bindings, 0), 3), 0) AS tac_coverage_pct,
                seq,
                -- The delivery's own day, so the caller can say which delivery these figures
                -- describe rather than implying they are the newest data imported.
                (SELECT max(data_date) FROM sqm.agg_change_summary_daily AS d
                  WHERE d.seq = agg_kpi_daily.seq) AS delivery_date
            FROM sqm.agg_kpi_daily
            WHERE seq = (SELECT max(seq) FROM sqm.mart_ready)
            ORDER BY seq DESC
            LIMIT 1
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, Sql);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new KpiSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, null);
        }

        return new KpiSummary(
            ActiveBindings: GetInt64(reader, 0),
            DistinctSubscribers: GetInt64(reader, 1),
            DistinctSims: GetInt64(reader, 2),
            DistinctDevices: GetInt64(reader, 3),
            UnknownDeviceBindings: GetInt64(reader, 4),
            MalformedImeiBindings: GetInt64(reader, 5),
            TacMatchedBindings: GetInt64(reader, 6),
            TacCoveragePercent: GetDouble(reader, 7),
            DeliverySequence: (int)GetInt64(reader, 8),
            DeliveryDate: GetDateOrNull(reader, 9));
    }

    /// <summary>Reads a nullable Date column.</summary>
    /// <remarks>
    /// The initial dump has no delivery date - it covers 2025-12-27 to 2026-01-25, a window
    /// rather than a day - so null here is a real answer and not a missing one.
    /// </remarks>
    private static DateOnly? GetDateOrNull(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateOnly.FromDateTime(reader.GetDateTime(ordinal));

    /// <summary>Filtered KPIs, which must scan the raw table. Measured at ~6 s.</summary>
    private async Task<KpiSummary> GetKpiFromRawAsync(DashboardFilter filter, CancellationToken ct)
    {
        var f = FilterBuilder.ForCurrentState(filter);

        // uniqExact would be correct but costs ~52 s on this cardinality (measured). uniq() is a
        // HyperLogLog estimate with ~0.5% error, which is invisible on a KPI card reading "79.5M" and
        // three orders of magnitude cheaper. Exact counts remain available via export.
        var sql = $"""
            SELECT
                count()                                        AS active_bindings,
                uniq(b.msisdn)                                 AS distinct_subscribers,
                uniq(b.imsi)                                   AS distinct_sims,
                uniqIf(b.imei, length(b.imei) = 14)            AS distinct_devices,
                countIf(b.imei = '000000')                     AS unknown_device_bindings,
                countIf(length(b.imei) != 14 AND b.imei != '000000') AS malformed_imei_bindings,
                countIf(t.tac != '')                           AS tac_matched_bindings,
                -- A filter that matches nothing made this 0/0: NaN, which JSON cannot carry, so the
                -- whole KPI request failed. Zero coverage of zero bindings is the honest figure.
                if(count() = 0, 0, round(100.0 * countIf(t.tac != '') / count(), 3)) AS tac_coverage_pct,
                -- The raw path reads current state, which already has every delivery folded in,
                -- so it reports the newest delivery rather than whichever one the marts serve.
                -- agg_change_summary_daily is 133 rows; reading max(seq) off binding_event would
                -- be a full column scan of a billion.
                (SELECT argMax(seq, data_date) FROM sqm.agg_change_summary_daily) AS delivery_seq,
                -- NULL before the first daily file, not max() of nothing, which is 1970-01-01.
                (SELECT if(count() = 0, NULL, max(data_date)) FROM sqm.agg_change_summary_daily) AS delivery_date
            FROM sqm.binding_current AS b FINAL
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
            return new KpiSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, null);
        }

        return new KpiSummary(
            ActiveBindings: GetInt64(reader, 0),
            DistinctSubscribers: GetInt64(reader, 1),
            DistinctSims: GetInt64(reader, 2),
            DistinctDevices: GetInt64(reader, 3),
            UnknownDeviceBindings: GetInt64(reader, 4),
            MalformedImeiBindings: GetInt64(reader, 5),
            TacMatchedBindings: GetInt64(reader, 6),
            TacCoveragePercent: GetDouble(reader, 7),
            DeliverySequence: (int)GetInt64(reader, 8),
            DeliveryDate: GetDateOrNull(reader, 9));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DimensionCount>> GetTopDimensionAsync(
        AnalyticsDimension dimension, DashboardFilter filter, int limit, CountBy countBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return MartRouting.CanUseMart(filter) && !HasDimensionFilter(filter)
            ? await TopFromMartAsync(dimension, limit, countBy, filter.IncludeUnknownDevice, ct)
                .ConfigureAwait(false)
            : await TopFromRawAsync(dimension, filter, limit, countBy, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Unfiltered breakdown, read from the per-dimension mart.
    /// </summary>
    /// <remarks>
    /// The mart carries all three measures precomputed, because distinct counts cannot be derived
    /// at query time: subscribers do not sum across dimension values (one person can own a Samsung
    /// and an Apple), so there is nothing to add up.
    /// </remarks>
    private async Task<IReadOnlyList<DimensionCount>> TopFromMartAsync(
        AnalyticsDimension dimension, int limit, CountBy countBy, bool includeUnknownDevice,
        CancellationToken ct)
    {
        var measure = AnalyticsSchema.MartColumnFor(countBy);
        var populationTotal = AnalyticsSchema.PopulationTotalFor(countBy);
        var take = FilterBuilder.ClampLimit(limit);

        // Hiding the unknown bucket is one row to skip in a 26k-row rollup, not a reason to
        // scan 126M rows. Treating it as a filter sent the model breakdown down the raw path,
        // where grouping by marketing name with a distinct count ran past 100 seconds.
        var unknownClause = includeUnknownDevice
            ? string.Empty
            : $" AND dim_value NOT IN ('{AnalyticsSchema.UnknownDeviceLabel}', "
              + $"'{AnalyticsSchema.UnknownTacLabel}')";

        // The percentage denominator is the whole population, taken from the KPI mart — never the
        // sum of the rows returned. A share of the top-N subtotal changes with the page size.
        var sql = $$"""
            SELECT
                dim_value AS k,
                {{measure}} AS n,
                round(100.0 * {{measure}} / nullIf(
                    (SELECT {{populationTotal}} FROM sqm.agg_kpi_daily WHERE seq = (SELECT max(seq) FROM sqm.mart_ready) LIMIT 1), 0), 3) AS pct
            FROM sqm.agg_dimension_daily
            WHERE seq = (SELECT max(seq) FROM sqm.mart_ready)
              AND dimension = {dimension:String}{{unknownClause}}
            ORDER BY n DESC
            LIMIT {{take}}
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, sql);

        var p = command.CreateParameter();
        p.ParameterName = "dimension";
        p.Value = AnalyticsSchema.MartKeyFor(dimension);
        command.Parameters.Add(p);

        return await ReadDimensionCountsAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>Filtered breakdown. Scans the raw table; measured at several seconds.</summary>
    private async Task<IReadOnlyList<DimensionCount>> TopFromRawAsync(
        AnalyticsDimension dimension, DashboardFilter filter, int limit, CountBy countBy,
        CancellationToken ct)
    {
        var column = AnalyticsSchema.ColumnFor(dimension);
        var measure = AnalyticsSchema.RawMeasureFor(countBy);
        var f = FilterBuilder.ForCurrentState(filter);
        var take = FilterBuilder.ClampLimit(limit);

        // Under a filter the population is the filtered population, so the denominator is computed
        // over the same WHERE clause rather than read from the unfiltered KPI mart.
        //
        // And with the same measure, ungrouped - not the sum of the groups. For bindings those are
        // equal; for subscribers or handsets they are not, because one subscriber with a phone and
        // a tablet is in both groups: the sum counted them twice and the shares came out 50/50
        // where they are 100% each. The empty grouping set is that ungrouped total, taken in the
        // same pass rather than a second scan.
        var sql = $$"""
            SELECT k, n, round(100.0 * n / nullIf(total, 0), 3) AS pct
            FROM (
                SELECT k, n, is_total, max(if(is_total = 1, n, 0)) OVER () AS total
                FROM (
                    SELECT
                        multiIf(b.tac = '', {unknown_device:String},
                                t.tac = '',  {unknown_tac:String},
                                coalesce(nullIf({{column}}, ''), {unknown_tac:String})) AS k,
                        {{measure}} AS n,
                        grouping(k) AS is_total
                    FROM sqm.binding_current AS b FINAL
                    LEFT JOIN sqm.tac AS t ON t.tac = b.tac
                    LEFT JOIN sqm.tac_vendor_map AS v ON v.raw_manufacturer = t.manufacturer
                    WHERE b.active = 1 AND {{f.WhereClause}}
                    GROUP BY GROUPING SETS ((k), ())
                )
            )
            WHERE is_total = 0
            ORDER BY n DESC LIMIT {{take}}
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, sql);
        f.Bind(command);
        AddLabelParameters(command);

        return await ReadDimensionCountsAsync(command, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<DimensionCount>> ReadDimensionCountsAsync(
        ClickHouseCommand command, CancellationToken ct)
    {
        var rows = new List<DimensionCount>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new DimensionCount(
                reader.GetString(0),
                GetInt64(reader, 1),
                reader.IsDBNull(2) ? 0 : GetDouble(reader, 2)));
        }
        return rows;
    }

    /// <summary>True when a filter narrows the population, forcing the raw-table path.</summary>
    private static bool HasDimensionFilter(DashboardFilter filter) =>
        !string.IsNullOrWhiteSpace(filter.Manufacturer)
        || !string.IsNullOrWhiteSpace(filter.VendorCanonical)
        || !string.IsNullOrWhiteSpace(filter.DeviceType)
        || !string.IsNullOrWhiteSpace(filter.OperatingSystem)
        || !string.IsNullOrWhiteSpace(filter.Tac);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DistributionSlice>> GetDistributionAsync(
        AnalyticsDimension dimension, DashboardFilter filter, CountBy countBy, CancellationToken ct)
    {
        var top = await GetTopDimensionAsync(dimension, filter, 50, countBy, ct).ConfigureAwait(false);
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
                t.manufacturer, t.marketingName, t.deviceType, b.active,
                -- NULL when no daily file has ever mentioned this binding: it is active only
                -- because the initial dump listed it and nothing has removed it since.
                b.last_change_date
            FROM sqm.binding_current AS b FINAL
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
                IsActive: Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture) == 1,
                LastChangeDate: GetDateOrNull(reader, 8)));
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
            ORDER BY data_date
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<DistributionSlice>> GetDeviceClassMixAsync(
        CountBy countBy, CancellationToken ct)
    {
        // Under bindings and handsets the classes partition the population. Under
        // subscribers they overlap - someone owning a smartphone and a feature phone is in
        // both - so the shares sum past 100%. The UI says so rather than hiding it.
        const string Sql = """
            SELECT device_class, sum(n) AS n
            FROM sqm.agg_device_class_daily
            WHERE seq = (SELECT max(seq) FROM sqm.mart_ready)
              AND measure = {measure:String}
            GROUP BY device_class
            ORDER BY n DESC
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, Sql);
        AddMeasureParameter(command, countBy);

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
            .Select(r => new DistributionSlice(r.Key, r.Count, Percent(r.Count, total)))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CapabilitySupport>> GetCapabilitySupportAsync(
        CountBy countBy, CancellationToken ct)
    {
        const string Sql = """
            SELECT capability, supported, unsupported, unknown
            FROM sqm.agg_capability_daily
            WHERE seq = (SELECT max(seq) FROM sqm.mart_ready)
              AND measure = {measure:String}
            ORDER BY supported DESC
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, Sql);
        AddMeasureParameter(command, countBy);

        var results = new List<CapabilitySupport>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var capability = reader.GetString(0);
            var supported = GetInt64(reader, 1);
            var unsupported = GetInt64(reader, 2);
            var unknown = GetInt64(reader, 3);

            var assessable = supported + unsupported;
            var total = assessable + unknown;

            results.Add(new CapabilitySupport(
                Capability: capability,
                Supported: supported,
                Unsupported: unsupported,
                Unknown: unknown,
                PercentOfAssessable: Percent(supported, assessable),
                PercentOfAll: Percent(supported, total),
                CoveragePercent: Percent(assessable, total)));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DailyChange>> GetDailyChangesAsync(CancellationToken ct)
    {
        // The running total is computed in SQL rather than in the client: it is a window
        // function over ~150 rows, and doing it here keeps the API's shape the same whether
        // the caller wants one month or the whole history.
        //
        // IT IS ANCHORED TO THE END, NOT THE BEGINNING, and that is the whole correctness of it.
        //
        // The obvious form - start at the initial dump and add each day's net - was wrong by
        // 9,532,541 bindings, 8.3%, and put a line on the chart that ended at 124,684,132 while
        // the KPI card beside it read 115,151,591. Two figures for one quantity on one screen.
        //
        // The cause is the dump itself. It lists every binding seen over a 30-day window rather
        // than at an instant, averaging 1.57 handsets per SIM, so its 125,939,523 rows are not a
        // population and the event stream never reconciles to them. Measured: the drift is
        // already 9,549,453 at the earliest delivery that has a measured population and stays
        // flat to within 0.4% of itself for every delivery after, so it is an error in the
        // starting point rather than one that accumulates.
        //
        // Anchoring instead to the population the KPI mart actually measured, and walking the
        // net backwards, was checked against all 16 deliveries whose population is known: worst
        // drift 28,554, or 0.025%, against a constant 9.5 million. The last point is exact by
        // construction, which is the one a reader cross-checks against the card above it.
        //
        // Expressed as a baseline rather than a reversed window so it stays a single pass:
        //   cumulative(d) = anchor - net_through_anchor + running_net(d)
        // At d = anchor this is the anchor; before it, it walks back; after it - days folded but
        // not yet in a complete snapshot - it walks forward, which is what those days are.
        const string Sql = """
            WITH
                (SELECT max(seq) FROM sqm.mart_ready) AS anchor_seq,
                (SELECT active_bindings FROM sqm.agg_kpi_daily WHERE seq = anchor_seq) AS anchor_pop,
                -- Through the anchor's DAY, not its sequence number: a missing day imported late
                -- has a higher number than the anchor but sits before it in the series.
                (SELECT max(data_date) FROM sqm.agg_change_summary_daily
                  WHERE seq = anchor_seq) AS anchor_date,
                (SELECT sum(toInt64(added) - toInt64(removed))
                   FROM sqm.agg_change_summary_daily WHERE data_date <= anchor_date) AS net_to_anchor
            SELECT
                data_date,
                added,
                removed,
                toInt64(added) - toInt64(removed) AS net,
                anchor_pop - net_to_anchor
                    + sum(toInt64(added) - toInt64(removed)) OVER (ORDER BY data_date) AS cumulative,
                unknown_device_rows
            FROM sqm.agg_change_summary_daily
            ORDER BY data_date
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, Sql);

        var rows = new List<DailyChange>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new DailyChange(
                Date: DateOnly.FromDateTime(reader.GetDateTime(0)),
                Added: GetInt64(reader, 1),
                Removed: GetInt64(reader, 2),
                Net: GetInt64(reader, 3),
                Cumulative: GetInt64(reader, 4),
                UnknownDeviceRows: GetInt64(reader, 5)));
        }
        return rows;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DailyChurn>> GetDailyChurnAsync(CancellationToken ct)
    {
        // FULL OUTER would be tidier, but the two marts are keyed identically and both are
        // ~133 rows, so a union-and-group is simpler to read and costs nothing.
        const string Sql = """
            SELECT data_date, sum(sim_changes) AS sim_changes, sum(device_changes) AS device_changes
            FROM (
                SELECT data_date, msisdn_changed AS sim_changes, 0 AS device_changes
                FROM sqm.agg_sim_change_daily
                UNION ALL
                SELECT data_date, 0, msisdn_changed
                FROM sqm.agg_device_change_daily
            )
            GROUP BY data_date
            ORDER BY data_date
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, Sql);

        var rows = new List<DailyChurn>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new DailyChurn(
                DateOnly.FromDateTime(reader.GetDateTime(0)),
                GetInt64(reader, 1),
                GetInt64(reader, 2)));
        }
        return rows;
    }

    /// <summary>
    /// Opens a connection backed by the pooled <see cref="HttpClient"/>.
    /// </summary>
    /// <remarks>
    /// Constructing a connection with only a connection string makes ClickHouse.Client create its own
    /// <see cref="HttpClient"/> per instance. That costs a fresh TCP and TLS handshake on every query —
    /// measured here at roughly a second of overhead on a 39 ms query — and under load it leaks sockets
    /// into TIME_WAIT until the port range is exhausted. Passing the factory reuses pooled connections.
    /// </remarks>
    private ClickHouseConnection CreateConnection() =>
        new(_options.ConnectionString, _httpClientFactory, HttpClientName);

    private ClickHouseCommand CreateCommand(ClickHouseConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        return command;
    }

    /// <summary>Binds the measure name used by the measure-aware marts.</summary>
    /// <remarks>
    /// The value comes from an enum, so it is a literal chosen at compile time rather than
    /// anything a caller supplies - but it is still bound as a parameter rather than
    /// interpolated, so there is one rule for reaching SQL and no exceptions to it.
    /// </remarks>
    private static void AddMeasureParameter(ClickHouseCommand command, CountBy countBy)
    {
        var p = command.CreateParameter();
        p.ParameterName = "measure";
        p.Value = countBy switch
        {
            CountBy.Bindings => "bindings",
            CountBy.Subscribers => "subscribers",
            CountBy.Handsets => "handsets",
            _ => throw new ArgumentOutOfRangeException(nameof(countBy), countBy, "Unmapped measure."),
        };
        command.Parameters.Add(p);
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
