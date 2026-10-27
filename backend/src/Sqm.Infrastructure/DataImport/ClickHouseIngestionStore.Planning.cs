using System.Globalization;
using Microsoft.Extensions.Logging;
using Sqm.Application.Planning;
using Sqm.Domain.Devices;

namespace Sqm.Infrastructure.DataImport;

/// <summary>
/// The planning snapshot (analytics migration 030): per number, the 2G/3G sunset and eSIM classes of
/// its active handsets; per IoT device, the SIMs it has carried - built one key-range chunk at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>A number's class is decided by all its handsets at once</b>, from the radio profiles of
/// migration 029: one handset with LTE or 5G makes it ready; only when every handset is known and
/// 2G/3G-only is it exposed; anything in between that cannot be assessed - an unknown device, a TAC
/// the catalogue lacks, a model whose record lists no band, a shifted IMEI - makes it "not
/// assessable", with the reason, never exposed and never ready.
/// </para>
/// <para>
/// <b>Dual-SIM pairs</b> (<c>14-relationship-explorer.md</c>) settle one case: an IMEI whose own TAC
/// cannot be assessed, whose partner by the documented rule - the same TAC and a serial one apart, or
/// a TAC a hundred apart and the same serial - is active on the same number with a known profile.
/// Both are one handset's radios, so the handset takes its partner's profile. Galaxy A54's partner TAC
/// is not in the catalogue at all, which without this would leave every A54 number with both radios on
/// it "not assessable". Pairing never merges handsets in a count: handsets are IMEIs, as everywhere.
/// </para>
/// <para>
/// <b>Twice per chunk over the same range</b> of current state: once to classify each number, once to
/// count who holds which model under that class. Classifying needs every handset of a number together;
/// the breakdown needs each binding beside its number's class.
/// </para>
/// </remarks>
public sealed partial class ClickHouseIngestionStore : IPlanningSnapshotStore
{
    [LoggerMessage(EventId = 3240, Level = LogLevel.Information,
        Message = "Planning snapshot run {RunId}: planned as of delivery {Sequence}, {Chunks} chunk(s) per table")]
    private partial void LogPlanningPlanned(ulong runId, int sequence, int chunks);

    [LoggerMessage(EventId = 3241, Level = LogLevel.Information,
        Message = "Planning snapshot run {RunId}: {Table} chunk {Chunk} written in {ElapsedMs} ms")]
    private partial void LogPlanningChunk(ulong runId, PlanningTable table, int chunk, long elapsedMs);

    [LoggerMessage(EventId = 3242, Level = LogLevel.Information,
        Message = "Planning snapshot run {RunId}: {Outcome}")]
    private partial void LogPlanningRun(ulong runId, string outcome);

    private static readonly string[] PlanningTables = ["planning_handset_stage", "planning_number", "planning_breakdown", "planning_iot_device"];

    /// <inheritdoc />
    public async Task<bool> PlanningDeployedAsync(CancellationToken ct) =>
        await ScalarAsync($"EXISTS TABLE {_database}.planning_run", ct).ConfigureAwait(false) == 1
        && await ScalarAsync($"EXISTS TABLE {_database}.tac_radio_parsed", ct).ConfigureAwait(false) == 1;

    /// <inheritdoc />
    public async Task<PlanningInputs> ReadPlanningInputsAsync(CancellationToken ct)
    {
        var days = await GetBusinessDatesAsync(null, null, ct).ConfigureAwait(false);
        DateOnly? asOfDate = days.Count > 0 ? days[^1] : null;
        var sequence = asOfDate is { } latest
            ? await GetSequenceForDateAsync(latest, ct).ConfigureAwait(false) ?? 0
            : await GetMaxSequenceAsync(ct).ConfigureAwait(false);

        var tacVersion = (int)(await ScalarAsync(
            $"SELECT version_id FROM {_database}.tac_active FINAL ORDER BY activated_at DESC LIMIT 1", ct).ConfigureAwait(false) ?? 0);
        var parsed = await ScalarAsync(
            $"SELECT max(parser_version) FROM {_database}.tac_radio_parsed FINAL WHERE version_id = {tacVersion}", ct).ConfigureAwait(false) ?? 0;
        var pending = await ScalarAsync(
            $"SELECT countIf(state = 'pending') FROM {_database}.binding_history_day FINAL", ct).ConfigureAwait(false) ?? 0;

        var fingerprint = await ScalarUInt64Async($"""
            SELECT cityHash64(
                {sequence},
                (SELECT groupArray((data_date, state, events, updated_at))
                   FROM (SELECT data_date, state, events, updated_at FROM {_database}.binding_history_day FINAL ORDER BY data_date)),
                {tacVersion}, {parsed}, {PlanningOptions.DefinitionVersion})
            """, ct).ConfigureAwait(false);

        string? reason = null;
        if (tacVersion == 0)
        {
            reason = "no GSMA version is active";
        }
        else if (parsed != RadioProfileParser.Version)
        {
            reason = string.Create(CultureInfo.InvariantCulture, $"the radio profiles of TAC version {tacVersion} are not parsed by the current parser yet");
        }
        else if (pending > 0)
        {
            reason = string.Create(CultureInfo.InvariantCulture, $"{pending} day(s) of the binding history are still being written");
        }

        return new PlanningInputs(sequence, asOfDate, fingerprint, tacVersion, reason is null, reason);
    }

    /// <inheritdoc />
    public async Task<PlanningRun?> LatestPlanningRunAsync(string state, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT run_id, as_of_seq, as_of_date, fingerprint, tac_version_id, toString(state), chunks, done, note, updated_at
            FROM {_database}.planning_run FINAL
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

        return new PlanningRun(
            Convert.ToUInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? null : DateOnly.FromDateTime(reader.GetDateTime(2)),
            Convert.ToUInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
            Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture),
            reader.GetString(5),
            Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
            ((string[])reader.GetValue(7)).ToHashSet(StringComparer.Ordinal),
            reader.GetString(8),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc)));
    }

    /// <inheritdoc />
    public async Task<PlanningRun> PlanPlanningRunAsync(PlanningInputs inputs, PlanningOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);

        var chunks = options.Chunks;
        var runId = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fractions = string.Join(", ", Enumerable.Range(1, chunks - 1)
            .Select(i => ((double)i / chunks).ToString("R", CultureInfo.InvariantCulture)));

        // Integer bounds, fixed for the run, so chunks built on different idle ticks tile the key
        // space exactly (the risk snapshot found a Float64 bound kept the primary key from pruning).
        var msisdnCuts = chunks == 1 ? "[]" : await StringAsync(
            $"SELECT toString(arrayMap(x -> toUInt64(x), quantiles({fractions})(msisdn))) FROM {_database}.binding_current WHERE active = 1",
            ct).ConfigureAwait(false);
        var imeiCuts = chunks == 1 ? "[]" : await StringAsync($"""
            SELECT toString(arrayMap(x -> leftPad(toString(toUInt64(x)), 14, '0'), quantiles({fractions})(toUInt64OrZero(imei))))
            FROM {_database}.binding_by_imei
            """, ct).ConfigureAwait(false);

        var date = inputs.AsOfDate is { } d ? $"'{Iso(d)}'" : "NULL";
        await ExecuteHttpAsync($"""
            INSERT INTO {_database}.planning_run
                (run_id, as_of_seq, as_of_date, fingerprint, tac_version_id, parser_version, state, chunks,
                 msisdn_cuts, imei_cuts, done, note, updated_at)
            VALUES ({runId}, {inputs.AsOfSequence}, {date}, {inputs.Fingerprint}, {inputs.TacVersionId},
                    {RadioProfileParser.Version}, 'running', {chunks}, {msisdnCuts}, {imeiCuts}, [], '', now64(3))
            """, NoParameters, ct).ConfigureAwait(false);

        LogPlanningPlanned(runId, inputs.AsOfSequence, chunks);
        return new PlanningRun(runId, inputs.AsOfSequence, inputs.AsOfDate, inputs.Fingerprint, inputs.TacVersionId,
            "running", chunks, new HashSet<string>(StringComparer.Ordinal), string.Empty, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<PlanningRun> BuildPlanningChunkAsync(
        PlanningRun run, PlanningTable table, int chunk, PlanningOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var limits = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["max_execution_time"] = options.MaxStatementSeconds.ToString(CultureInfo.InvariantCulture),
            ["timeout_overflow_mode"] = "throw",
            ["join_algorithm"] = "grace_hash",
            ["grace_hash_join_initial_buckets"] = "16",
            ["max_bytes_in_join"] = "300000000",
        };

        if (table == PlanningTable.Numbers)
        {
            var range = await PlanningRangeAsync(run, "msisdn_cuts", "msisdn", chunk, numeric: true, ct).ConfigureAwait(false);
            foreach (var target in new[] { "planning_handset_stage", "planning_number", "planning_breakdown" })
            {
                await ExecuteHttpAsync(
                    $"ALTER TABLE {_database}.{target} DROP PARTITION ({run.RunId}, {chunk})", NoParameters, ct).ConfigureAwait(false);
            }

            await ExecuteHttpAsync(StageSql(run, chunk, range), limits, ct).ConfigureAwait(false);
            await ExecuteHttpAsync(NumbersSql(run, chunk), limits, ct).ConfigureAwait(false);
            await ExecuteHttpAsync(BreakdownSql(run, chunk), limits, ct).ConfigureAwait(false);
            await ExecuteHttpAsync(
                $"ALTER TABLE {_database}.planning_handset_stage DROP PARTITION ({run.RunId}, {chunk})", NoParameters, ct).ConfigureAwait(false);
        }
        else
        {
            var range = await PlanningRangeAsync(run, "imei_cuts", "imei", chunk, numeric: false, ct).ConfigureAwait(false);
            await ExecuteHttpAsync(
                $"ALTER TABLE {_database}.planning_iot_device DROP PARTITION ({run.RunId}, {chunk})", NoParameters, ct).ConfigureAwait(false);
            await ExecuteHttpAsync(IotDeviceSql(run, chunk, range), limits, ct).ConfigureAwait(false);
        }

        var done = new HashSet<string>(run.Done, StringComparer.Ordinal) { PlanningRun.Key(table, chunk) };
        await WritePlanningRunAsync(run, "running", done, run.Note, ct).ConfigureAwait(false);

        LogPlanningChunk(run.RunId, table, chunk, started.ElapsedMilliseconds);
        return run with { Done = done, UpdatedAt = DateTimeOffset.UtcNow };
    }

    /// <inheritdoc />
    public async Task<string?> TryPublishPlanningAsync(PlanningRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Next() is { } missing)
        {
            return $"chunk {missing.Chunk} of {missing.Table} is not written yet";
        }

        foreach (var (table, key) in new[]
                 {
                     ("planning_number", "msisdn"),
                     ("planning_iot_device", "imei"),
                     ("planning_breakdown", "(chunk, sunset, reason, esim, tac)"),
                 })
        {
            var duplicates = await ScalarAsync(
                $"SELECT count() - uniqExact({key}) FROM {_database}.{table} WHERE run_id = {run.RunId}", ct).ConfigureAwait(false) ?? 0;
            if (duplicates != 0)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{table} holds {duplicates} duplicate key(s)");
            }

            var chunks = await ScalarAsync(
                $"SELECT uniqExact(chunk) FROM {_database}.{table} WHERE run_id = {run.RunId}", ct).ConfigureAwait(false) ?? 0;
            if (chunks > run.Chunks)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{table} holds {chunks} chunks, {run.Chunks} planned");
            }
        }

        var now = await ReadPlanningInputsAsync(ct).ConfigureAwait(false);
        if (now.Fingerprint != run.Fingerprint)
        {
            return "its inputs changed while it was being built";
        }

        // The finished figures, kept beyond the run's own rows: the trend. Numbers and bindings are
        // exact - a number is in one chunk - and handsets are the merged estimate.
        var date = run.AsOfDate is { } d ? $"'{Iso(d)}'" : "NULL";
        await ExecuteHttpAsync($"""
            INSERT INTO {_database}.planning_summary (run_id, as_of_seq, as_of_date, measure, class, reason, numbers, bindings, handsets)
            SELECT {run.RunId}, {run.AsOfSequence}, {date}, n.measure, n.class, n.reason, n.numbers, n.bindings, coalesce(h.handsets, 0)
            FROM (
                SELECT 'sunset' AS measure, sunset AS class, reason, count() AS numbers, sum(bindings) AS bindings
                FROM {_database}.planning_number WHERE run_id = {run.RunId} GROUP BY sunset, reason
                UNION ALL
                SELECT 'esim', esim, 0, count(), sum(bindings)
                FROM {_database}.planning_number WHERE run_id = {run.RunId} GROUP BY esim
            ) AS n
            LEFT JOIN (
                SELECT 'sunset' AS measure, sunset AS class, reason, uniqMerge(handsets) AS handsets
                FROM {_database}.planning_breakdown WHERE run_id = {run.RunId} GROUP BY sunset, reason
                UNION ALL
                SELECT 'esim', esim, 0, uniqMerge(handsets)
                FROM {_database}.planning_breakdown WHERE run_id = {run.RunId} GROUP BY esim
            ) AS h ON h.measure = n.measure AND h.class = n.class AND h.reason = n.reason
            """, NoParameters, ct).ConfigureAwait(false);

        await WritePlanningRunAsync(run, "published", run.Done, "published after coverage, duplicate and input checks", ct).ConfigureAwait(false);
        LogPlanningRun(run.RunId, "published");
        return null;
    }

    /// <inheritdoc />
    public async Task EndPlanningRunAsync(PlanningRun run, string state, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        await WritePlanningRunAsync(run, state, run.Done, reason, ct).ConfigureAwait(false);
        LogPlanningRun(run.RunId, state + ": " + reason);
    }

    /// <inheritdoc />
    public async Task<int> DropOldPlanningRunsAsync(CancellationToken ct)
    {
        var keep = new HashSet<ulong>();
        keep.UnionWith(await IdsAsync(
            $"SELECT run_id FROM {_database}.planning_run FINAL WHERE state = 'published' ORDER BY run_id DESC LIMIT 2", ct).ConfigureAwait(false));
        keep.UnionWith(await IdsAsync(
            $"SELECT run_id FROM {_database}.planning_run FINAL WHERE state = 'running'", ct).ConfigureAwait(false));

        var dropped = 0;
        foreach (var table in PlanningTables)
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

    /// <summary>
    /// A handset's state, from its IMEI and its model's radio profile: 0-3 the profile's sunset class
    /// (0 no band listed, 1 LTE or NR, 2 LTE-M / NB-IoT only, 3 2G/3G only), 5 a shifted IMEI,
    /// 6 an unknown device (000000 or not 14 digits, fourteen zeros), 7 a TAC the catalogue lacks.
    /// </summary>
    private string HandsetState(string imei, string profileTac, string sunset) => $$"""
        multiIf({{Shifted(imei)}}, 5,
                NOT match({{imei}}, '^[0-9]{14}$') OR {{imei}} = '00000000000000', 6,
                {{profileTac}} = '', 7,
                {{sunset}})
        """;

    /// <summary>
    /// A chunk's handsets: one row per (number, IMEI) of its active bindings, with the model's profile.
    /// The one read of current state a chunk makes.
    /// </summary>
    private string StageSql(PlanningRun run, int chunk, string range) => $$"""
        INSERT INTO {{_database}}.planning_handset_stage (run_id, chunk, msisdn, imei, tac, state, esim, umts, iot, bindings)
        SELECT {{run.RunId}}, {{chunk}}, b.msisdn, b.imei, any(b.tac),
               toUInt8(any({{HandsetState("b.imei", "r.tac", "r.sunset")}})),
               toUInt8(any(if(r.tac = '', 2, r.esim))),
               any(r.umts),
               toUInt8(any(r.device_class = 'IoT / M2M')),
               toUInt32(count())
        FROM {{_database}}.binding_current AS b FINAL
        LEFT JOIN (SELECT tac, sunset, esim, umts, device_class FROM {{_database}}.tac_radio_active) AS r ON r.tac = b.tac
        WHERE b.active = 1 AND {{range}}
        GROUP BY b.msisdn, b.imei
        """;

    /// <summary>
    /// One row per number, from the chunk's handsets. A handset that cannot be assessed (state 0 or 7)
    /// takes the profile of its dual-SIM partner, found by a join on the number and the partner's IMEI:
    /// the same TAC and a serial one either side, or a TAC a hundred either side and the same serial.
    /// Then the number's classes follow from the whole set.
    /// </summary>
    private string NumbersSql(PlanningRun run, int chunk) => $$"""
        INSERT INTO {{_database}}.planning_number
            (run_id, chunk, msisdn, sunset, reason, esim, iot, gsm_only, handsets, bindings, paired, tacs)
        SELECT
            {{run.RunId}}, {{chunk}}, msisdn,
            multiIf(has(st, 1), 1, has(st, 2), 2, hasAny(st, [0, 5, 6, 7]), 4, 3)                  AS sunset,
            multiIf(sunset != 4, 0, has(st, 5), 1, has(st, 6), 2, has(st, 7), 3, 4)                  AS reason,
            multiIf(has(es, 1), 1, has(es, 2), 2, 0)                                                 AS esim,
            iot_any                                                                                  AS iot,
            toUInt8(sunset = 3 AND umts_any = 0)                                                     AS gsm_only,
            toUInt16(least(handset_count, 65535))                                                    AS handsets,
            toUInt16(least(binding_count, 65535))                                                    AS bindings,
            toUInt8(least(paired_count, 255))                                                        AS paired,
            if(sunset = 1, [], models)                                                               AS tacs
        FROM (
            SELECT h.msisdn AS msisdn,
                   groupUniqArray(if(p.found = 1, p.state, h.state))          AS st,
                   groupUniqArray(if(p.found = 1, p.esim, h.esim))            AS es,
                   toUInt8(max(if(p.found = 1, p.iot, h.iot)))                AS iot_any,
                   max(if(p.found = 1, p.umts, h.umts))                       AS umts_any,
                   count()                                                    AS handset_count,
                   sum(h.bindings)                                            AS binding_count,
                   countIf(p.found = 1)                                       AS paired_count,
                   groupUniqArray(64)(h.tac)                                  AS models
            FROM (
                SELECT msisdn, imei, tac, state, esim, umts, iot, bindings
                FROM {{_database}}.planning_handset_stage WHERE run_id = {{run.RunId}} AND chunk = {{chunk}}
            ) AS h
            LEFT JOIN (
                SELECT u.msisdn AS msisdn, u.imei AS imei, toUInt8(1) AS found,
                       min(k.state) AS state, if(max(k.esim = 1) = 1, toUInt8(1), min(k.esim)) AS esim,
                       max(k.umts) AS umts, max(k.iot) AS iot
                FROM (
                    SELECT msisdn, imei,
                           arrayJoin([
                               concat(substring(imei, 1, 8), leftPad(toString(toUInt32(substring(imei, 9, 6)) + 1), 6, '0')),
                               concat(substring(imei, 1, 8), leftPad(toString(toInt64(substring(imei, 9, 6)) - 1), 6, '0')),
                               concat(leftPad(toString(toUInt32(substring(imei, 1, 8)) + 100), 8, '0'), substring(imei, 9, 6)),
                               concat(leftPad(toString(toInt64(substring(imei, 1, 8)) - 100), 8, '0'), substring(imei, 9, 6))]) AS partner
                    FROM {{_database}}.planning_handset_stage
                    WHERE run_id = {{run.RunId}} AND chunk = {{chunk}} AND state IN (0, 7)
                ) AS u
                INNER JOIN (
                    SELECT msisdn, imei, state, esim, umts, iot
                    FROM {{_database}}.planning_handset_stage
                    WHERE run_id = {{run.RunId}} AND chunk = {{chunk}} AND state IN (1, 2, 3)
                ) AS k ON k.msisdn = u.msisdn AND k.imei = u.partner
                GROUP BY u.msisdn, u.imei
            ) AS p ON p.msisdn = h.msisdn AND p.imei = h.imei
            GROUP BY h.msisdn
        )
        """;

    /// <summary>Who holds which model, under each number class: the breakdown by vendor, model and type.</summary>
    private string BreakdownSql(PlanningRun run, int chunk) => $"""
        INSERT INTO {_database}.planning_breakdown (run_id, chunk, sunset, reason, esim, tac, numbers, bindings, handsets)
        SELECT {run.RunId}, {chunk}, n.sunset, n.reason, n.esim, s.tac,
               uniqState(s.msisdn), sum(s.bindings), uniqStateIf(s.imei, length(s.imei) = 14)
        FROM (
            SELECT msisdn, imei, tac, bindings FROM {_database}.planning_handset_stage
            WHERE run_id = {run.RunId} AND chunk = {chunk}
        ) AS s
        INNER JOIN (
            SELECT msisdn, sunset, reason, esim FROM {_database}.planning_number WHERE run_id = {run.RunId} AND chunk = {chunk}
        ) AS n ON n.msisdn = s.msisdn
        GROUP BY n.sunset, n.reason, n.esim, s.tac
        """;

    /// <summary>
    /// Per IoT / M2M device: the SIMs and numbers its bindings have had, from the binding history, and
    /// those active now, from current state. Countable IMEIs only - a shifted IMEI is not a device.
    /// </summary>
    private string IotDeviceSql(PlanningRun run, int chunk, string range)
    {
        var since = (run.AsOfDate ?? new DateOnly(2026, 1, 25)).AddDays(-89);
        return $"""
            INSERT INTO {_database}.planning_iot_device
                (run_id, chunk, imei, tac, sims_ever, numbers_ever, sims_active, numbers_active, sims_90, first_date, in_dump)
            SELECT {run.RunId}, {chunk}, h.imei, substring(h.imei, 1, 8),
                   h.sims_ever, h.numbers_ever, coalesce(c.sims_active, 0), coalesce(c.numbers_active, 0),
                   h.sims_90, h.first_date, h.in_dump
            FROM (
                SELECT imei,
                       uniqExact(imsi)                        AS sims_ever,
                       uniqExact(msisdn)                      AS numbers_ever,
                       uniqExactIf(imsi, first >= '{Iso(since)}') AS sims_90,
                       min(first)                             AS first_date,
                       max(dump)                              AS in_dump
                FROM (
                    SELECT imei, msisdn, imsi, min(first_date) AS first, max(in_dump) AS dump
                    FROM {_database}.binding_history_by_imei
                    WHERE {range} AND substring(imei, 1, 8) IN (SELECT tac FROM {_database}.tac_radio_active WHERE device_class = 'IoT / M2M')
                    GROUP BY imei, msisdn, imsi
                )
                WHERE {Countable("imei")}
                GROUP BY imei
            ) AS h
            LEFT JOIN (
                SELECT imei, uniqExactIf(imsi, active = 1) AS sims_active, uniqExactIf(msisdn, active = 1) AS numbers_active
                FROM {_database}.binding_by_imei FINAL
                WHERE {range} AND substring(imei, 1, 8) IN (SELECT tac FROM {_database}.tac_radio_active WHERE device_class = 'IoT / M2M')
                GROUP BY imei
            ) AS c ON c.imei = h.imei
            """;
    }

    // ------------------------------------------------------------------ helpers

    private async Task<string> PlanningRangeAsync(PlanningRun run, string column, string key, int chunk, bool numeric, CancellationToken ct)
    {
        var lower = chunk == 0 ? null : await StringAsync(
            $"SELECT toString({column}[{chunk}]) FROM {_database}.planning_run FINAL WHERE run_id = {run.RunId}", ct).ConfigureAwait(false);
        var upper = chunk >= run.Chunks - 1 ? null : await StringAsync(
            $"SELECT toString({column}[{chunk + 1}]) FROM {_database}.planning_run FINAL WHERE run_id = {run.RunId}", ct).ConfigureAwait(false);

        string Literal(string v) => numeric ? v : $"'{v.Replace("'", string.Empty, StringComparison.Ordinal)}'";

        var parts = new List<string>(2);
        if (lower is not null) parts.Add($"{key} >= {Literal(lower)}");
        if (upper is not null) parts.Add($"{key} < {Literal(upper)}");
        return parts.Count == 0 ? "1" : string.Join(" AND ", parts);
    }

    private async Task WritePlanningRunAsync(PlanningRun run, string state, IReadOnlySet<string> done, string note, CancellationToken ct)
    {
        var doneList = "[" + string.Join(", ", done.Order(StringComparer.Ordinal).Select(d => $"'{d}'")) + "]";
        var safeNote = note.Replace("'", " ", StringComparison.Ordinal).Replace("\\", " ", StringComparison.Ordinal);

        await ExecuteHttpAsync($"""
            INSERT INTO {_database}.planning_run
                (run_id, as_of_seq, as_of_date, fingerprint, tac_version_id, parser_version, state, chunks,
                 msisdn_cuts, imei_cuts, done, note, updated_at)
            SELECT run_id, as_of_seq, as_of_date, fingerprint, tac_version_id, parser_version, '{state}', chunks,
                   msisdn_cuts, imei_cuts, {doneList}, '{safeNote}', now64(3)
            FROM {_database}.planning_run FINAL WHERE run_id = {run.RunId}
            """, NoParameters, ct).ConfigureAwait(false);
    }
}
