using System.Globalization;
using Sqm.Application.Risk;
using Sqm.Domain.Risk;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>The risk page's analyses: counts that name nobody.</summary>
public sealed partial class ClickHouseRiskReader
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RiskBucket>> DistributionAsync(
        RiskPublishedRun run, RiskRule rule, IReadOnlyList<long> edges, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(edges);

        var list = RiskEvaluation.ListOf(rule);
        var (value, raw, _, _) = Columns(rule);
        var parameters = Parameters(run, new RiskThreshold(0), 1);
        parameters["edges"] = "[" + string.Join(",", edges.Select(e => e.ToString(CultureInfo.InvariantCulture))) + "]";

        // A row's bucket is how many edges its value reaches; 0 means below the first edge. The raw
        // count is bucketed on its own value, so the two bars of a bucket count different entities.
        var source = list switch
        {
            RiskList.Sims => "SELECT * FROM risk_sim_window WHERE run_id = {run:UInt64}",
            RiskList.ImeiWindow => "SELECT * FROM risk_imei_window WHERE run_id = {run:UInt64}",
            RiskList.ImeiLifetime => "SELECT * FROM risk_imei_lifetime WHERE run_id = {run:UInt64}",
            _ => $"SELECT {NumberColumns} FROM risk_sim_change_day WHERE data_date BETWEEN {{from7:Date}} AND {{asOf:Date}} GROUP BY msisdn",
        };

        const string Edges = "{edges:Array(Int64)}";
        var rawBucket = raw is null ? "0" : $"arrayCount(e -> {raw} >= e, {Edges})";
        var result = await _query.ExecuteAsync($"""
            SELECT b, sum(clean), sum(raw) FROM (
                SELECT arrayJoin([(arrayCount(e -> {value} >= e, {Edges}), 1, 0),
                                  ({rawBucket}, 0, {(raw is null ? "0" : "1")})]) AS t,
                       t.1 AS b, t.2 AS clean, t.3 AS raw
                FROM ({source}))
            WHERE b > 0
            GROUP BY b ORDER BY b
            """, parameters, ct, Budget).ConfigureAwait(false);

        var counts = result.Rows.ToDictionary(
            r => ClickHouseJsonResult.Int32(r, 0),
            r => (Clean: ClickHouseJsonResult.Int64(r, 1), Raw: ClickHouseJsonResult.Int64(r, 2)));

        return [.. edges.Select((from, i) =>
        {
            var (clean, rawCount) = counts.GetValueOrDefault(i + 1);
            return new RiskBucket(from, i + 1 < edges.Count ? edges[i + 1] : null, clean, raw is null ? null : rawCount);
        })];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string DeviceType, long Entities)>> DeviceTypesOfListAsync(
        RiskPublishedRun run, RiskListQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(query);

        var list = RiskEvaluation.ListOf(query.Rule);
        if (list == RiskList.Numbers)
        {
            return [];
        }

        var (table, tac) = list switch
        {
            RiskList.Sims => ("risk_sim_window", "top_tacs_20[1]"),
            RiskList.ImeiWindow => ("risk_imei_window", "substring(imei, 1, 8)"),
            _ => ("risk_imei_lifetime", "substring(imei, 1, 8)"),
        };

        var parameters = Parameters(run, query.Threshold, query.MaxDefectShare);
        var result = await _query.ExecuteAsync($$"""
            SELECT coalesce(nullIf(t.deviceType, ''), 'Not in GSMA') AS type, count() AS entities
            FROM (SELECT {{tac}} AS tac FROM {{table}} WHERE run_id = {run:UInt64} AND {{Condition(query.Rule, query.View)}}) AS l
            LEFT JOIN (SELECT tac, deviceType FROM tac) AS t ON t.tac = l.tac
            GROUP BY type ORDER BY entities DESC, type
            SETTINGS join_use_nulls = 1
            """, parameters, ct, Budget).ConfigureAwait(false);

        return [.. result.Rows.Select(r => (ClickHouseJsonResult.Text(r, 0) ?? "Not in GSMA", ClickHouseJsonResult.Int64(r, 1)))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RiskChangeDay>> SimChangeDaysAsync(CancellationToken ct)
    {
        var result = await _query.ExecuteAsync("""
            SELECT data_date, count(), countIf(set_aside = 'multi_number'), countIf(set_aside = 'dq_unavailable')
            FROM risk_sim_change_day GROUP BY data_date ORDER BY data_date
            """, NoParameters, ct, Budget).ConfigureAwait(false);

        return [.. result.Rows.Select(r => new RiskChangeDay(
            ClickHouseJsonResult.Date(r, 0)!.Value, ClickHouseJsonResult.Int64(r, 1),
            ClickHouseJsonResult.Int64(r, 2), ClickHouseJsonResult.Int64(r, 3)))];
    }
}
