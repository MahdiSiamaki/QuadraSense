using System.Globalization;
using Microsoft.Extensions.Options;
using Sqm.Application.Planning;
using Sqm.Contracts.Planning;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>Reads the published planning snapshot (analytics migration 030) and the per-model marts beside it.</summary>
/// <remarks>
/// <para>
/// <b>Exact where the unit allows.</b> An unfiltered view's numbers and bindings come from the run's
/// finished summary, where every number was counted once. Narrowed to a vendor, model or device type,
/// a view counts the numbers holding such a handset, merged from the per-model uniq() states -
/// estimated, and the response says so.
/// </para>
/// <para>
/// <b>Two kinds of history, kept apart.</b> The per-number trend has a point for every published run,
/// from the first. The per-model trend reads <c>agg_device_model</c> - every delivery it holds - under
/// the profiles of the active GSMA version: binding counts only, because distinct counts do not add up
/// across models.
/// </para>
/// </remarks>
public sealed class ClickHousePlanningReader : IPlanningReader
{
    private static readonly QueryBudget Budget = new(MaxRowsToRead: 200_000_000, MaxExecutionSeconds: 60);

    private static readonly IReadOnlyDictionary<string, string> NoParameters =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly ClickHouseJsonQuery _query;

    /// <summary>Creates the reader over the analytics connection.</summary>
    public ClickHousePlanningReader(IOptions<ClickHouseOptions> options, IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _query = new ClickHouseJsonQuery(options.Value.ConnectionString, httpClientFactory);
    }

    // ------------------------------------------------------------------ the classes

    /// <summary>The sunset classes of planning_number.sunset, in display order.</summary>
    public static readonly IReadOnlyList<(int Code, string Key, string Label)> SunsetClasses =
    [
        (1, "ready-lte", "Ready: LTE or 5G"),
        (2, "ready-lpwan", "Ready: LTE-M / NB-IoT only"),
        (3, "exposed", "Exposed: 2G/3G only"),
        (4, "not-assessable", "Not assessable"),
    ];

    /// <summary>Why a number is not assessable, planning_number.reason.</summary>
    public static readonly IReadOnlyList<(int Code, string Key, string Label)> Reasons =
    [
        (1, "shifted-imei", "A shifted IMEI (feed defect)"),
        (2, "unknown-device", "An unknown device (000000 or malformed IMEI)"),
        (3, "unregistered-tac", "A TAC the GSMA catalogue does not hold"),
        (4, "no-bands", "A model whose record lists no band"),
    ];

    /// <summary>The eSIM classes of planning_number.esim.</summary>
    public static readonly IReadOnlyList<(int Code, string Key, string Label)> EsimClasses =
    [
        (1, "capable", "An eSIM-capable handset"),
        (0, "not-capable", "No eSIM-capable handset, every one known"),
        (2, "not-stated", "Not stated for at least one handset"),
    ];

    // ------------------------------------------------------------------ status

    /// <inheritdoc />
    public async Task<PlanningStatusResponse> GetStatusAsync(CancellationToken ct)
    {
        var deployed = await _query.ExecuteAsync(
            "SELECT count() FROM system.tables WHERE database = currentDatabase() AND name IN ('planning_run', 'tac_radio_parsed')",
            NoParameters, ct).ConfigureAwait(false);
        if (ClickHouseJsonResult.Int64(deployed.Rows[0], 0) < 2)
        {
            return new PlanningStatusResponse(false, [], null, null, null);
        }

        var runs = await RunsAsync(ct).ConfigureAwait(false);

        var running = await _query.ExecuteAsync("""
            SELECT run_id, as_of_seq, toString(as_of_date), length(done), chunks * 2
            FROM planning_run FINAL WHERE state = 'running' ORDER BY run_id DESC LIMIT 1
            """, NoParameters, ct).ConfigureAwait(false);
        var progress = running.Rows.Count == 0
            ? null
            : new PlanningProgress(
                ClickHouseJsonResult.UInt64(running.Rows[0], 0), ClickHouseJsonResult.Int32(running.Rows[0], 1),
                DateText(ClickHouseJsonResult.Text(running.Rows[0], 2)),
                ClickHouseJsonResult.Int32(running.Rows[0], 3), ClickHouseJsonResult.Int32(running.Rows[0], 4));

        var catalogue = await _query.ExecuteAsync("""
            SELECT p.version_id, p.parser_version, p.rows, p.unparsed_tacs,
                   (SELECT countIf(positionCaseInsensitive(bandDetails, 'volte') > 0) FROM tac)
            FROM tac_radio_parsed AS p FINAL
            WHERE p.version_id = (SELECT version_id FROM tac_active FINAL ORDER BY activated_at DESC LIMIT 1)
            """, NoParameters, ct).ConfigureAwait(false);
        var info = catalogue.Rows.Count == 0
            ? null
            : new PlanningCatalogue(
                ClickHouseJsonResult.Int32(catalogue.Rows[0], 0), ClickHouseJsonResult.Int32(catalogue.Rows[0], 1),
                ClickHouseJsonResult.Int64(catalogue.Rows[0], 2), ClickHouseJsonResult.Int64(catalogue.Rows[0], 3),
                ClickHouseJsonResult.Int64(catalogue.Rows[0], 4));

        // Stale when a newer delivery than the newest run's has landed: the run then describes the
        // network as it was, which the page says rather than presenting it as today.
        string? stale = null;
        if (runs.Count > 0)
        {
            var latest = await _query.ExecuteAsync(
                "SELECT max(toDate(partition)) FROM system.parts WHERE database = currentDatabase() AND table = 'binding_event' AND active",
                NoParameters, ct).ConfigureAwait(false);
            var through = ClickHouseJsonResult.Date(latest.Rows[0], 0);
            if (through is { Year: > 1970 } day && runs[0].AsOfDate is { } asOf
                && day > DateOnly.ParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            {
                stale = $"The data now runs to {day:yyyy-MM-dd}; these figures describe {asOf}. The next run is built while the import worker is idle.";
            }
        }

        return new PlanningStatusResponse(true, runs, progress, stale, info);
    }

    // ------------------------------------------------------------------ N1

    /// <inheritdoc />
    public async Task<SunsetResponse?> GetSunsetAsync(ulong? runId, PlanningFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (await RunAsync(runId, ct).ConfigureAwait(false) is not { } run)
        {
            return null;
        }

        var (classRows, total) = await ClassesAsync(run, "sunset", SunsetClasses, filter, ct).ConfigureAwait(false);
        var notAssessable = await ReasonsAsync(run, filter, total, ct).ConfigureAwait(false);

        long gsmOnly = 0, iot = 0, paired = 0;
        if (run.HasNumbers)
        {
            var parameters = FilterParameters(filter);
            parameters["run"] = Id(run);
            var counts = await _query.ExecuteAsync($$"""
                SELECT countIf(sunset = 3 AND gsm_only = 1), countIf(sunset = 3 AND iot = 1), sum(paired)
                FROM planning_number
                WHERE run_id = {run:UInt64} {{(filter.Any ? $"AND hasAny(tacs, ({MatchingTacs}))" : string.Empty)}}
                """, parameters, ct, Budget).ConfigureAwait(false);
            gsmOnly = ClickHouseJsonResult.Int64(counts.Rows[0], 0);
            iot = ClickHouseJsonResult.Int64(counts.Rows[0], 1);
            paired = ClickHouseJsonResult.Int64(counts.Rows[0], 2);
        }

        var trend = filter.Any ? [] : await TrendAsync("sunset", SunsetClasses, ct).ConfigureAwait(false);
        var modelTrend = await ModelTrendAsync(
            "multiIf(r.tac = '', 'not-assessable', r.sunset = 1, 'ready-lte', r.sunset = 2, 'ready-lpwan', r.sunset = 3, 'exposed', 'not-assessable')",
            filter, ct).ConfigureAwait(false);

        IReadOnlyList<PlanningNoData> noData =
        [
            new("VoLTE",
                "The GSMA catalogue has no VoLTE field: its IMS columns describe emergency calling, and the band text "
                + "mentions VoLTE for a handful of models. A handset with LTE is not shown to have VoLTE, so voice after a "
                + "3G switch-off cannot be assessed from this data."),
        ];

        return new SunsetResponse(run, filter.Any, total, classRows, notAssessable, gsmOnly, iot, paired, trend, modelTrend, noData);
    }

    // ------------------------------------------------------------------ N3

    /// <inheritdoc />
    public async Task<EsimResponse?> GetEsimAsync(ulong? runId, PlanningFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (await RunAsync(runId, ct).ConfigureAwait(false) is not { } run)
        {
            return null;
        }

        var (classRows, total) = await ClassesAsync(run, "esim", EsimClasses, filter, ct).ConfigureAwait(false);
        var trend = filter.Any ? [] : await TrendAsync("esim", EsimClasses, ct).ConfigureAwait(false);
        var modelTrend = await ModelTrendAsync(
            "multiIf(r.tac = '', 'not-stated', r.esim = 1, 'capable', r.esim = 0, 'not-capable', 'not-stated')",
            filter, ct).ConfigureAwait(false);

        IReadOnlyList<PlanningNoData> noData =
        [
            new("eSIM-capable handset on a physical SIM",
                "Nothing in the operator's files says whether a SIM is physical or an eSIM, so the numbers that could move to "
                + "an eSIM cannot be told from those that already have one."),
        ];

        return new EsimResponse(run, filter.Any, total, classRows, trend, modelTrend, noData);
    }

    // ------------------------------------------------------------------ N5

    /// <inheritdoc />
    public async Task<IotResponse?> GetIotAsync(ulong? runId, PlanningFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (await RunAsync(runId, ct).ConfigureAwait(false) is not { } run)
        {
            return null;
        }

        var parameters = FilterParameters(filter);
        parameters["seq"] = run.AsOfSequence.ToString(CultureInfo.InvariantCulture);
        var match = filter.Any ? $"AND p.tac IN ({MatchingTacs})" : string.Empty;

        // The population of the run's delivery, per model, as the dashboard's filtered views read it.
        var population = $$"""
            SELECT p.tac AS tac, p.bindings AS bindings, p.subscribers AS subscribers, p.handsets AS handsets,
                   r.device_class AS device_class, r.sunset AS sunset, r.umts AS umts, r.gsm AS gsm, t.deviceType AS device_type
            FROM agg_tac_population AS p
            INNER JOIN tac_radio_active AS r ON r.tac = p.tac
            LEFT JOIN tac AS t ON t.tac = p.tac
            WHERE p.seq = {seq:UInt16} AND r.device_class = 'IoT / M2M' {{match}}
            """;

        var devices = await _query.ExecuteAsync($"""
            SELECT uniqMerge(subscribers), sum(bindings), uniqMerge(handsets)
            FROM ({population})
            """, parameters, ct, Budget).ConfigureAwait(false);
        var all = await _query.ExecuteAsync(
            "SELECT uniqMerge(subscribers) FROM agg_tac_population WHERE seq = {seq:UInt16}", parameters, ct, Budget).ConfigureAwait(false);
        var totalNumbers = ClickHouseJsonResult.Int64(all.Rows[0], 0);

        var deviceRow = new PlanningClassRow(
            "iot", "IoT / M2M devices",
            ClickHouseJsonResult.Int64(devices.Rows[0], 0), ClickHouseJsonResult.Int64(devices.Rows[0], 1),
            ClickHouseJsonResult.Int64(devices.Rows[0], 2),
            Percent(ClickHouseJsonResult.Int64(devices.Rows[0], 0), totalNumbers));

        var byType = await GroupedAsync($"""
            SELECT device_type, device_type, '', uniqMerge(subscribers) AS n, sum(bindings), uniqMerge(handsets)
            FROM ({population}) GROUP BY device_type ORDER BY n DESC
            """, parameters, ct).ConfigureAwait(false);

        var exposure = await GroupedAsync($"""
            SELECT k, multiIf(k = '2g-only', 'GSM (2G) only', k = '2g-3g', '2G and 3G, no LTE', k = 'lpwan', 'LTE-M / NB-IoT only',
                              k = 'lte', 'LTE or 5G', 'Not stated: no band listed'),
                   '', uniqMerge(subscribers) AS n, sum(bindings), uniqMerge(handsets)
            FROM (SELECT *, multiIf(sunset = 3 AND umts = 0, '2g-only', sunset = 3, '2g-3g', sunset = 2, 'lpwan', sunset = 1, 'lte', 'unknown') AS k
                  FROM ({population}))
            GROUP BY k ORDER BY n DESC
            """, parameters, ct).ConfigureAwait(false);

        IReadOnlyList<PlanningBreakdownRow> rotation = [];
        if (run.HasNumbers)
        {
            var iotParameters = FilterParameters(filter);
            iotParameters["run"] = Id(run);
            var tacMatch = filter.Any ? $"AND tac IN ({MatchingTacs})" : string.Empty;
            rotation = await GroupedAsync($$"""
                SELECT k, k, '', count() AS devices, sum(numbers_active), toUInt64(0)
                FROM (SELECT multiIf(sims_ever <= 1, 1, sims_ever = 2, 2, sims_ever <= 5, 3, sims_ever <= 10, 4, sims_ever <= 50, 5, 6) AS i,
                             ['1 SIM', '2 SIMs', '3-5 SIMs', '6-10 SIMs', '11-50 SIMs', 'more than 50 SIMs'][i] AS k,
                             numbers_active
                      FROM planning_iot_device WHERE run_id = {run:UInt64} {{tacMatch}})
                GROUP BY k ORDER BY min(i)
                """, iotParameters, ct).ConfigureAwait(false);
        }

        // What no class can place: unknown devices (no TAC) and TACs the catalogue does not hold.
        var unclassified = await _query.ExecuteAsync("""
            SELECT uniqMerge(subscribers), sum(bindings), uniqMerge(handsets)
            FROM agg_tac_population
            WHERE seq = {seq:UInt16} AND (tac = '' OR tac NOT IN (SELECT tac FROM tac_radio_active))
            """, parameters, ct, Budget).ConfigureAwait(false);
        var notClassified = new PlanningClassRow(
            "not-classified", "Not classifiable: unknown devices and TACs the catalogue lacks",
            ClickHouseJsonResult.Int64(unclassified.Rows[0], 0), ClickHouseJsonResult.Int64(unclassified.Rows[0], 1),
            ClickHouseJsonResult.Int64(unclassified.Rows[0], 2),
            Percent(ClickHouseJsonResult.Int64(unclassified.Rows[0], 0), totalNumbers));

        IReadOnlyList<PlanningNoData> noData =
        [
            new("Trackers",
                "The GSMA catalogue has no tracker device type. Modules, modems, IoT devices, routers and vehicle units are "
                + "classed as IoT / M2M; a tracker sold as one of those is counted there, and cannot be told apart."),
        ];

        return new IotResponse(run, deviceRow, byType, exposure, rotation, notClassified, noData);
    }

    // ------------------------------------------------------------------ breakdown and lists

    /// <inheritdoc />
    public async Task<PlanningBreakdownResponse?> GetBreakdownAsync(
        ulong? runId, string view, string planningClass, string by, PlanningFilter filter, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (await RunAsync(runId, ct).ConfigureAwait(false) is not { } run)
        {
            return null;
        }

        var key = by switch
        {
            "vendor" => "coalesce(nullIf(v.vendor_canonical, ''), nullIf(t.manufacturer, ''), '(unknown TAC)')",
            "model" => "coalesce(nullIf(t.marketingName, ''), '(unknown TAC)')",
            "deviceType" => "coalesce(nullIf(t.deviceType, ''), '(unknown TAC)')",
            _ => "b.tac",
        };
        var label = by == "tac" ? "coalesce(nullIf(t.marketingName, ''), if(b.tac = '', '(unknown device)', '(unknown TAC)'))" : "k";
        var tac = by is "tac" or "model" ? "any(b.tac)" : "''";

        var parameters = FilterParameters(filter);
        parameters["run"] = Id(run);
        parameters["seq"] = run.AsOfSequence.ToString(CultureInfo.InvariantCulture);
        parameters["limit"] = Math.Clamp(limit, 1, 500).ToString(CultureInfo.InvariantCulture);
        var match = filter.Any ? $"AND b.tac IN ({MatchingTacs})" : string.Empty;

        string source;
        if (view == "iot")
        {
            source = """
                SELECT p.tac AS tac, p.subscribers AS numbers, p.bindings AS bindings, p.handsets AS handsets
                FROM agg_tac_population AS p INNER JOIN tac_radio_active AS r ON r.tac = p.tac
                WHERE p.seq = {seq:UInt16} AND r.device_class = 'IoT / M2M'
                """;
        }
        else
        {
            if (!run.HasNumbers)
            {
                return new PlanningBreakdownResponse(run, view, planningClass, by, [], 0);
            }

            var classes = view == "esim" ? EsimClasses : SunsetClasses;
            if (classes.FirstOrDefault(c => c.Key == planningClass) is not { Key: not null } chosen)
            {
                return new PlanningBreakdownResponse(run, view, planningClass, by, [], 0);
            }

            var column = view == "esim" ? "esim" : "sunset";
            source = $$"""
                SELECT tac, numbers, bindings, handsets FROM planning_breakdown
                WHERE run_id = {run:UInt64} AND {{column}} = {{chosen.Code}}
                """;
        }

        var rows = await _query.ExecuteAsync($$"""
            SELECT {{key}} AS k, {{label}} AS l, {{tac}}, uniqMerge(b.numbers) AS n, sum(b.bindings), uniqMerge(b.handsets),
                   count() OVER () AS total
            FROM ({{source}}) AS b
            LEFT JOIN tac AS t ON t.tac = b.tac
            LEFT JOIN (SELECT * FROM tac_vendor_map FINAL) AS v ON v.raw_manufacturer = t.manufacturer
            WHERE 1 {{match}}
            GROUP BY k
            ORDER BY n DESC, k
            LIMIT {limit:UInt32}
            """, parameters, ct, Budget).ConfigureAwait(false);

        var list = rows.Rows.Select(r => new PlanningBreakdownRow(
            ClickHouseJsonResult.Text(r, 0) ?? string.Empty,
            ClickHouseJsonResult.Text(r, 1) ?? string.Empty,
            ClickHouseJsonResult.NullIfEmpty(r, 2),
            ClickHouseJsonResult.Int64(r, 3), ClickHouseJsonResult.Int64(r, 4), ClickHouseJsonResult.Int64(r, 5))).ToList();
        var total = rows.Rows.Count == 0 ? 0 : ClickHouseJsonResult.Int64(rows.Rows[0], 6);

        return new PlanningBreakdownResponse(run, view, planningClass, by, list, total);
    }

    /// <inheritdoc />
    public async Task<(PlanningRunInfo Run, IReadOnlyList<PlanningNumberRead> Rows, long Total)?> ListNumbersAsync(
        ulong? runId, string view, string planningClass, string? reason, PlanningFilter filter, int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (await RunAsync(runId, ct).ConfigureAwait(false) is not { HasNumbers: true } run)
        {
            return null;
        }

        if (NumbersCondition(view, planningClass, reason, filter) is not { } condition)
        {
            return (run, [], 0);
        }

        var parameters = FilterParameters(filter);
        parameters["run"] = Id(run);
        parameters["limit"] = pageSize.ToString(CultureInfo.InvariantCulture);
        parameters["offset"] = ((long)(page - 1) * pageSize).ToString(CultureInfo.InvariantCulture);

        var rows = await _query.ExecuteAsync($$"""
            WITH (SELECT (groupArray(tac), groupArray(marketingName)) FROM tac) AS names
            SELECT msisdn, reason, handsets, bindings,
                   arrayMap(x -> if(x = '', '(unknown device)', transform(x, names.1, names.2, concat('TAC ', x))), tacs),
                   count() OVER () AS total
            FROM planning_number
            WHERE run_id = {run:UInt64} AND {{condition}}
            ORDER BY msisdn
            LIMIT {limit:UInt32} OFFSET {offset:UInt64}
            """, parameters, ct, Budget).ConfigureAwait(false);

        var list = rows.Rows.Select(r => new PlanningNumberRead(
            ClickHouseJsonResult.UInt64(r, 0),
            ClickHouseJsonResult.Int32(r, 1),
            ClickHouseJsonResult.Int32(r, 2),
            ClickHouseJsonResult.Int32(r, 3),
            r[4].EnumerateArray().Select(m => m.GetString() ?? string.Empty).Distinct(StringComparer.Ordinal).ToList())).ToList();
        var total = rows.Rows.Count == 0 ? 0 : ClickHouseJsonResult.Int64(rows.Rows[0], 5);

        return (run, list, total);
    }

    /// <inheritdoc />
    public async Task<long?> CountNumbersAsync(
        ulong runId, string view, string planningClass, string? reason, PlanningFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (await RunAsync(runId, ct).ConfigureAwait(false) is not { HasNumbers: true } run)
        {
            return null;
        }

        if (NumbersCondition(view, planningClass, reason, filter) is not { } condition)
        {
            return 0;
        }

        var parameters = FilterParameters(filter);
        parameters["run"] = Id(run);
        var result = await _query.ExecuteAsync(
            $"SELECT count() FROM planning_number WHERE run_id = {{run:UInt64}} AND {condition}", parameters, ct, Budget)
            .ConfigureAwait(false);
        return ClickHouseJsonResult.Int64(result.Rows[0], 0);
    }

    /// <summary>A campaign list may take minutes to stream; it reads one class of one run, in key order.</summary>
    private static readonly QueryBudget StreamBudget = new(MaxRowsToRead: 200_000_000, MaxExecutionSeconds: 1800);

    /// <inheritdoc />
    public async Task<long> StreamNumbersAsync(
        ulong runId, string view, string planningClass, string? reason, PlanningFilter filter,
        Func<PlanningNumberRead, ValueTask> onRow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(onRow);

        if (NumbersCondition(view, planningClass, reason, filter) is not { } condition)
        {
            return 0;
        }

        var parameters = FilterParameters(filter);
        parameters["run"] = runId.ToString(CultureInfo.InvariantCulture);

        // Models joined with a separator no marketing name holds, then split here; a TSV field
        // escapes only the backslash, tab and newline, which no name holds either.
        return await _query.StreamLinesAsync($$"""
            WITH (SELECT (groupArray(tac), groupArray(marketingName)) FROM tac) AS names
            SELECT msisdn, reason, handsets, bindings,
                   arrayStringConcat(arrayDistinct(arrayMap(x -> if(x = '', '(unknown device)', transform(x, names.1, names.2, concat('TAC ', x))), tacs)), '\x1f')
            FROM planning_number
            WHERE run_id = {run:UInt64} AND {{condition}}
            ORDER BY msisdn
            """, parameters, line =>
            {
                var f = line.Split('\t');
                return onRow(new PlanningNumberRead(
                    ulong.Parse(f[0], CultureInfo.InvariantCulture),
                    int.Parse(f[1], CultureInfo.InvariantCulture),
                    int.Parse(f[2], CultureInfo.InvariantCulture),
                    int.Parse(f[3], CultureInfo.InvariantCulture),
                    f.Length > 4 && f[4].Length > 0 ? f[4].Split('\x1f') : []));
            }, ct, StreamBudget).ConfigureAwait(false);
    }

    /// <summary>The condition that selects one class's numbers, or null when the class is not one of the view's.</summary>
    private static string? NumbersCondition(string view, string planningClass, string? reason, PlanningFilter filter)
    {
        var classes = view == "esim" ? EsimClasses : SunsetClasses;
        if (classes.FirstOrDefault(c => c.Key == planningClass) is not { Key: not null } chosen)
        {
            return null;
        }

        var condition = $"{(view == "esim" ? "esim" : "sunset")} = {chosen.Code}";
        if (view != "esim" && reason is not null && Reasons.FirstOrDefault(r => r.Key == reason) is { Key: not null } why)
        {
            condition += $" AND reason = {why.Code}";
        }

        if (filter.Any)
        {
            condition += $" AND hasAny(tacs, ({MatchingTacs}))";
        }

        return condition;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The TACs a filter selects, compared as the dashboard's filters compare them.</summary>
    private const string MatchingTacs = """
        SELECT t.tac FROM tac AS t
        LEFT JOIN (SELECT * FROM tac_vendor_map FINAL) AS v ON v.raw_manufacturer = t.manufacturer
        WHERE ({vendor:String} = '' OR coalesce(nullIf(v.vendor_canonical, ''), t.manufacturer) = {vendor:String})
          AND ({model:String} = '' OR t.marketingName = {model:String})
          AND ({type:String} = '' OR t.deviceType = {type:String})
        """;

    private static Dictionary<string, string> FilterParameters(PlanningFilter filter) => new(StringComparer.Ordinal)
    {
        ["vendor"] = filter.Vendor?.Trim() ?? string.Empty,
        ["model"] = filter.Model?.Trim() ?? string.Empty,
        ["type"] = filter.DeviceType?.Trim() ?? string.Empty,
    };

    private static string Id(PlanningRunInfo run) => run.RunId.ToString(CultureInfo.InvariantCulture);

    private static double Percent(long part, long whole) => whole == 0 ? 0 : Math.Round(100.0 * part / whole, 2);

    private static string? DateText(string? value) =>
        string.IsNullOrEmpty(value) || value.StartsWith("1970", StringComparison.Ordinal) ? null : value;

    private async Task<List<PlanningRunInfo>> RunsAsync(CancellationToken ct)
    {
        // Which runs still hold their per-number rows, from the partition list rather than the 75M-row
        // table: the partition of a (run_id, chunk) key reads "(run, chunk)".
        var parts = await _query.ExecuteAsync("""
            SELECT DISTINCT partition FROM system.parts
            WHERE database = currentDatabase() AND table = 'planning_number' AND active
            """, NoParameters, ct).ConfigureAwait(false);
        var held = parts.Rows
            .Select(r => (ClickHouseJsonResult.Text(r, 0) ?? string.Empty).Trim('(', ')').Split(',')[0])
            .Select(s => ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .ToHashSet();

        var result = await _query.ExecuteAsync("""
            SELECT run_id, as_of_seq, toString(as_of_date), toString(updated_at)
            FROM planning_run FINAL
            WHERE state = 'published'
            ORDER BY run_id DESC
            """, NoParameters, ct).ConfigureAwait(false);

        return [.. result.Rows.Select(r => new PlanningRunInfo(
            ClickHouseJsonResult.UInt64(r, 0), ClickHouseJsonResult.Int32(r, 1), DateText(ClickHouseJsonResult.Text(r, 2)),
            ClickHouseJsonResult.Text(r, 3) ?? string.Empty, held.Contains(ClickHouseJsonResult.UInt64(r, 0))))];
    }

    private async Task<PlanningRunInfo?> RunAsync(ulong? runId, CancellationToken ct)
    {
        var runs = await RunsAsync(ct).ConfigureAwait(false);
        return runId is { } id ? runs.FirstOrDefault(r => r.RunId == id) : runs.FirstOrDefault();
    }

    /// <summary>A view's classes for one run: exact from the summary, or merged from the breakdown when filtered.</summary>
    private async Task<(List<PlanningClassRow> Rows, long Total)> ClassesAsync(
        PlanningRunInfo run, string measure, IReadOnlyList<(int Code, string Key, string Label)> classes, PlanningFilter filter, CancellationToken ct)
    {
        var parameters = FilterParameters(filter);
        parameters["run"] = Id(run);
        parameters["measure"] = measure;
        var column = measure == "esim" ? "esim" : "sunset";

        var result = filter.Any
            ? await _query.ExecuteAsync($$"""
                SELECT {{column}}, uniqMerge(numbers), sum(bindings), uniqMerge(handsets)
                FROM planning_breakdown
                WHERE run_id = {run:UInt64} AND tac IN ({{MatchingTacs}})
                GROUP BY {{column}}
                """, parameters, ct, Budget).ConfigureAwait(false)
            : await _query.ExecuteAsync("""
                SELECT class, sum(numbers), sum(bindings), sum(handsets)
                FROM planning_summary FINAL
                WHERE run_id = {run:UInt64} AND measure = {measure:String}
                GROUP BY class
                """, parameters, ct, Budget).ConfigureAwait(false);

        var byClass = result.Rows.ToDictionary(
            r => ClickHouseJsonResult.Int32(r, 0),
            r => (Numbers: ClickHouseJsonResult.Int64(r, 1), Bindings: ClickHouseJsonResult.Int64(r, 2), Handsets: ClickHouseJsonResult.Int64(r, 3)));

        long total;
        if (filter.Any)
        {
            var all = await _query.ExecuteAsync($$"""
                SELECT uniqMerge(numbers) FROM planning_breakdown WHERE run_id = {run:UInt64} AND tac IN ({{MatchingTacs}})
                """, parameters, ct, Budget).ConfigureAwait(false);
            total = ClickHouseJsonResult.Int64(all.Rows[0], 0);
        }
        else
        {
            total = byClass.Values.Sum(v => v.Numbers);
        }

        var rows = classes.Select(c =>
        {
            var v = byClass.GetValueOrDefault(c.Code);
            return new PlanningClassRow(c.Key, c.Label, v.Numbers, v.Bindings, v.Handsets, Percent(v.Numbers, total));
        }).ToList();

        return (rows, total);
    }

    private async Task<List<PlanningClassRow>> ReasonsAsync(PlanningRunInfo run, PlanningFilter filter, long total, CancellationToken ct)
    {
        var parameters = FilterParameters(filter);
        parameters["run"] = Id(run);

        var result = filter.Any
            ? await _query.ExecuteAsync($$"""
                SELECT reason, uniqMerge(numbers), sum(bindings), uniqMerge(handsets)
                FROM planning_breakdown WHERE run_id = {run:UInt64} AND sunset = 4 AND tac IN ({{MatchingTacs}})
                GROUP BY reason
                """, parameters, ct, Budget).ConfigureAwait(false)
            : await _query.ExecuteAsync("""
                SELECT reason, sum(numbers), sum(bindings), sum(handsets)
                FROM planning_summary FINAL WHERE run_id = {run:UInt64} AND measure = 'sunset' AND class = 4
                GROUP BY reason
                """, parameters, ct, Budget).ConfigureAwait(false);

        var byReason = result.Rows.ToDictionary(
            r => ClickHouseJsonResult.Int32(r, 0),
            r => (Numbers: ClickHouseJsonResult.Int64(r, 1), Bindings: ClickHouseJsonResult.Int64(r, 2), Handsets: ClickHouseJsonResult.Int64(r, 3)));

        return [.. Reasons.Select(r =>
        {
            var v = byReason.GetValueOrDefault(r.Code);
            return new PlanningClassRow(r.Key, r.Label, v.Numbers, v.Bindings, v.Handsets, Percent(v.Numbers, total));
        })];
    }

    private async Task<List<PlanningTrendPoint>> TrendAsync(
        string measure, IReadOnlyList<(int Code, string Key, string Label)> classes, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["measure"] = measure };
        var result = await _query.ExecuteAsync("""
            SELECT s.as_of_seq, toString(s.as_of_date), s.class, sum(s.numbers), sum(s.bindings)
            FROM planning_summary AS s FINAL
            WHERE s.measure = {measure:String}
              AND s.run_id IN (SELECT run_id FROM planning_run FINAL WHERE state = 'published')
            GROUP BY s.run_id, s.as_of_seq, s.as_of_date, s.class
            ORDER BY s.as_of_date, s.as_of_seq, s.class
            """, parameters, ct, Budget).ConfigureAwait(false);

        var keys = classes.ToDictionary(c => c.Code, c => c.Key);
        return [.. result.Rows.Select(r => new PlanningTrendPoint(
            ClickHouseJsonResult.Int32(r, 0), DateText(ClickHouseJsonResult.Text(r, 1)),
            keys.GetValueOrDefault(ClickHouseJsonResult.Int32(r, 2), "other"),
            ClickHouseJsonResult.Int64(r, 3), ClickHouseJsonResult.Int64(r, 4)))];
    }

    /// <summary>Bindings per model class at every delivery agg_device_model holds, under today's profiles.</summary>
    private async Task<List<PlanningModelTrendPoint>> ModelTrendAsync(string classExpression, PlanningFilter filter, CancellationToken ct)
    {
        var parameters = FilterParameters(filter);
        var match = filter.Any ? $"AND d.tac IN ({MatchingTacs})" : string.Empty;

        var result = await _query.ExecuteAsync($$"""
            SELECT d.seq, toString(any(e.data_date)), {{classExpression}} AS k, sum(d.bindings)
            FROM agg_device_model AS d
            LEFT JOIN (SELECT tac, sunset, esim FROM tac_radio_active) AS r ON r.tac = d.tac
            LEFT JOIN (SELECT seq, min(data_date) AS data_date FROM agg_change_summary_daily GROUP BY seq) AS e ON e.seq = d.seq
            WHERE d.seq IN (SELECT seq FROM mart_ready FINAL) {{match}}
            GROUP BY d.seq, k
            ORDER BY d.seq, k
            """, parameters, ct, Budget).ConfigureAwait(false);

        return [.. result.Rows.Select(r => new PlanningModelTrendPoint(
            ClickHouseJsonResult.Int32(r, 0), DateText(ClickHouseJsonResult.Text(r, 1)),
            ClickHouseJsonResult.Text(r, 2) ?? string.Empty, ClickHouseJsonResult.Int64(r, 3)))];
    }

    private async Task<List<PlanningBreakdownRow>> GroupedAsync(
        string sql, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var result = await _query.ExecuteAsync(sql, parameters, ct, Budget).ConfigureAwait(false);
        return [.. result.Rows.Select(r => new PlanningBreakdownRow(
            ClickHouseJsonResult.Text(r, 0) ?? string.Empty,
            ClickHouseJsonResult.Text(r, 1) ?? string.Empty,
            ClickHouseJsonResult.NullIfEmpty(r, 2),
            ClickHouseJsonResult.Int64(r, 3), ClickHouseJsonResult.Int64(r, 4), ClickHouseJsonResult.Int64(r, 5)))];
    }

}
