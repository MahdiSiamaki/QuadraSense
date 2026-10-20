using System.Data.Common;
using ClickHouse.Client.ADO;
using Sqm.Application.Abstractions;
using Sqm.Contracts.Dashboard;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>
/// Filtered dashboard figures, from the population marts rather than binding_current.
/// </summary>
/// <remarks>
/// <para>
/// Every dashboard filter - vendor, device type, OS, TAC, unknown devices - is a property of the
/// model (TAC), so a filtered view is the per-model rows that match it, added up for bindings and
/// merged for the distinct counts (<c>agg_tac_population</c>, migration 027), instead of
/// binding_current FINAL, which held 817 million row versions for 120 million bindings on
/// 2026-10-19 and took 100 s alone, 183 s on the dashboard - past the API's client timeout, so the
/// KPI row failed and was retried while the abandoned query ran on.
/// </para>
/// <para>
/// Merging states costs what they weigh - 1,005 MiB for a delivery - so one filter on its own reads
/// its finished row from <c>agg_filter_population</c> (migration 028) instead. Measured for the KPI
/// row on delivery 246: Smartphone 3.4 s merging 35,069 models' states, Samsung 2.7 s, a single TAC
/// 0.19 s. A combination still merges, and picks its models first
/// (<see cref="MatchingModels"/>), so the states of models it does not match are never read.
/// </para>
/// <para>
/// It serves the delivery the marts serve (<c>mart_ready</c>), the same one as the unfiltered
/// figures beside it. Until the mart has rows for that delivery - a refresh that predates migration
/// 027 - the raw path still answers, slowly but correctly. An MSISDN prefix is not a model property
/// and keeps the raw path; no dashboard widget sends one.
/// </para>
/// </remarks>
public sealed partial class ClickHouseAnalyticsStore
{
    /// <summary>The per-model rows of the served delivery, with what a filter reads beside them.</summary>
    private const string TacMartSource = """
        sqm.agg_tac_population AS b
        LEFT JOIN sqm.tac AS t ON t.tac = b.tac
        LEFT JOIN (SELECT * FROM sqm.tac_vendor_map FINAL) AS v ON v.raw_manufacturer = t.manufacturer
        """;

    private const string ServedSequence = "(SELECT max(seq) FROM sqm.mart_ready)";

    /// <summary>
    /// The served delivery's models a filter matches, chosen from the small columns before any state
    /// is read.
    /// </summary>
    /// <remarks>
    /// Filtering on the joined catalogue alone made ClickHouse read every model's states and discard
    /// the rest after the join: a filter matching nothing took 2.0 s, and 0.03 s like this. The
    /// clause binds the same parameters as the filter it wraps.
    /// </remarks>
    private static string MatchingModels(FilterBuilder f) => $"""
        b.tac IN (
            SELECT b.tac FROM {TacMartSource}
            WHERE b.seq = {ServedSequence} AND {f.WhereClause})
        """;

    /// <summary>Whether the per-filter mart holds the delivery the dashboard serves.</summary>
    private async Task<bool> FilterMartReadyAsync(CancellationToken ct)
    {
        const string Sql = $"""
            SELECT count() > 0 FROM sqm.agg_filter_population WHERE seq = {ServedSequence}
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, Sql);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// The KPI row for one filter on its own: its finished row, or zeros when nothing has the value.
    /// </summary>
    private async Task<KpiSummary> GetKpiFromFilterMartAsync(
        string dimension, string value, CancellationToken ct)
    {
        const string Sql = $$"""
            SELECT
                sum(bindings)                               AS active_bindings,
                sum(subscribers)                            AS distinct_subscribers,
                sum(sims)                                   AS distinct_sims,
                sum(handsets)                               AS distinct_devices,
                sum(unknown_device)                         AS unknown_device_bindings,
                sum(malformed_imei)                         AS malformed_imei_bindings,
                sum(tac_matched)                            AS tac_matched_bindings,
                if(sum(bindings) = 0, 0,
                   round(100.0 * sum(tac_matched) / sum(bindings), 3)) AS tac_coverage_pct,
                {{ServedSequence}}                          AS delivery_seq,
                (SELECT max(data_date) FROM sqm.agg_change_summary_daily AS d
                  WHERE d.seq = {{ServedSequence}})         AS delivery_date
            FROM sqm.agg_filter_population
            -- One row at most: the key is (seq, dimension, value). The sums only turn no row into zeros.
            WHERE seq = {{ServedSequence}} AND dimension = {dimension:String} AND value = {value:String}
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, Sql);
        AddParameter(command, "dimension", dimension);
        AddParameter(command, "value", value);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ReadKpi(reader)
            : new KpiSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, null);
    }

    private static void AddParameter(ClickHouseCommand command, string name, string value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }

    /// <summary>A KPI row, in the column order both population marts' queries return it.</summary>
    private static KpiSummary ReadKpi(DbDataReader reader) => new(
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

    /// <summary>Whether the per-model mart holds the delivery the dashboard serves.</summary>
    private async Task<bool> TacMartReadyAsync(CancellationToken ct)
    {
        const string Sql = $"""
            SELECT count() > 0 FROM sqm.agg_tac_population WHERE seq = {ServedSequence}
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, Sql);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>The filtered KPI row: the matching models' bindings added, their states merged.</summary>
    private async Task<KpiSummary> GetKpiFromTacMartAsync(DashboardFilter filter, CancellationToken ct)
    {
        var f = FilterBuilder.ForCurrentState(filter);

        var sql = $"""
            SELECT
                sum(b.bindings)                             AS active_bindings,
                uniqMerge(b.subscribers)                    AS distinct_subscribers,
                uniqMerge(b.sims)                           AS distinct_sims,
                uniqMerge(b.handsets)                       AS distinct_devices,
                sum(b.unknown_device)                       AS unknown_device_bindings,
                sum(b.malformed_imei)                       AS malformed_imei_bindings,
                sumIf(b.bindings, b.tac IN (SELECT tac FROM sqm.tac)) AS tac_matched_bindings,
                -- Zero coverage of zero bindings when the filter matches nothing, never NaN.
                if(sum(b.bindings) = 0, 0,
                   round(100.0 * sumIf(b.bindings, b.tac IN (SELECT tac FROM sqm.tac)) / sum(b.bindings), 3))
                                                            AS tac_coverage_pct,
                {ServedSequence}                            AS delivery_seq,
                (SELECT max(data_date) FROM sqm.agg_change_summary_daily AS d
                  WHERE d.seq = {ServedSequence})           AS delivery_date
            -- The models table alone, no catalogue joined beside the states: with the joins here,
            -- ClickHouse took the filtered models for the small side, built a hash table of their
            -- states, and Smartphone + Samsung took 8.9 s instead of 2.5 s (2026-10-19).
            FROM sqm.agg_tac_population AS b
            WHERE b.seq = {ServedSequence} AND {MatchingModels(f)}
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, sql);
        f.Bind(command);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ReadKpi(reader)
            : new KpiSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, null);
    }

    /// <summary>A filtered breakdown, grouped over the matching models' rows.</summary>
    /// <remarks>
    /// The same shape as the raw fallback - the filtered population as the denominator, taken with
    /// the same measure in the same pass (the empty grouping set), the unknown buckets labelled -
    /// so a share means what it meant there.
    /// </remarks>
    private async Task<IReadOnlyList<DimensionCount>> TopFromTacMartAsync(
        AnalyticsDimension dimension, DashboardFilter filter, int limit, CountBy countBy,
        CancellationToken ct)
    {
        var column = AnalyticsSchema.ColumnFor(dimension);
        var measure = AnalyticsSchema.TacMartMeasureFor(countBy);
        var f = FilterBuilder.ForCurrentState(filter);
        var take = FilterBuilder.ClampLimit(limit);

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
                    FROM {{TacMartSource}}
                    WHERE b.seq = {{ServedSequence}} AND {{MatchingModels(f)}}
                    GROUP BY GROUPING SETS ((k), ())
                )
            )
            WHERE is_total = 0
            ORDER BY n DESC LIMIT {{take}}
            -- The grouping key needs the catalogue joined; left as written, ClickHouse may take the
            -- filtered models for the small side and hash their states (the KPI row above: 8.9 s
            -- against 2.6 s with this).
            SETTINGS query_plan_join_swap_table = 'false'
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, sql);
        f.Bind(command);
        AddLabelParameters(command);

        return await ReadDimensionCountsAsync(command, ct).ConfigureAwait(false);
    }
}
