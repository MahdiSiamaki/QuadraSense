using Microsoft.Extensions.Logging;
using Sqm.Application.DataImport;

namespace Sqm.Infrastructure.DataImport;

/// <summary>
/// Phase 5's per-day, per-model counts (analytics migration 025): what new models and network age
/// are read from.
/// </summary>
/// <remarks>
/// Countable IMEIs only - the feed-quality test, as the risk measures use it - so the shifted IMEIs'
/// fake prefixes never appear as models. Pairs first, then the model: two linear GROUP BYs a day,
/// never a per-model set of IMEIs held in memory (that form exceeded the import's 1.2 GB on an
/// ordinary day, measured 2026-10-05).
/// </remarks>
public sealed partial class ClickHouseIngestionStore
{
    [LoggerMessage(EventId = 3240, Level = LogLevel.Information,
        Message = "Model day {BusinessDate}: {Models} model(s), {Imeis} handset(s)")]
    private partial void LogTacDay(DateOnly businessDate, long models, long imeis);

    /// <summary>The day the initial dump is written under.</summary>
    internal static readonly DateOnly DumpDay = DateOnly.FromDateTime(DateTime.UnixEpoch);

    /// <inheritdoc />
    public async Task<TacDayRefresh?> RefreshTacDayAsync(DateOnly businessDate, CancellationToken ct)
    {
        if (await ScalarAsync($"EXISTS TABLE {_database}.tac_day", ct).ConfigureAwait(false) != 1)
        {
            return null;
        }

        var source = $"SELECT substring(imei, 1, 8) AS tac, imei, imsi, label FROM {_database}.binding_event "
            + $"WHERE data_date = {{businessDate:Date}} AND {Countable("imei")}";

        await ExecuteBoundedAsync($"ALTER TABLE {_database}.tac_day DROP PARTITION '{Iso(businessDate)}'", businessDate, ct)
            .ConfigureAwait(false);
        await ExecuteBoundedAsync(TacDaySql("{businessDate:Date}", source, "countIf(label = 'add')"), businessDate, ct)
            .ConfigureAwait(false);

        return await TacDayTotalsAsync(businessDate, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TacDayRefresh?> RefreshTacDumpAsync(CancellationToken ct)
    {
        if (await ScalarAsync($"EXISTS TABLE {_database}.tac_day", ct).ConfigureAwait(false) != 1)
        {
            return null;
        }

        // The dump lists bindings with no dates; every model in it is "seen before the first file".
        var source = $"SELECT substring(imei, 1, 8) AS tac, imei, imsi, 'add' AS label FROM {_database}.binding_snapshot "
            + $"WHERE {Countable("imei")}";

        await ExecuteBoundedAsync($"ALTER TABLE {_database}.tac_day DROP PARTITION '{Iso(DumpDay)}'", DumpDay, ct)
            .ConfigureAwait(false);
        await ExecuteBoundedAsync(TacDaySql("toDate(0)", source, "toUInt32(0)"), DumpDay, ct).ConfigureAwait(false);

        return await TacDayTotalsAsync(DumpDay, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<DateOnly>> GetTacDaysAsync(CancellationToken ct)
    {
        var days = new HashSet<DateOnly>();
        if (await ScalarAsync($"EXISTS TABLE {_database}.tac_day", ct).ConfigureAwait(false) != 1)
        {
            return days;
        }

        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT data_date FROM {_database}.tac_day";
        command.CommandTimeout = _options.QueryTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            days.Add(DateOnly.FromDateTime(reader.GetDateTime(0)));
        }

        return days;
    }

    private string TacDaySql(string day, string source, string adds) => $"""
        INSERT INTO {_database}.tac_day (data_date, tac, imeis, sims, adds, computed_at)
        WITH day AS ({source})
        SELECT {day}, i.tac, i.imeis, s.sims, i.adds, now64(3)
        FROM (SELECT tac, count() AS imeis, sum(adds) AS adds
              FROM (SELECT tac, imei, {adds} AS adds FROM day GROUP BY tac, imei)
              GROUP BY tac) AS i
        LEFT JOIN (SELECT tac, count() AS sims FROM (SELECT tac, imsi FROM day GROUP BY tac, imsi) GROUP BY tac) AS s
            USING tac
        """;

    private async Task<TacDayRefresh> TacDayTotalsAsync(DateOnly day, CancellationToken ct)
    {
        var date = Iso(day);
        var models = await ScalarAsync(
            $"SELECT count() FROM {_database}.tac_day WHERE data_date = toDate('{date}')", ct).ConfigureAwait(false) ?? 0;
        var imeis = await ScalarAsync(
            $"SELECT sum(imeis) FROM {_database}.tac_day WHERE data_date = toDate('{date}')", ct).ConfigureAwait(false) ?? 0;

        LogTacDay(day, models, imeis);
        return new TacDayRefresh(models, imeis);
    }
}
