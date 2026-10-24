using Microsoft.Extensions.Logging;

namespace Sqm.Infrastructure.DataImport;

/// <summary>
/// The initial dump: the baseline every daily file folds onto.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Until 2026-10-24 no code loaded the dump. On the first machine it went in
/// by hand, with one SQL statement over ClickHouse's <c>file()</c>
/// (<c>tools/benchmark/clickhouse/run.sh</c>), and migration 013 later only created
/// <c>binding_snapshot</c>. The Import Center refuses it - its name is a range, not a day - so the
/// first deployment away from that machine had no way to start.
/// </para>
/// <para>
/// <b>Raw, as it was.</b> Every row, typed (a malformed number fails the insert) and nothing else: no
/// row is validated away, because the first machine's numbers - the 125,939,523 bindings, the
/// data-quality categories counted over them - were computed from exactly that. The dump's columns are
/// matched by header name, so its order (imei, imsi, msisdn) is not assumed.
/// </para>
/// </remarks>
public sealed partial class ClickHouseIngestionStore
{
    [LoggerMessage(EventId = 3250, Level = LogLevel.Information,
        Message = "Loaded the initial dump: {Rows} row(s) into binding_snapshot in {ElapsedMs} ms")]
    private partial void LogDumpLoaded(long rows, long elapsedMs);

    [LoggerMessage(EventId = 3251, Level = LogLevel.Information,
        Message = "Seeded current state from the dump: {Rows} row(s) in {ElapsedMs} ms")]
    private partial void LogDumpSeeded(long rows, long elapsedMs);

    /// <inheritdoc />
    public async Task<long> LoadDumpAsync(Stream csv, Action<long>? onBytesRead, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(csv);

        var insert = $$"""
            INSERT INTO {{_database}}.binding_snapshot (msisdn, imsi, imei)
            SELECT msisdn, imsi, imei
            FROM input('imei String, imsi UInt64, msisdn UInt64')
            FORMAT CSVWithNames
            """;

        // The daily load's settings, for the same reasons (LoadDailyEventsAsync).
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["query"] = insert,
            ["database"] = _database,
            ["max_memory_usage"] = "3000000000",
            ["max_insert_block_size"] = "1048576",
            ["insert_deduplicate"] = "0",
            ["input_format_allow_errors_num"] = "0",
        };

        var body = onBytesRead is null ? csv : new CountingStream(csv, onBytesRead);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var rows = await InsertCsvAsync(query, body, "the initial dump", ct).ConfigureAwait(false);

        started.Stop();
        LogDumpLoaded(rows, started.ElapsedMilliseconds);
        return rows;
    }

    /// <inheritdoc />
    public async Task<long> SeedCurrentFromDumpAsync(CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        // What a binding seen only in the dump looks like on the first machine: active, change
        // sequence 0, no change date (read from its binding_current, 2026-10-24).
        await ExecuteHttpAsync(
            $"""
            INSERT INTO {_database}.binding_current
                (msisdn, imsi, imei, active, last_change_seq, last_change_date)
            SELECT msisdn, imsi, imei, 1, 0, NULL FROM {_database}.binding_snapshot
            """,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ct).ConfigureAwait(false);

        var rows = await ScalarAsync($"SELECT count() FROM {_database}.binding_current", ct)
            .ConfigureAwait(false) ?? 0;

        started.Stop();
        LogDumpSeeded(rows, started.ElapsedMilliseconds);
        return rows;
    }

    /// <inheritdoc />
    public async Task ClearDumpAsync(CancellationToken ct)
    {
        foreach (var table in new[] { "binding_snapshot", "binding_current", "binding_by_imsi", "binding_by_imei" })
        {
            await ExecuteHttpAsync(
                $"TRUNCATE TABLE IF EXISTS {_database}.{table}",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ct).ConfigureAwait(false);
        }
    }
}
