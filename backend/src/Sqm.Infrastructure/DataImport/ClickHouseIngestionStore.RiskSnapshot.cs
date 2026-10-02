using System.Globalization;
using Microsoft.Extensions.Logging;
using Sqm.Application.Risk;

namespace Sqm.Infrastructure.DataImport;

/// <summary>
/// The risk snapshot (analytics migration 024): per SIM and per IMEI, the windowed and all-time
/// measures the risk lists read, built one key-range chunk at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>The measures are the calibration's, statement for statement.</b> The distributions in ADR-014
/// were measured with these definitions on the real history (2026-10-02), so a threshold cut from them
/// means the same thing when the lists apply it.
/// </para>
/// <para>
/// <b>Linear and spillable.</b> Distinct counts are two GROUP BYs - pairs first, then the entity -
/// never a per-entity set inside one aggregate. Per-day maxima come from a sumMap over at most 30 day
/// keys per entity. The multi-number screen is a grace-hash join in buckets: in September the list
/// holds ~6M (day, SIM) pairs, which as an IN-set cannot spill and exceeded the limit when measured.
/// No statement relies on optimize_aggregation_in_order, which disabled spilling on the fold.
/// </para>
/// </remarks>
public sealed partial class ClickHouseIngestionStore : IRiskSnapshotStore
{
    [LoggerMessage(EventId = 3230, Level = LogLevel.Information,
        Message = "Risk snapshot run {RunId}: planned as of {AsOf}, {Chunks} chunk(s) per table")]
    private partial void LogRiskPlanned(ulong runId, DateOnly asOf, int chunks);

    [LoggerMessage(EventId = 3231, Level = LogLevel.Information,
        Message = "Risk snapshot run {RunId}: {Table} chunk {Chunk} written in {ElapsedMs} ms")]
    private partial void LogRiskChunk(ulong runId, RiskTable table, int chunk, long elapsedMs);

    [LoggerMessage(EventId = 3232, Level = LogLevel.Information,
        Message = "Risk snapshot run {RunId}: {Outcome}")]
    private partial void LogRiskRun(ulong runId, string outcome);

    private static readonly string[] RiskSnapshotTables = ["risk_sim_window", "risk_imei_window", "risk_imei_lifetime"];

    /// <summary>A countable IMEI: 14 digits, not fourteen zeros, not the shifted shape.</summary>
    /// <remarks>
    /// The shifted test is the feed-quality monitor's (ClickHouseIngestionStore.cs, dq_daily): a
    /// 14-digit IMEI ending in 0 whose TAC GSMA does not know, and which one leading digit turns into
    /// a GSMA TAC. The ten IN-sets share one subquery, which ClickHouse builds once.
    /// </remarks>
    private string Countable(string imei)
    {
        var tac = $"(SELECT tac FROM {_database}.tac)";
        var restored = string.Join(" OR ", Enumerable.Range(0, 10)
            .Select(d => string.Create(CultureInfo.InvariantCulture, $"concat('{d}', substring({imei}, 1, 7)) IN {tac}")));

        return $"(match({imei}, '^[0-9]{{14}}$') AND {imei} != '00000000000000' AND NOT "
            + $"(endsWith({imei}, '0') AND substring({imei}, 1, 8) NOT IN {tac} AND ({restored})))";
    }

    /// <inheritdoc />
    public async Task<bool> DeployedAsync(CancellationToken ct) =>
        await ScalarAsync($"EXISTS TABLE {_database}.risk_run", ct).ConfigureAwait(false) == 1;

    /// <inheritdoc />
    public async Task<RiskInputs> ReadInputsAsync(RiskOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var pending = await ScalarAsync(
            $"SELECT countIf(state = 'pending') FROM {_database}.binding_history_day FINAL", ct).ConfigureAwait(false) ?? 0;
        var missing = await ScalarAsync($"""
            SELECT count() FROM (
                SELECT DISTINCT partition FROM system.parts
                WHERE database = '{_database}' AND table = 'binding_event' AND active)
            WHERE toDate(partition) NOT IN (SELECT data_date FROM {_database}.binding_history_day FINAL WHERE state = 'done')
            """, ct).ConfigureAwait(false) ?? 0;

        var asOfDays = await ScalarAsync(
            $"SELECT toUInt32(max(data_date)) FROM {_database}.binding_history_day FINAL WHERE state = 'done'", ct)
            .ConfigureAwait(false) ?? 0;
        var asOf = DateOnly.FromDayNumber(DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber + (int)asOfDays);

        var tacVersion = (int)(await ScalarAsync(
            $"SELECT version_id FROM {_database}.tac_active FINAL ORDER BY activated_at DESC LIMIT 1", ct)
            .ConfigureAwait(false) ?? 0);

        var fingerprint = await ScalarUInt64Async($"""
            SELECT cityHash64(
                (SELECT groupArray((data_date, state, events, updated_at))
                   FROM (SELECT data_date, state, events, updated_at FROM {_database}.binding_history_day FINAL ORDER BY data_date)),
                (SELECT groupArray((data_date, computed_at, tac_version_id))
                   FROM (SELECT data_date, computed_at, tac_version_id FROM {_database}.dq_daily FINAL ORDER BY data_date)),
                {tacVersion},
                {RiskComputeOptions.DefinitionVersion},
                {options.Floors.SimImeis30}, {options.Floors.ImeiSims30}, {options.Floors.ImeiSimsEver}, {options.Floors.ImeiSimsNotRemoved})
            """, ct).ConfigureAwait(false);

        string? reason = null;
        if (asOfDays == 0)
        {
            reason = "the binding history holds no day yet";
        }
        else if (pending > 0)
        {
            reason = string.Create(CultureInfo.InvariantCulture, $"{pending} day(s) of the binding history are still being written");
        }
        else if (missing > 0)
        {
            reason = string.Create(CultureInfo.InvariantCulture, $"{missing} day(s) of the event log are not in the binding history yet");
        }

        return new RiskInputs(asOf, fingerprint, tacVersion, reason is null, reason);
    }

    /// <inheritdoc />
    public async Task<RiskRun?> LatestRunAsync(string state, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT run_id, as_of, fingerprint, tac_version_id, toString(state), chunks, done, note, updated_at
            FROM {_database}.risk_run FINAL
            WHERE state = '{state}'
            ORDER BY run_id DESC
            LIMIT 1
            """;
        command.CommandTimeout = _options.QueryTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new RiskRun(
            Convert.ToUInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            DateOnly.FromDateTime(reader.GetDateTime(1)),
            Convert.ToUInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
            Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
            reader.GetString(4),
            Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
            ((string[])reader.GetValue(6)).ToHashSet(StringComparer.Ordinal),
            reader.GetString(7),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc)));
    }

    /// <inheritdoc />
    public async Task<RiskRun> PlanRunAsync(RiskInputs inputs, RiskOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);

        var chunks = options.Compute.Chunks;
        var runId = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (from, _, _, to) = Windows(inputs.AsOf);
        var months = Months(from, to);
        var fractions = string.Join(", ", Enumerable.Range(1, chunks - 1)
            .Select(i => ((double)i / chunks).ToString("R", CultureInfo.InvariantCulture)));

        // Cut points fixed for the whole run, so chunks built on different idle ticks tile the key
        // space exactly. Integer bounds: a Float64 bound kept the primary key from pruning, and every
        // chunk read the whole window (measured 2026-10-02: 309M rows a chunk instead of ~46M).
        var simCuts = chunks == 1 ? "[]" : await StringAsync($"""
            SELECT toString(arrayMap(x -> toUInt64(x), quantiles({fractions})(imsi)))
            FROM {_database}.binding_history_by_imsi WHERE month IN ({months})
            """, ct).ConfigureAwait(false);
        var imeiCuts = chunks == 1 ? "[]" : await StringAsync($"""
            SELECT toString(arrayMap(x -> leftPad(toString(toUInt64(x)), 14, '0'), quantiles({fractions})(toUInt64OrZero(imei))))
            FROM {_database}.binding_history_by_imei WHERE month IN ({months})
            """, ct).ConfigureAwait(false);

        await ExecuteHttpAsync($"""
            INSERT INTO {_database}.risk_run
                (run_id, as_of, fingerprint, tac_version_id, state, chunks, sim_cuts, imei_cuts, done, note, updated_at)
            VALUES ({runId}, '{Iso(inputs.AsOf)}', {inputs.Fingerprint}, {inputs.TacVersionId}, 'running', {chunks},
                    {simCuts}, {imeiCuts}, [], '', now64(3))
            """, NoParameters, ct).ConfigureAwait(false);

        LogRiskPlanned(runId, inputs.AsOf, chunks);
        return new RiskRun(runId, inputs.AsOf, inputs.Fingerprint, inputs.TacVersionId, "running", chunks,
            new HashSet<string>(StringComparer.Ordinal), string.Empty, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<RiskRun> BuildChunkAsync(RiskRun run, RiskTable table, int chunk, RiskOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var target = Target(table);

        var range = table == RiskTable.SimWindow
            ? await RangeAsync(run, "sim_cuts", "imsi", chunk, numeric: true, ct).ConfigureAwait(false)
            : await RangeAsync(run, "imei_cuts", "imei", chunk, numeric: false, ct).ConfigureAwait(false);

        var sql = table switch
        {
            RiskTable.SimWindow => SimWindowSql(run, chunk, range, options.Floors.SimImeis30),
            RiskTable.ImeiWindow => ImeiWindowSql(run, chunk, range, options.Floors.ImeiSims30),
            _ => ImeiLifetimeSql(run, chunk, range, options.Floors.ImeiSimsEver, options.Floors.ImeiSimsNotRemoved),
        };

        var limits = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["max_execution_time"] = options.Compute.MaxStatementSeconds.ToString(CultureInfo.InvariantCulture),
            ["timeout_overflow_mode"] = "throw",
            ["join_algorithm"] = "grace_hash",
            ["grace_hash_join_initial_buckets"] = "16",
            ["max_bytes_in_join"] = "300000000",
        };

        await ExecuteHttpAsync(
            $"ALTER TABLE {_database}.{target} DROP PARTITION ({run.RunId}, {chunk})", NoParameters, ct).ConfigureAwait(false);
        await ExecuteHttpAsync(sql, limits, ct).ConfigureAwait(false);

        var done = new HashSet<string>(run.Done, StringComparer.Ordinal) { RiskRun.Key(table, chunk) };
        await WriteRunAsync(run, "running", done, run.Note, ct).ConfigureAwait(false);

        LogRiskChunk(run.RunId, table, chunk, started.ElapsedMilliseconds);
        return run with { Done = done, UpdatedAt = DateTimeOffset.UtcNow };
    }

    /// <inheritdoc />
    public async Task<string?> TryPublishAsync(RiskRun run, RiskOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);

        if (run.Next() is { } missing)
        {
            return $"chunk {missing.Chunk} of {missing.Table} is not written yet";
        }

        // Every planned chunk present, each exactly once in the tables, and no key twice - the check
        // that caught delivery 216 doubled by an orphaned INSERT in the marts.
        foreach (var (table, key) in new[] { ("risk_sim_window", "imsi"), ("risk_imei_window", "imei"), ("risk_imei_lifetime", "imei") })
        {
            var chunks = await ScalarAsync(
                $"SELECT uniqExact(chunk) FROM {_database}.{table} WHERE run_id = {run.RunId}", ct).ConfigureAwait(false) ?? 0;
            var duplicates = await ScalarAsync(
                $"SELECT count() - uniqExact({key}) FROM {_database}.{table} WHERE run_id = {run.RunId}", ct).ConfigureAwait(false) ?? 0;

            if (duplicates != 0)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{table} holds {duplicates} duplicate key(s)");
            }

            // A chunk can legitimately be empty (nothing above the floor), so fewer chunks with rows
            // than planned is not an error; more is.
            if (chunks > run.Chunks)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{table} holds {chunks} chunks, {run.Chunks} planned");
            }
        }

        var now = await ReadInputsAsync(options, ct).ConfigureAwait(false);
        if (now.Fingerprint != run.Fingerprint)
        {
            return "its inputs changed while it was being built";
        }

        await WriteRunAsync(run, "published", run.Done, "published after coverage, duplicate and input checks", ct).ConfigureAwait(false);
        LogRiskRun(run.RunId, "published");
        return null;
    }

    /// <inheritdoc />
    public async Task AbandonAsync(RiskRun run, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        await WriteRunAsync(run, "abandoned", run.Done, reason, ct).ConfigureAwait(false);
        LogRiskRun(run.RunId, "abandoned: " + reason);
    }

    /// <inheritdoc />
    public async Task FailAsync(RiskRun run, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        await WriteRunAsync(run, "failed", run.Done, reason, ct).ConfigureAwait(false);
        LogRiskRun(run.RunId, "failed: " + reason);
    }

    /// <inheritdoc />
    public async Task<int> DropOldRunsAsync(CancellationToken ct)
    {
        var keep = new HashSet<ulong>();
        var published = await IdsAsync(
            $"SELECT run_id FROM {_database}.risk_run FINAL WHERE state = 'published' ORDER BY run_id DESC LIMIT 2", ct).ConfigureAwait(false);
        keep.UnionWith(published);
        keep.UnionWith(await IdsAsync(
            $"SELECT run_id FROM {_database}.risk_run FINAL WHERE state = 'running'", ct).ConfigureAwait(false));

        var dropped = 0;
        foreach (var table in RiskSnapshotTables)
        {
            var partitions = await PairsAsync($"SELECT DISTINCT run_id, chunk FROM {_database}.{table}", ct).ConfigureAwait(false);
            foreach (var (runId, chunk) in partitions.Where(p => !keep.Contains(p.RunId)))
            {
                await ExecuteHttpAsync(
                    $"ALTER TABLE {_database}.{table} DROP PARTITION ({runId}, {chunk})", NoParameters, ct).ConfigureAwait(false);
                dropped++;
            }
        }

        return dropped;
    }

    // ------------------------------------------------------------------ statements

    private static (DateOnly From30, DateOnly From20, DateOnly From7, DateOnly To) Windows(DateOnly asOf) =>
        (asOf.AddDays(-29), asOf.AddDays(-19), asOf.AddDays(-6), asOf);

    private static string Months(DateOnly from, DateOnly to)
    {
        var months = new SortedSet<int>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            months.Add((d.Year * 100) + d.Month);
        }

        return string.Join(", ", months);
    }

    private static string Target(RiskTable table) => table switch
    {
        RiskTable.SimWindow => "risk_sim_window",
        RiskTable.ImeiWindow => "risk_imei_window",
        _ => "risk_imei_lifetime",
    };

    private string SimWindowSql(RiskRun run, int chunk, string range, int floor)
    {
        var (from30, from20, from7, to) = Windows(run.AsOf);
        return $"""
            INSERT INTO {_database}.risk_sim_window
                (run_id, chunk, imsi, imeis_30, imeis_30_raw, imeis_20, tacs_20, imeis_7, imeis_7_raw,
                 adds_30, adds_set_aside_30, add_days_30, max_imeis_one_day_30, max_day_30, top_tacs_20)
            SELECT {run.RunId}, {chunk}, imsi, imeis_30, imeis_30_raw, imeis_20, tacs_20, imeis_7, imeis_7_raw,
                   adds_30, adds_set_aside_30,
                   length(dm.1)                                              AS add_days_30,
                   if(empty(dm.2), 0, arrayMax(dm.2))                        AS max_imeis_one_day_30,
                   if(empty(dm.2), toDate(0), dm.1[indexOf(dm.2, arrayMax(dm.2))]) AS max_day_30,
                   top_tacs_20
            FROM (
                SELECT imsi,
                       countIf(c30)                                  AS imeis_30,
                       count()                                       AS imeis_30_raw,
                       countIf(c20)                                  AS imeis_20,
                       uniqExactIf(substring(imei, 1, 8), c20)       AS tacs_20,
                       countIf(c7)                                   AS imeis_7,
                       countIf(r7)                                   AS imeis_7_raw,
                       sum(adds)                                     AS adds_30,
                       sum(adds_aside)                               AS adds_set_aside_30,
                       sumMap(clean_days, arrayMap(x -> toUInt32(1), clean_days)) AS dm,
                       topKIf(5)(substring(imei, 1, 8), c20)         AS top_tacs_20
                FROM (
                    SELECT imsi, imei,
                           max(clean)                          AS c30,
                           max(clean AND d >= '{Iso(from20)}') AS c20,
                           max(clean AND d >= '{Iso(from7)}')  AS c7,
                           max(d >= '{Iso(from7)}')            AS r7,
                           count()                             AS adds,
                           countIf(NOT clean)                  AS adds_aside,
                           groupUniqArrayIf(d, clean)          AS clean_days
                    FROM (
                        SELECT h.imsi AS imsi, h.imei AS imei, h.d AS d,
                               {Countable("h.imei")} AND m.x = 0 AS clean
                        FROM (
                            SELECT imsi, imei, e.1 AS d
                            FROM {_database}.binding_history_by_imsi ARRAY JOIN events AS e
                            WHERE month IN ({Months(from30, to)}) AND {range}
                              AND e.3 = 1 AND e.1 BETWEEN '{Iso(from30)}' AND '{Iso(to)}' AND length(imei) = 14
                        ) AS h
                        LEFT JOIN (
                            SELECT data_date, imsi, toUInt8(1) AS x FROM {_database}.dq_multi_number_sim_day
                            WHERE data_date BETWEEN '{Iso(from30)}' AND '{Iso(to)}' AND {range}
                        ) AS m ON m.data_date = h.d AND m.imsi = h.imsi
                    )
                    GROUP BY imsi, imei
                )
                GROUP BY imsi
                HAVING imeis_30_raw >= {floor}
            )
            """;
    }

    private string ImeiWindowSql(RiskRun run, int chunk, string range, int floor)
    {
        var (from30, _, from7, to) = Windows(run.AsOf);
        return $"""
            INSERT INTO {_database}.risk_imei_window
                (run_id, chunk, imei, sims_30, sims_30_raw, sims_7, numbers_30, adds_30, adds_set_aside_30,
                 add_days_30, max_sims_one_day_30, max_day_30)
            SELECT {run.RunId}, {chunk}, imei, sims_30, sims_30_raw, sims_7, numbers_30, adds_30, adds_set_aside_30,
                   length(dm.1)                                              AS add_days_30,
                   if(empty(dm.2), 0, arrayMax(dm.2))                        AS max_sims_one_day_30,
                   if(empty(dm.2), toDate(0), dm.1[indexOf(dm.2, arrayMax(dm.2))]) AS max_day_30
            FROM (
                SELECT imei,
                       countIf(c30)                         AS sims_30,
                       count()                              AS sims_30_raw,
                       countIf(c7)                          AS sims_7,
                       length(groupUniqArrayArray(numbers)) AS numbers_30,
                       sum(adds)                            AS adds_30,
                       sum(adds_aside)                      AS adds_set_aside_30,
                       sumMap(clean_days, arrayMap(x -> toUInt32(1), clean_days)) AS dm
                FROM (
                    SELECT imei, imsi,
                           max(clean)                         AS c30,
                           max(clean AND d >= '{Iso(from7)}') AS c7,
                           count()                            AS adds,
                           countIf(NOT clean)                 AS adds_aside,
                           groupUniqArrayIf(msisdn, clean)    AS numbers,
                           groupUniqArrayIf(d, clean)         AS clean_days
                    FROM (
                        SELECT h.imei AS imei, h.imsi AS imsi, h.msisdn AS msisdn, h.d AS d, m.x = 0 AS clean
                        FROM (
                            SELECT imei, imsi, msisdn, e.1 AS d
                            FROM {_database}.binding_history_by_imei ARRAY JOIN events AS e
                            WHERE month IN ({Months(from30, to)}) AND {range}
                              AND e.3 = 1 AND e.1 BETWEEN '{Iso(from30)}' AND '{Iso(to)}' AND {Countable("imei")}
                        ) AS h
                        LEFT JOIN (
                            SELECT data_date, imsi, toUInt8(1) AS x FROM {_database}.dq_multi_number_sim_day
                            WHERE data_date BETWEEN '{Iso(from30)}' AND '{Iso(to)}'
                        ) AS m ON m.data_date = h.d AND m.imsi = h.imsi
                    )
                    GROUP BY imei, imsi
                )
                GROUP BY imei
                HAVING greatest(sims_30_raw, numbers_30) >= {floor}
            )
            """;
    }

    private string ImeiLifetimeSql(RiskRun run, int chunk, string range, int floorEver, int floorNotRemoved) => $"""
        INSERT INTO {_database}.risk_imei_lifetime
            (run_id, chunk, imei, sims_ever, numbers_ever, bindings, sims_not_removed_dated, sims_not_removed_dump)
        SELECT {run.RunId}, {chunk}, imei,
               count()                            AS sims_ever,
               length(groupUniqArrayArray(numbers)) AS numbers_ever,
               sum(bindings)                      AS bindings,
               countIf(dated)                     AS sims_not_removed_dated,
               countIf(dump AND NOT dated)        AS sims_not_removed_dump
        FROM (
            SELECT imei, imsi,
                   count()                                                   AS bindings,
                   max(active = 1 AND last_change_date IS NOT NULL)          AS dated,
                   max(active = 1 AND last_change_date IS NULL)              AS dump,
                   groupUniqArray(msisdn)                                    AS numbers
            FROM {_database}.binding_by_imei FINAL
            WHERE {range} AND {Countable("imei")}
            GROUP BY imei, imsi
        )
        GROUP BY imei
        HAVING sims_ever >= {floorEver} OR sims_not_removed_dated >= {floorNotRemoved}
        """;

    // ------------------------------------------------------------------ helpers

    private static readonly IReadOnlyDictionary<string, string> NoParameters =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The key range of one chunk: open at both ends, so keys outside the cut points are covered too.</summary>
    private async Task<string> RangeAsync(RiskRun run, string column, string key, int chunk, bool numeric, CancellationToken ct)
    {
        var lower = chunk == 0 ? null : await StringAsync(
            $"SELECT toString({column}[{chunk}]) FROM {_database}.risk_run FINAL WHERE run_id = {run.RunId}", ct).ConfigureAwait(false);
        var upper = chunk >= run.Chunks - 1 ? null : await StringAsync(
            $"SELECT toString({column}[{chunk + 1}]) FROM {_database}.risk_run FINAL WHERE run_id = {run.RunId}", ct).ConfigureAwait(false);

        string Literal(string v) => numeric ? v : $"'{v.Replace("'", string.Empty, StringComparison.Ordinal)}'";

        var parts = new List<string>(2);
        if (lower is not null) parts.Add($"{key} >= {Literal(lower)}");
        if (upper is not null) parts.Add($"{key} < {Literal(upper)}");
        return parts.Count == 0 ? "1" : string.Join(" AND ", parts);
    }

    private async Task WriteRunAsync(RiskRun run, string state, IReadOnlySet<string> done, string note, CancellationToken ct)
    {
        var doneList = "[" + string.Join(", ", done.Order(StringComparer.Ordinal).Select(d => $"'{d}'")) + "]";
        var safeNote = note.Replace("'", " ", StringComparison.Ordinal).Replace("\\", " ", StringComparison.Ordinal);

        await ExecuteHttpAsync($"""
            INSERT INTO {_database}.risk_run
                (run_id, as_of, fingerprint, tac_version_id, state, chunks, sim_cuts, imei_cuts, done, note, updated_at)
            SELECT run_id, as_of, fingerprint, tac_version_id, '{state}', chunks, sim_cuts, imei_cuts, {doneList}, '{safeNote}', now64(3)
            FROM {_database}.risk_run FINAL WHERE run_id = {run.RunId}
            """, NoParameters, ct).ConfigureAwait(false);
    }

    private async Task<string> StringAsync(string sql, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        return Convert.ToString(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async Task<ulong> ScalarUInt64Async(string sql, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        return Convert.ToUInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private async Task<List<ulong>> IdsAsync(string sql, CancellationToken ct)
    {
        var ids = new List<ulong>();
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(Convert.ToUInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
        }

        return ids;
    }

    private async Task<List<(ulong RunId, int Chunk)>> PairsAsync(string sql, CancellationToken ct)
    {
        var pairs = new List<(ulong, int)>();
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            pairs.Add((Convert.ToUInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                       Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)));
        }

        return pairs;
    }
}
