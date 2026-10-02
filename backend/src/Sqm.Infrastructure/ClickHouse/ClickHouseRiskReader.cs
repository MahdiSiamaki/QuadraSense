using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sqm.Application.Risk;
using Sqm.Domain.Risk;
using Sqm.Infrastructure.Risk;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>Reads the published risk snapshot (analytics migration 024) and the per-day SIM changes (023).</summary>
/// <remarks>
/// <para>
/// <b>Only filters and sorts.</b> The measures were counted by the worker; a list is a filter on one
/// run's rows - at most a few million, ordered by key - and a page of them. No level is decided here:
/// the API judges each returned row with <see cref="RiskRules"/>.
/// </para>
/// <para>
/// Through <see cref="ClickHouseJsonQuery"/> - read-only, cancelled with the caller - under a budget
/// the server enforces. The SIM-change list is the one aggregate: at most seven days of rows, about
/// 900,000 in the worst September week.
/// </para>
/// </remarks>
public sealed partial class ClickHouseRiskReader : IRiskReader
{
    [LoggerMessage(EventId = 3600, Level = LogLevel.Information,
        Message = "Risk list {Rule} ({View}): {Returned} of {Total}, {RowsRead} rows read, {ElapsedMs} ms")]
    private partial void LogList(RiskRule rule, RiskView view, int returned, long total, long rowsRead, long elapsedMs);

    private static readonly QueryBudget Budget = new(MaxRowsToRead: 50_000_000, MaxExecutionSeconds: 30);

    private static readonly IReadOnlyDictionary<string, string> NoParameters =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly ClickHouseJsonQuery _query;
    private readonly ILogger<ClickHouseRiskReader> _logger;

    /// <summary>Creates the reader over the analytics connection.</summary>
    public ClickHouseRiskReader(
        IOptions<ClickHouseOptions> options, IHttpClientFactory httpClientFactory, ILogger<ClickHouseRiskReader> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _query = new ClickHouseJsonQuery(options.Value.ConnectionString, httpClientFactory);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RiskReadState> GetStateAsync(RiskFloorOptions floors, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(floors);

        var deployed = await _query.ExecuteAsync(
            "SELECT count() FROM system.tables WHERE database = currentDatabase() AND name IN ('risk_run', 'risk_sim_change_day')",
            NoParameters, ct).ConfigureAwait(false);

        var empty = new HashSet<DateOnly>();
        if (ClickHouseJsonResult.Int64(deployed.Rows[0], 0) < 2)
        {
            return new RiskReadState(false, null, null, null, empty);
        }

        var days = await _query.ExecuteAsync(
            "SELECT data_date FROM binding_history_day FINAL WHERE state = 'done' ORDER BY data_date", NoParameters, ct)
            .ConfigureAwait(false);
        var withData = days.Rows.Select(r => ClickHouseJsonResult.Date(r, 0)!.Value).ToHashSet();

        var through = await _query.ExecuteAsync(
            "SELECT max(toDate(partition)) FROM system.parts WHERE database = currentDatabase() AND table = 'binding_event' AND active",
            NoParameters, ct).ConfigureAwait(false);
        var dataThrough = ClickHouseJsonResult.Date(through.Rows[0], 0) is { Year: > 1970 } d ? d : (DateOnly?)null;

        var published = await _query.ExecuteAsync("""
            SELECT run_id, as_of, fingerprint, toString(updated_at)
            FROM risk_run FINAL WHERE state = 'published' ORDER BY run_id DESC LIMIT 1
            """, NoParameters, ct).ConfigureAwait(false);

        if (published.Rows.Count == 0)
        {
            return new RiskReadState(true, null, null, dataThrough, withData);
        }

        var row = published.Rows[0];
        var run = new RiskPublishedRun(
            ClickHouseJsonResult.UInt64(row, 0),
            ClickHouseJsonResult.Date(row, 1)!.Value,
            ClickHouseJsonResult.UInt64(row, 2),
            DateTimeOffset.Parse(ClickHouseJsonResult.Text(row, 3)!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));

        var tac = await _query.ExecuteAsync(RiskSnapshotSql.TacVersion(string.Empty), NoParameters, ct).ConfigureAwait(false);
        var tacVersion = tac.Rows.Count == 0 ? 0 : ClickHouseJsonResult.Int32(tac.Rows[0], 0);
        var now = await _query.ExecuteAsync(RiskSnapshotSql.Fingerprint(string.Empty, tacVersion, floors), NoParameters, ct)
            .ConfigureAwait(false);

        var stale = ClickHouseJsonResult.UInt64(now.Rows[0], 0) == run.Fingerprint
            ? null
            : "The data has changed since these measures were computed - a new, late or corrected day, a GSMA "
              + "version activated, or the storage floors changed. New measures are computed while the import "
              + "worker is idle.";

        return new RiskReadState(true, run, stale, dataThrough, withData);
    }

    /// <inheritdoc />
    public async Task<RiskListPage> ListAsync(RiskPublishedRun run, RiskListQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(query);

        var list = RiskEvaluation.ListOf(query.Rule);
        var parameters = Parameters(run, query.Threshold, query.MaxDefectShare);
        parameters["limit"] = query.PageSize.ToString(CultureInfo.InvariantCulture);
        parameters["offset"] = ((long)(query.Page - 1) * query.PageSize).ToString(CultureInfo.InvariantCulture);

        var condition = Condition(query.Rule, query.View);
        var order = Order(query.Rule, query.View);

        var sql = list switch
        {
            RiskList.Sims => $$"""
                SELECT {{SimColumns}}, count() OVER () AS total
                FROM risk_sim_window
                WHERE run_id = {run:UInt64} AND {{condition}}
                ORDER BY {{order}} DESC, imsi
                LIMIT {limit:UInt32} OFFSET {offset:UInt64}
                """,
            RiskList.ImeiWindow => $$"""
                SELECT {{ImeiWindowColumns}}, count() OVER () AS total
                FROM risk_imei_window
                WHERE run_id = {run:UInt64} AND {{condition}}
                ORDER BY {{order}} DESC, imei
                LIMIT {limit:UInt32} OFFSET {offset:UInt64}
                """,
            RiskList.ImeiLifetime => $$"""
                SELECT {{ImeiLifetimeColumns}}, count() OVER () AS total
                FROM risk_imei_lifetime
                WHERE run_id = {run:UInt64} AND {{condition}}
                ORDER BY {{order}} DESC, imei
                LIMIT {limit:UInt32} OFFSET {offset:UInt64}
                """,
            _ => $$"""
                SELECT {{NumberColumns}}, count() OVER () AS total
                FROM risk_sim_change_day
                WHERE data_date BETWEEN {from7:Date} AND {asOf:Date}
                GROUP BY msisdn
                HAVING {{condition}}
                ORDER BY {{order}} DESC, msisdn
                LIMIT {limit:UInt32} OFFSET {offset:UInt64}
                """,
        };

        var result = await _query.ExecuteAsync(sql, parameters, ct, Budget).ConfigureAwait(false);

        var rows = new List<RiskEntityMeasures>(result.Rows.Count);
        long total = 0;
        var width = list switch
        {
            RiskList.Sims => SimWidth,
            RiskList.ImeiWindow => ImeiWindowWidth,
            RiskList.ImeiLifetime => ImeiLifetimeWidth,
            _ => NumberWidth,
        };

        foreach (var row in result.Rows)
        {
            rows.Add(list switch
            {
                RiskList.Sims => Sim(row),
                RiskList.ImeiWindow => new RiskEntityMeasures(RiskFamily.Imei, ClickHouseJsonResult.Text(row, 0)!, ImeiWindow: ImeiWindow(row, 1)),
                RiskList.ImeiLifetime => new RiskEntityMeasures(RiskFamily.Imei, ClickHouseJsonResult.Text(row, 0)!, ImeiLifetime: ImeiLifetime(row, 1)),
                _ => Number(row),
            });
            total = ClickHouseJsonResult.Int64(row, width);
        }

        var rowsRead = result.RowsRead;
        if (list is RiskList.ImeiWindow or RiskList.ImeiLifetime && rows.Count > 0)
        {
            (rows, rowsRead) = await CompleteImeisAsync(run, rows, list, rowsRead, ct).ConfigureAwait(false);
        }

        LogList(query.Rule, query.View, rows.Count, total, rowsRead, result.ElapsedMs);
        return new RiskListPage(rows, total, result.ElapsedMs, rowsRead);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RiskRuleCount>> CountAsync(
        RiskPublishedRun run, IReadOnlyDictionary<RiskRule, RiskThreshold> thresholds, double maxDefectShare, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(thresholds);

        var counts = new List<RiskRuleCount>();

        foreach (var group in thresholds.GroupBy(t => RiskEvaluation.ListOf(t.Key)))
        {
            var parameters = Parameters(run, new RiskThreshold(0), maxDefectShare);
            var columns = new List<string>();
            var rules = group.Select(g => g.Key).OrderBy(r => r).ToList();

            foreach (var rule in rules)
            {
                var threshold = thresholds[rule];
                var name = rule.ToString();
                parameters["t_" + name] = threshold.Value.ToString(CultureInfo.InvariantCulture);
                parameters["tt_" + name] = (threshold.Tacs ?? 0).ToString(CultureInfo.InvariantCulture);

                columns.Add($"countIf({Condition(rule, RiskView.Risk, name)})");
                columns.Add(RiskEvaluation.HasDataQualityView(rule) ? $"countIf({Condition(rule, RiskView.DataQuality, name)})" : "0");
            }

            var source = group.Key switch
            {
                RiskList.Sims => "SELECT * FROM risk_sim_window WHERE run_id = {run:UInt64}",
                RiskList.ImeiWindow => "SELECT * FROM risk_imei_window WHERE run_id = {run:UInt64}",
                RiskList.ImeiLifetime => "SELECT * FROM risk_imei_lifetime WHERE run_id = {run:UInt64}",
                _ => $"SELECT {NumberColumns} FROM risk_sim_change_day WHERE data_date BETWEEN {{from7:Date}} AND {{asOf:Date}} GROUP BY msisdn",
            };

            var result = await _query.ExecuteAsync(
                $"SELECT {string.Join(", ", columns)} FROM ({source})", parameters, ct, Budget).ConfigureAwait(false);

            var row = result.Rows[0];
            counts.AddRange(rules.Select((rule, i) =>
                new RiskRuleCount(rule, ClickHouseJsonResult.Int64(row, 2 * i), ClickHouseJsonResult.Int64(row, (2 * i) + 1))));
        }

        return [.. counts.OrderBy(c => c.Rule)];
    }

    /// <inheritdoc />
    public async Task<RiskEntityMeasures?> GetEntityAsync(RiskPublishedRun run, RiskFamily family, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(key);

        var parameters = Parameters(run, new RiskThreshold(0), 1);
        parameters["key"] = key;

        switch (family)
        {
            case RiskFamily.Sim:
            {
                var result = await _query.ExecuteAsync(
                    $"SELECT {SimColumns} FROM risk_sim_window WHERE run_id = {{run:UInt64}} AND imsi = {{key:UInt64}}",
                    parameters, ct, Budget).ConfigureAwait(false);
                return result.Rows.Count == 0 ? null : Sim(result.Rows[0]);
            }

            case RiskFamily.Number:
            {
                var result = await _query.ExecuteAsync($$"""
                    SELECT {{NumberColumns}} FROM risk_sim_change_day
                    WHERE data_date BETWEEN {from7:Date} AND {asOf:Date} AND msisdn = {key:UInt64}
                    GROUP BY msisdn
                    """, parameters, ct, Budget).ConfigureAwait(false);
                return result.Rows.Count == 0 ? null : Number(result.Rows[0]);
            }

            default:
            {
                var (complete, _) = await CompleteImeisAsync(
                    run, [new RiskEntityMeasures(RiskFamily.Imei, key)], RiskList.ImeiLifetime, 0, ct, includeOwn: true).ConfigureAwait(false);
                var entity = complete[0];
                return entity.ImeiWindow is null && entity.ImeiLifetime is null ? null : entity;
            }
        }
    }

    // ------------------------------------------------------------------ list definitions

    private const string SimColumns =
        "imsi, imeis_30, imeis_30_raw, imeis_20, tacs_20, imeis_7, imeis_7_raw, adds_30, adds_set_aside_30, "
        + "add_days_30, max_imeis_one_day_30, max_day_30, top_tacs_20";

    private const int SimWidth = 13;

    private const string ImeiWindowColumns =
        "imei, sims_30, sims_30_raw, sims_7, numbers_30, adds_30, adds_set_aside_30, add_days_30, max_sims_one_day_30, max_day_30";

    private const int ImeiWindowWidth = 10;

    private const string ImeiLifetimeColumns =
        "imei, sims_ever, numbers_ever, bindings, sims_not_removed_dated, sims_not_removed_dump";

    private const int ImeiLifetimeWidth = 6;

    // Days with a change, clean and raw. A row is one number's change on one day.
    private const string NumberColumns =
        "msisdn, countIf(set_aside = 'none') AS changes_7, count() AS changes_7_raw, max(data_date) AS last_change";

    private const int NumberWidth = 4;

    /// <summary>The clean measure a rule compares, the raw one beside it (or null), and the adds behind the share.</summary>
    private static (string Value, string? Raw, string Adds, string Aside) Columns(RiskRule rule) => rule switch
    {
        RiskRule.HighDeviceCount30 => ("imeis_30", "imeis_30_raw", "adds_30", "adds_set_aside_30"),
        RiskRule.RapidDeviceChange7 => ("imeis_7", "imeis_7_raw", "adds_30", "adds_set_aside_30"),
        RiskRule.Randomisation20 => ("imeis_20", null, "adds_30", "adds_set_aside_30"),
        RiskRule.SharedImeiSims30 => ("sims_30", "sims_30_raw", "adds_30", "adds_set_aside_30"),
        RiskRule.SharedImeiNumbers30 => ("numbers_30", null, "adds_30", "adds_set_aside_30"),
        RiskRule.SharedImeiSimsEver => ("sims_ever", null, "0", "0"),
        RiskRule.SharedImeiSimsNotRemoved => ("sims_not_removed_dated", null, "0", "0"),
        _ => ("changes_7", "changes_7_raw", "changes_7_raw", "(changes_7_raw - changes_7)"),
    };

    /// <summary>
    /// Which rows a view lists. Risk: over the threshold on clean evidence, and assessable. Data quality:
    /// over it only on the raw count, or over it with most of the evidence set aside - the rows the
    /// domain would mark not assessable.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <param name="view">The view.</param>
    /// <param name="suffix">Parameter-name suffix, so one statement can count several rules; empty for a list.</param>
    private static string Condition(RiskRule rule, RiskView view, string suffix = "")
    {
        var (value, raw, adds, aside) = Columns(rule);
        var t = suffix.Length == 0 ? "{t:Int64}" : $"{{t_{suffix}:Int64}}";
        var tt = suffix.Length == 0 ? "{tt:Int64}" : $"{{tt_{suffix}:Int64}}";
        var tacs = rule == RiskRule.Randomisation20 ? $" AND tacs_20 > {tt}" : string.Empty;

        var crossed = $"({value} > {t}{tacs})";
        var assessable = $"({aside} <= {{share:Float64}} * {adds})";

        if (view == RiskView.Risk)
        {
            return $"{crossed} AND {assessable}";
        }

        var rawOnly = raw is null ? "0" : $"({raw} > {t}{tacs} AND NOT {crossed})";
        return $"({rawOnly} OR ({crossed} AND NOT {assessable}))";
    }

    private static string Order(RiskRule rule, RiskView view)
    {
        var (value, raw, _, _) = Columns(rule);
        return view == RiskView.DataQuality && raw is not null ? raw : value;
    }

    private static Dictionary<string, string> Parameters(RiskPublishedRun run, RiskThreshold threshold, double share) =>
        new(StringComparer.Ordinal)
        {
            ["run"] = run.RunId.ToString(CultureInfo.InvariantCulture),
            ["asOf"] = run.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["from7"] = run.AsOf.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["t"] = threshold.Value.ToString(CultureInfo.InvariantCulture),
            ["tt"] = (threshold.Tacs ?? 0).ToString(CultureInfo.InvariantCulture),
            ["share"] = share.ToString("R", CultureInfo.InvariantCulture),
        };

    // ------------------------------------------------------------------ rows

    private static RiskEntityMeasures Sim(JsonElement row) => new(
        RiskFamily.Sim,
        ClickHouseJsonResult.UInt64(row, 0).ToString(CultureInfo.InvariantCulture),
        Sim: new RiskSimMeasures(
            ClickHouseJsonResult.Int64(row, 1), ClickHouseJsonResult.Int64(row, 2), ClickHouseJsonResult.Int64(row, 3),
            ClickHouseJsonResult.Int64(row, 4), ClickHouseJsonResult.Int64(row, 5), ClickHouseJsonResult.Int64(row, 6),
            ClickHouseJsonResult.Int64(row, 7), ClickHouseJsonResult.Int64(row, 8), ClickHouseJsonResult.Int32(row, 9),
            ClickHouseJsonResult.Int64(row, 10), Day(row, 11),
            [.. row[12].EnumerateArray().Select(t => t.GetString() ?? string.Empty)]));

    private static RiskImeiWindowMeasures ImeiWindow(JsonElement row, int at) => new(
        ClickHouseJsonResult.Int64(row, at), ClickHouseJsonResult.Int64(row, at + 1), ClickHouseJsonResult.Int64(row, at + 2),
        ClickHouseJsonResult.Int64(row, at + 3), ClickHouseJsonResult.Int64(row, at + 4), ClickHouseJsonResult.Int64(row, at + 5),
        ClickHouseJsonResult.Int32(row, at + 6), ClickHouseJsonResult.Int64(row, at + 7), Day(row, at + 8));

    private static RiskImeiLifetimeMeasures ImeiLifetime(JsonElement row, int at) => new(
        ClickHouseJsonResult.Int64(row, at), ClickHouseJsonResult.Int64(row, at + 1), ClickHouseJsonResult.Int64(row, at + 2),
        ClickHouseJsonResult.Int64(row, at + 3), ClickHouseJsonResult.Int64(row, at + 4));

    private static RiskEntityMeasures Number(JsonElement row) => new(
        RiskFamily.Number,
        ClickHouseJsonResult.UInt64(row, 0).ToString(CultureInfo.InvariantCulture),
        Number: new RiskNumberMeasures(ClickHouseJsonResult.Int64(row, 1), ClickHouseJsonResult.Int64(row, 2), Day(row, 3)));

    /// <summary>A stored zero date means none: no clean add in the window.</summary>
    private static DateOnly? Day(JsonElement row, int index) =>
        ClickHouseJsonResult.Date(row, index) is { Year: > 1970 } day ? day : null;

    /// <summary>
    /// An IMEI's family reads both its tables: the page came from one, so the other's rows for the same
    /// IMEIs are read by key, and each IMEI's GSMA model with them.
    /// </summary>
    private async Task<(List<RiskEntityMeasures> Rows, long RowsRead)> CompleteImeisAsync(
        RiskPublishedRun run, List<RiskEntityMeasures> rows, RiskList from, long rowsRead, CancellationToken ct,
        bool includeOwn = false)
    {
        var keys = "[" + string.Join(",", rows.Select(r => "'" + r.Key.Replace("'", string.Empty, StringComparison.Ordinal) + "'")) + "]";
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run"] = run.RunId.ToString(CultureInfo.InvariantCulture),
            ["keys"] = keys,
        };

        var windows = new Dictionary<string, RiskImeiWindowMeasures>(StringComparer.Ordinal);
        var lifetimes = new Dictionary<string, RiskImeiLifetimeMeasures>(StringComparer.Ordinal);

        if (from != RiskList.ImeiWindow || includeOwn)
        {
            var result = await _query.ExecuteAsync(
                $"SELECT {ImeiWindowColumns} FROM risk_imei_window WHERE run_id = {{run:UInt64}} AND imei IN {{keys:Array(String)}}",
                parameters, ct, Budget).ConfigureAwait(false);
            foreach (var row in result.Rows)
            {
                windows[ClickHouseJsonResult.Text(row, 0)!] = ImeiWindow(row, 1);
            }

            rowsRead += result.RowsRead;
        }

        if (from != RiskList.ImeiLifetime || includeOwn)
        {
            var result = await _query.ExecuteAsync(
                $"SELECT {ImeiLifetimeColumns} FROM risk_imei_lifetime WHERE run_id = {{run:UInt64}} AND imei IN {{keys:Array(String)}}",
                parameters, ct, Budget).ConfigureAwait(false);
            foreach (var row in result.Rows)
            {
                lifetimes[ClickHouseJsonResult.Text(row, 0)!] = ImeiLifetime(row, 1);
            }

            rowsRead += result.RowsRead;
        }

        var models = await _query.ExecuteAsync("""
            SELECT tac,
                   nullIf(coalesce(nullIf(brandName, ''), manufacturer), '') AS brand,
                   nullIf(marketingName, '')                                 AS model
            FROM tac
            WHERE tac IN (SELECT DISTINCT substring(arrayJoin({keys:Array(String)}), 1, 8))
            """, parameters, ct, Budget).ConfigureAwait(false);
        var byTac = models.Rows.ToDictionary(
            r => ClickHouseJsonResult.Text(r, 0)!,
            r => (Brand: ClickHouseJsonResult.Text(r, 1), Model: ClickHouseJsonResult.Text(r, 2)),
            StringComparer.Ordinal);

        var complete = rows.Select(r =>
        {
            var tac = r.Key.Length == 14 ? r.Key[..8] : null;
            var model = tac is not null && byTac.TryGetValue(tac, out var m) ? m : (null, null);
            return r with
            {
                ImeiWindow = r.ImeiWindow ?? windows.GetValueOrDefault(r.Key),
                ImeiLifetime = r.ImeiLifetime ?? lifetimes.GetValueOrDefault(r.Key),
                Tac = tac,
                Brand = model.Brand,
                Model = model.Model,
            };
        }).ToList();

        return (complete, rowsRead + models.RowsRead);
    }
}
