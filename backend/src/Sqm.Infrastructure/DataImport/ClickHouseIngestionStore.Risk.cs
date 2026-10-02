using Microsoft.Extensions.Logging;
using Sqm.Application.DataImport;

namespace Sqm.Infrastructure.DataImport;

/// <summary>
/// The per-day half of Phase 4's risk signals: each day's SIM changes, kept as rows
/// (analytics migration 023).
/// </summary>
/// <remarks>
/// <para>
/// <b>The dashboard's definition, to the row.</b> The condition is the one
/// <c>agg_sim_change_daily</c> counts - a number with a remove carrying one IMSI and an add carrying
/// an IMSI not also removed that day - and the same two linear GROUP BYs compute it, never arrays
/// filtered against arrays (that form needed memory in the square of a number's day and failed
/// 2026-08-06 five times). A day's rows therefore reconcile with the chart's count for that day.
/// </para>
/// <para>
/// <b>Screened, not filtered.</b> A change involving a SIM the feed listed under several numbers that
/// day is written with <c>set_aside = 'multi_number'</c>: counted on the data-quality side, never
/// listed as behaviour, and never silently lost.
/// </para>
/// </remarks>
public sealed partial class ClickHouseIngestionStore
{
    [LoggerMessage(EventId = 3220, Level = LogLevel.Information,
        Message = "Risk day {BusinessDate}: {Changes} SIM changes, {SetAside} set aside, quality measured {QualityMeasured}")]
    private partial void LogRiskDay(DateOnly businessDate, long changes, long setAside, bool qualityMeasured);

    /// <inheritdoc />
    public async Task<RiskDayRefresh?> RefreshRiskDayAsync(DateOnly businessDate, CancellationToken ct)
    {
        if (await ScalarAsync($"EXISTS TABLE {_database}.risk_sim_change_day", ct).ConfigureAwait(false) != 1)
        {
            return null;
        }

        var date = Iso(businessDate);

        // The screen needs the day's feed-quality rows. Without them every change is kept as
        // 'dq_unavailable' rather than passed as clean: an unscreened change is not evidence.
        var qualityDeployed = await ScalarAsync($"EXISTS TABLE {_database}.dq_daily", ct).ConfigureAwait(false) == 1;
        var qualityMeasured = qualityDeployed && await ScalarAsync(
            $"SELECT count() FROM {_database}.dq_daily FINAL WHERE data_date = toDate('{date}')", ct)
            .ConfigureAwait(false) > 0;

        var multi = qualityMeasured
            ? $"imsi IN (SELECT imsi FROM {_database}.dq_multi_number_sim_day WHERE data_date = {{businessDate:Date}})"
            : "0";

        var setAside = qualityMeasured
            ? "if(max(multi) = 1, 'multi_number', 'none')"
            : "'dq_unavailable'";

        string[] statements =
        [
            $"ALTER TABLE {_database}.risk_sim_change_day DROP PARTITION '{date}'",

            $$"""
            INSERT INTO {{_database}}.risk_sim_change_day
                (data_date, msisdn, new_imsis, old_imsis, new_count, old_count, new_imei, old_imei, set_aside, computed_at)
            SELECT {businessDate:Date}, msisdn,
                   groupArrayIf(5)(imsi, added AND NOT removed),
                   groupArrayIf(5)(imsi, removed),
                   countIf(added AND NOT removed),
                   countIf(removed),
                   anyIf(add_imei, added AND NOT removed),
                   anyIf(remove_imei, removed),
                   {{setAside}},
                   now64(3)
            FROM (
                SELECT msisdn, imsi,
                       max(label = 'add')            AS added,
                       max(label = 'remove')         AS removed,
                       anyIf(imei, label = 'add')    AS add_imei,
                       anyIf(imei, label = 'remove') AS remove_imei,
                       {{multi}}                      AS multi
                FROM {{_database}}.binding_event
                WHERE data_date = {businessDate:Date}
                GROUP BY msisdn, imsi
            )
            GROUP BY msisdn
            HAVING countIf(removed) > 0 AND countIf(added AND NOT removed) > 0
            """,
        ];

        foreach (var sql in statements)
        {
            await ExecuteBoundedAsync(sql, businessDate, ct).ConfigureAwait(false);
        }

        var changes = await ScalarAsync(
            $"SELECT count() FROM {_database}.risk_sim_change_day WHERE data_date = toDate('{date}')", ct)
            .ConfigureAwait(false) ?? 0;
        var setAsideCount = await ScalarAsync(
            $"SELECT countIf(set_aside != 'none') FROM {_database}.risk_sim_change_day WHERE data_date = toDate('{date}')", ct)
            .ConfigureAwait(false) ?? 0;

        LogRiskDay(businessDate, changes, setAsideCount, qualityMeasured);
        return new RiskDayRefresh(changes, setAsideCount, qualityMeasured);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<DateOnly>> GetRiskDaysAsync(CancellationToken ct)
    {
        var days = new HashSet<DateOnly>();

        if (await ScalarAsync($"EXISTS TABLE {_database}.risk_sim_change_day", ct).ConfigureAwait(false) != 1)
        {
            return days;
        }

        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT data_date FROM {_database}.risk_sim_change_day";
        command.CommandTimeout = _options.QueryTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            days.Add(DateOnly.FromDateTime(reader.GetDateTime(0)));
        }

        return days;
    }
}
