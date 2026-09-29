using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using ClickHouse.Client.ADO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Infrastructure.ClickHouse;

namespace Sqm.Infrastructure.DataImport;

/// <summary>Writes daily deltas into the ClickHouse event log.</summary>
/// <remarks>
/// <para>
/// Reads go through the ADO driver like everything else; the bulk insert does not. It posts the
/// file straight to the HTTP interface as the request body, so the bytes travel from disk to
/// socket without ever being materialised as rows in this process. Going through the driver
/// would mean parsing a gigabyte of CSV into objects only to serialise them again.
/// </para>
/// <para>
/// The insert is expressed with <c>input()</c> rather than a plain <c>INSERT ... FORMAT CSV</c>
/// because the file has four columns and the table has six: <c>seq</c> and <c>data_date</c> are
/// supplied by the importer, not by the file. <c>input()</c> declares the file's shape and lets
/// the SELECT add the two.
/// </para>
/// </remarks>
public sealed partial class ClickHouseIngestionStore : IAnalyticsIngestionStore
{
    /// <summary>
    /// This store's own HTTP client, separate from the one the query path uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two workloads, two timeouts, and they cannot share a name.</b> The query path allows
    /// 180 seconds: a dashboard query that has not answered by then has gone wrong, and failing
    /// fast is the useful behaviour. This path allows thirty minutes, because a bulk insert of a
    /// gigabyte legitimately runs for minutes.
    /// </para>
    /// <para>
    /// Both used to be registered under the name <c>clickhouse</c>, which was safe only while the
    /// API and the worker were separate processes. Hosting the worker inside the API makes the
    /// last registration win, and either outcome is wrong: a gigabyte insert cut off at 180
    /// seconds, or a broken dashboard query hanging for half an hour instead of failing.
    /// </para>
    /// </remarks>
    public const string HttpClientName = "clickhouse-ingestion";

    [LoggerMessage(EventId = 3200, Level = LogLevel.Information,
        Message = "Loaded {Rows} rows for {BusinessDate} (seq {Sequence}) in {ElapsedMs} ms")]
    private partial void LogLoaded(long rows, DateOnly businessDate, int sequence, long elapsedMs);

    [LoggerMessage(EventId = 3201, Level = LogLevel.Information,
        Message = "Dropped existing data for {BusinessDate} before reloading")]
    private partial void LogDroppedDay(DateOnly businessDate);

    private readonly ClickHouseOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ClickHouseIngestionStore> _logger;
    private readonly Uri _httpEndpoint;
    private readonly AuthenticationHeaderValue? _authentication;
    private readonly string _database;

    public ClickHouseIngestionStore(
        IOptions<ClickHouseOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<ClickHouseIngestionStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        var builder = new ClickHouseConnectionStringBuilder(_options.ConnectionString);
        _database = string.IsNullOrWhiteSpace(builder.Database) ? "sqm" : builder.Database;

        var scheme = builder.Protocol is "https" ? "https" : "http";
        _httpEndpoint = new Uri($"{scheme}://{builder.Host}:{builder.Port}/");

        if (!string.IsNullOrEmpty(builder.Username))
        {
            var credential = $"{builder.Username}:{builder.Password}";
            _authentication = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credential)));
        }
    }

    private ClickHouseConnection CreateConnection() =>
        new(_options.ConnectionString, _httpClientFactory, HttpClientName);

    public async Task<long> CountEventsForDateAsync(DateOnly businessDate, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();

        command.CommandText = $$"""
            SELECT count() FROM {{_database}}.binding_event WHERE data_date = {businessDate:Date}
            """;
        AddDateParameter(command, "businessDate", businessDate);

        var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    public async Task RemoveDayAsync(DateOnly businessDate, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();

        // DROP PARTITION, not DELETE WHERE. With a daily partition key the day IS a partition, so
        // this detaches and removes its parts as a metadata operation. The row-level form is a
        // ClickHouse mutation, which rewrites every part it touches - measured as the cause of a
        // bulk load degrading from 11 s per file to progressively worse as the table grew.
        //
        // The partition id for `PARTITION BY data_date` is the date itself.
        var partition = businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // The bindings the outgoing day touched are kept before it goes. A binding the corrected
        // file no longer mentions still has a row in binding_current that this day decided, and
        // nothing else would ever revisit it: the fold only reads the new day's events. The next
        // fold of this day re-derives these from what remains. Appended, not replaced, so a
        // retry after a failed load keeps the keys of the attempt before it too.
        var replaced = ReplacedKeysTable(businessDate);
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {_database}.{replaced}
                (msisdn UInt64, imsi UInt64, imei String)
            ENGINE = MergeTree ORDER BY (msisdn, imsi, imei)
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await ExecuteBoundedAsync($$"""
            INSERT INTO {{_database}}.{{replaced}}
            SELECT DISTINCT msisdn, imsi, imei FROM {{_database}}.binding_event
            WHERE data_date = {businessDate:Date}
            """, businessDate, ct).ConfigureAwait(false);

        command.CommandText =
            $"ALTER TABLE {_database}.binding_event DROP PARTITION '{partition}'";

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        LogDroppedDay(businessDate);
    }

    /// <summary>Scratch table holding the bindings of a day that is being replaced.</summary>
    private static string ReplacedKeysTable(DateOnly businessDate) =>
        "fold_replaced_" + businessDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public async Task<long> LoadDailyEventsAsync(
        DateOnly businessDate,
        int sequence,
        Stream csv,
        Action<long>? onBytesRead,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(csv);

        var date = businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Values are bound as ClickHouse query parameters, never interpolated into the SQL.
        var insert = $$"""
            INSERT INTO {{_database}}.binding_event (seq, data_date, msisdn, imsi, imei, label)
            SELECT {sequence:UInt16}, {businessDate:Date}, msisdn, imsi, imei, label
            FROM input('msisdn UInt64, imsi UInt64, imei String, label String')
            FORMAT CSVWithNames
            """;

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["query"] = insert,
            ["param_sequence"] = sequence.ToString(CultureInfo.InvariantCulture),
            ["param_businessDate"] = date,
            ["database"] = _database,

            // A per-query ceiling with spill below it. Without one, a heavy statement enters the
            // server's overcommit arbitration and gets killed non-deterministically; with one it
            // spills to disk instead - slower, and always correct.
            ["max_memory_usage"] = "3000000000",
            ["max_insert_block_size"] = "1048576",

            // The whole point of an idempotent day-level import: a partial insert that fails
            // halfway must not leave half a day behind. This makes the server buffer the insert
            // and commit it as one block set.
            ["insert_deduplicate"] = "0",
            ["input_format_allow_errors_num"] = "0",
        };

        var url = new UriBuilder(_httpEndpoint)
        {
            Query = string.Join('&', query.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")),
        }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (_authentication is not null)
        {
            request.Headers.Authorization = _authentication;
        }

        var body = onBytesRead is null ? csv : new CountingStream(csv, onBytesRead);
        request.Content = new StreamContent(body, bufferSize: 1 << 20);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var started = System.Diagnostics.Stopwatch.StartNew();

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"ClickHouse rejected the insert for {date}: {FirstLine(detail)}");
        }

        started.Stop();

        var rows = ReadWrittenRows(response);
        LogLoaded(rows, businessDate, sequence, started.ElapsedMilliseconds);
        return rows;
    }

    /// <summary>
    /// Reads the row count the server reports in <c>X-ClickHouse-Summary</c>.
    /// </summary>
    /// <remarks>
    /// Preferred over a follow-up <c>SELECT count()</c>: the summary describes this insert, where
    /// a count describes the table and would silently include anything another writer added in
    /// between. Returns 0 when the header is missing, which is a reporting gap, not a failure -
    /// the insert either succeeded or threw above.
    /// </remarks>
    private static long ReadWrittenRows(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-ClickHouse-Summary", out var values))
        {
            return 0;
        }

        foreach (var value in values)
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                if (document.RootElement.TryGetProperty("written_rows", out var written)
                    && long.TryParse(written.GetString(), CultureInfo.InvariantCulture, out var rows))
                {
                    return rows;
                }
            }
            catch (JsonException)
            {
                // Not fatal: the insert already succeeded. Fall through and report zero.
            }
        }

        return 0;
    }

    public async Task<long> FoldDayAsync(DateOnly businessDate, CancellationToken ct)
    {
        var replaced = ReplacedKeysTable(businessDate);
        var replacing = await ScalarAsync($"EXISTS TABLE {_database}.{replaced}", ct)
            .ConfigureAwait(false) == 1;
        var laterDays = await GetBusinessDatesAsync(businessDate.AddDays(1), null, ct)
            .ConfigureAwait(false);

        // The fast path. binding_current is a ReplacingMergeTree versioned by last_change_seq,
        // so when this is the newest day its row wins over anything written before it - which
        // is exactly the fold, expressed as a write rather than as a computation. It holds only
        // while the day is the latest by DATE and adds to history rather than rewriting it.
        var sql = $$"""
            INSERT INTO {{_database}}.binding_current
                (msisdn, imsi, imei, active, last_change_seq, last_change_date)
            SELECT
                msisdn, imsi, imei,
                argMax(label, seq) = 'add' AS active,
                max(seq)                   AS last_change_seq,
                argMax(data_date, seq)     AS last_change_date
            FROM {{_database}}.binding_event
            WHERE data_date = {businessDate:Date}
            GROUP BY msisdn, imsi, imei
            """;

        if (replacing || laterDays.Count > 0)
        {
            // A day that arrives after later days, or replaces one, cannot use it. A missing day
            // imported late is given the next sequence number, so its rows outranked every later
            // day's: a binding added on 12 May and removed on 3 June read as active once 12 May
            // landed. And a replaced day's withdrawn bindings were never revisited at all.
            //
            // So the bindings involved are re-derived from their whole history, in DATE order.
            await FoldFromHistoryAsync(businessDate, replacing ? replaced : null, ct)
                .ConfigureAwait(false);
        }
        else
        {
            await ExecuteBoundedAsync(sql, businessDate, ct).ConfigureAwait(false);
        }

        // Only once the fold has landed: until then a retry needs these keys again.
        await ExecuteBoundedAsync($"DROP TABLE IF EXISTS {_database}.{replaced}", businessDate, ct)
            .ConfigureAwait(false);

        return await CountFoldedAsync(businessDate, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-derives every binding a late or replaced day touched from its whole history, one
    /// msisdn range at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last event by date decides a binding's state, and with no event left the initial dump
    /// does. The version written is the highest sequence in the log, which no earlier row can
    /// exceed, so the re-derived row replaces what was there; days after this one keep winning,
    /// because they take the next sequence. One GROUP BY and no joins: under the fold's
    /// one-thread, 1.2 GB limits a GROUP BY can spill to disk and a hash join cannot. Each
    /// binding's candidates are ranked - its events (2), the initial dump (1), nothing (0) - and
    /// the highest-ranked, latest-dated one decides.
    /// </para>
    /// <para>
    /// <b>In ranges, because in one statement it cannot run.</b> Written as a single statement
    /// and never tried at scale, it failed on the first real day it was measured against:
    /// 2026-07-20 touched 6,503,281 bindings, the IN-sets built from them passed the 1.2 GB cap
    /// after seven seconds (MEMORY_LIMIT_EXCEEDED in CreatingSetsTransform), and every late day
    /// would have failed the same way - and a failed day blocks every day after it.
    /// </para>
    /// <para>
    /// A binding's key begins with its msisdn, so its whole history lies inside one msisdn range
    /// and cutting by range is exact, not an approximation. binding_event and binding_snapshot are
    /// sorted by msisdn first, so each range reads only its own part of them: the ranges together
    /// read the event log once. Measured on 2026-07-20 with ranges of about a million bindings:
    /// each ran in about a minute and a half, under the cap, and gave exactly the state
    /// binding_current holds.
    /// </para>
    /// <para>
    /// A retry after a failure part way is safe: a range already written is written again with
    /// the same rows and the same version, and ReplacingMergeTree keeps one.
    /// </para>
    /// </remarks>
    private async Task FoldFromHistoryAsync(
        DateOnly businessDate, string? replacedKeysTable, CancellationToken ct)
    {
        var date = businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var keySource = $"SELECT msisdn, imsi, imei FROM {_database}.binding_event "
                        + $"WHERE data_date = toDate('{date}')"
                        + (replacedKeysTable is null
                            ? string.Empty
                            : $" UNION ALL SELECT msisdn, imsi, imei FROM {_database}.{replacedKeysTable}");

        // Taken once, before any range is written, so every range carries the same version.
        var version = await ScalarAsync($"SELECT max(seq) FROM {_database}.binding_event", ct)
            .ConfigureAwait(false) ?? 0;

        var keys = await ScalarAsync($"SELECT count() FROM ({keySource})", ct)
            .ConfigureAwait(false) ?? 0;

        foreach (var (lo, hi) in await MsisdnRangesAsync(keySource, keys, ct).ConfigureAwait(false))
        {
            await ExecuteHttpAsync($$"""
                INSERT INTO {{_database}}.binding_current
                    (msisdn, imsi, imei, active, last_change_seq, last_change_date)
                WITH keys AS (
                    SELECT msisdn, imsi, imei FROM ({{keySource}})
                    WHERE msisdn BETWEEN {lo:UInt64} AND {hi:UInt64}
                )
                SELECT
                    msisdn, imsi, imei,
                    argMax(is_add, (rank, data_date, seq))              AS active,
                    {version:UInt16}                                    AS last_change_seq,
                    if(max(rank) = 2, maxIf(data_date, rank = 2), NULL) AS last_change_date
                FROM (
                    SELECT msisdn, imsi, imei, label = 'add' AS is_add, data_date, seq, 2 AS rank
                    FROM {{_database}}.binding_event
                    WHERE msisdn BETWEEN {lo:UInt64} AND {hi:UInt64}
                      AND (msisdn, imsi, imei) IN (SELECT msisdn, imsi, imei FROM keys)
                    UNION ALL
                    SELECT msisdn, imsi, imei, 1, toDate(0), 0, 1
                    FROM {{_database}}.binding_snapshot
                    WHERE msisdn BETWEEN {lo:UInt64} AND {hi:UInt64}
                      AND (msisdn, imsi, imei) IN (SELECT msisdn, imsi, imei FROM keys)
                    UNION ALL
                    SELECT msisdn, imsi, imei, 0, toDate(0), 0, 0
                    FROM keys
                )
                GROUP BY msisdn, imsi, imei
                """,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["param_lo"] = lo.ToString(CultureInfo.InvariantCulture),
                    ["param_hi"] = hi.ToString(CultureInfo.InvariantCulture),
                    ["param_version"] = version.ToString(CultureInfo.InvariantCulture),
                },
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Inclusive msisdn ranges holding about <see cref="ClickHouseOptions.FoldKeysPerRange"/>
    /// keys each, together covering every msisdn there is.
    /// </summary>
    private async Task<IReadOnlyList<(ulong Lo, ulong Hi)>> MsisdnRangesAsync(
        string keySource, long keys, CancellationToken ct)
    {
        var perRange = Math.Max(1, _options.FoldKeysPerRange);
        var count = (int)Math.Max(1, (keys + perRange - 1) / perRange);

        if (count == 1)
        {
            return [(0UL, ulong.MaxValue)];
        }

        var levels = string.Join(", ", Enumerable.Range(1, count - 1)
            .Select(i => ((double)i / count).ToString("R", CultureInfo.InvariantCulture)));

        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT arrayJoin(arraySort(arrayDistinct(quantilesExact({levels})(msisdn)))) FROM ({keySource})";
        command.CommandTimeout = _options.QueryTimeoutSeconds;

        var cuts = new List<ulong>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                cuts.Add(Convert.ToUInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
            }
        }

        // Each cut starts a range and the range before it ends one below. A cut at zero would
        // leave an empty first range, so it is skipped.
        var ranges = new List<(ulong Lo, ulong Hi)>();
        var lo = 0UL;
        foreach (var cut in cuts.Where(c => c > 0))
        {
            ranges.Add((lo, cut - 1));
            lo = cut;
        }

        ranges.Add((lo, ulong.MaxValue));
        return ranges;
    }

    private async Task<long> CountFoldedAsync(DateOnly businessDate, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();

        command.CommandText = $$"""
            SELECT uniqExact(msisdn, imsi, imei) FROM {{_database}}.binding_event
            WHERE data_date = {businessDate:Date}
            """;

        AddDateParameter(command, "businessDate", businessDate);
        var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    public async Task RefreshChangeMartsForDayAsync(
        DateOnly businessDate, int sequence, CancellationToken ct)
    {
        var partition = businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Drop before insert, every time. The marts are SummingMergeTree and ReplacingMergeTree,
        // and an insert-only refresh doubled every count in agg_device_daily once already -
        // silently, because a SummingMergeTree sums duplicates without complaint.
        // All four day-level marts, dropped then rebuilt. Drop before insert, every time: they
        // are SummingMergeTree and ReplacingMergeTree, and an insert-only refresh doubled every
        // count in agg_device_daily once already - silently, because a SummingMergeTree sums
        // duplicates without complaint.
        //
        // This method is the single implementation. The batch backfill runs the same code over a
        // range of days rather than a second copy of these statements, so the marts built for
        // the 133 historical days cannot drift from the ones a daily import builds tomorrow.
        string[] statements =
        [
            $"ALTER TABLE {_database}.agg_change_daily DROP PARTITION '{partition}'",
            $"ALTER TABLE {_database}.agg_change_summary_daily DROP PARTITION '{partition}'",
            $"ALTER TABLE {_database}.agg_sim_change_daily DROP PARTITION '{partition}'",
            $"ALTER TABLE {_database}.agg_device_change_daily DROP PARTITION '{partition}'",

            // Change counts per TAC, for vendor- and model-level growth.
            $$"""
            INSERT INTO {{_database}}.agg_change_daily (seq, data_date, tac, label, n)
            SELECT any(seq), data_date, tac, label, count()
            FROM {{_database}}.binding_event
            WHERE data_date = {businessDate:Date}
            GROUP BY data_date, tac, label
            """,

            // Per-day totals, kept separate so the headline series does not have to aggregate
            // the TAC-level table to draw one point.
            $$"""
            INSERT INTO {{_database}}.agg_change_summary_daily
                (seq, data_date, added, removed, redundant_adds, orphan_removes,
                 unknown_device_rows, rows_total)
            SELECT
                any(seq), data_date,
                countIf(label = 'add')     AS added,
                countIf(label = 'remove')  AS removed,
                -- Left at zero on purpose. Deriving them needs each binding's state before the
                -- day, which is a window function over the whole event log - a different order
                -- of cost from everything else here. They are measured in discovery (19.18% and
                -- 1.77%) and belong to the fold, which already knows the prior state.
                0, 0,
                countIf(imei = '000000')   AS unknown_device_rows,
                count()                    AS rows_total
            FROM {{_database}}.binding_event
            WHERE data_date = {businessDate:Date}
            GROUP BY data_date
            """,

            // SIM changes: a number that on this day had a remove carrying one IMSI and an add
            // carrying a different one - some IMSI was removed, and some IMSI was added that was
            // not also removed.
            //
            // Two GROUP BYs, not arrays. It used to collect each number's added and removed
            // IMSIs into arrays and filter one against the other, and that lambda copies the
            // removed array once per added element: memory grows with the SQUARE of what one
            // number carries in a day. Early August has numbers carrying ~8,900 devices a day,
            // and the handset version below needed 1.18 GiB for 2026-08-06 against a 1.12 GiB
            // cap - five attempts, five failures, and the day blocked every day after it. Two
            // aggregations are linear, and a GROUP BY spills to disk where an array cannot.
            // Same result: checked against the array form on real days where that one fits.
            $$"""
            INSERT INTO {{_database}}.agg_sim_change_daily (data_date, msisdn_changed)
            SELECT data_date, count()
            FROM (
                SELECT data_date, msisdn
                FROM (
                    SELECT data_date, msisdn, imsi,
                           max(label = 'add')    AS added,
                           max(label = 'remove') AS removed
                    FROM {{_database}}.binding_event
                    WHERE data_date = {businessDate:Date}
                    GROUP BY data_date, msisdn, imsi
                )
                GROUP BY data_date, msisdn
                HAVING countIf(removed) > 0 AND countIf(added AND NOT removed) > 0
            )
            GROUP BY data_date
            """,

            // Handset changes: the same shape, on IMEI. Measured over the whole window at 51.3%
            // of subscribers, so this is the largest churn signal in the dataset - and, keyed by a
            // string, the one the array form ran out of memory on.
            $$"""
            INSERT INTO {{_database}}.agg_device_change_daily (data_date, msisdn_changed)
            SELECT data_date, count()
            FROM (
                SELECT data_date, msisdn
                FROM (
                    SELECT data_date, msisdn, imei,
                           max(label = 'add')    AS added,
                           max(label = 'remove') AS removed
                    FROM {{_database}}.binding_event
                    WHERE data_date = {businessDate:Date}
                    GROUP BY data_date, msisdn, imei
                )
                GROUP BY data_date, msisdn
                HAVING countIf(removed) > 0 AND countIf(added AND NOT removed) > 0
            )
            GROUP BY data_date
            """,
        ];

        foreach (var sql in statements)
        {
            await ExecuteBoundedAsync(sql, businessDate, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one statement over HTTP with its resource limits as query parameters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The limits have to travel outside the SQL because three ways of attaching them to it did
    /// not survive the driver. <c>CustomSettings</c> did nothing; <c>set_*</c> in the connection
    /// string did nothing; and a trailing <c>SETTINGS</c> clause - which demonstrably works when
    /// the same statement is run by <c>clickhouse-client</c> - arrived at the server without it.
    /// </para>
    /// <para>
    /// Each was found the same way, and it is worth naming because a memory limit that is not
    /// applied looks exactly like a limit that is generous: by watching
    /// <c>system.processes.memory_usage</c> while the job ran. The aggregates were reaching
    /// 2.66 GiB against a declared cap of 1.2 GiB.
    /// </para>
    /// <para>
    /// URL parameters are the mechanism the bulk insert already uses, and the one path in this
    /// class that was never in doubt: the server reads settings from the query string before it
    /// parses anything, so no client library sits between the intent and the effect.
    /// </para>
    /// </remarks>
    private Task ExecuteBoundedAsync(string sql, DateOnly businessDate, CancellationToken ct) =>
        ExecuteHttpAsync(
            sql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["param_businessDate"] =
                    businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            },
            ct);

    /// <summary>
    /// POSTs one statement, with its resource limits as query parameters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The limits travel outside the SQL because three ways of attaching them to it did not
    /// survive the driver. <c>CustomSettings</c> did nothing; <c>set_*</c> in the connection
    /// string did nothing; and a trailing <c>SETTINGS</c> clause - which demonstrably works when
    /// the same statement is run by <c>clickhouse-client</c> - arrived at the server without it.
    /// </para>
    /// <para>
    /// Each was found the same way, and it is worth naming because a memory limit that is not
    /// applied looks exactly like a limit that is generous: by watching
    /// <c>system.processes.memory_usage</c> while the job ran. The aggregates were reaching
    /// 2.66 GiB against a declared cap of 1.2 GiB.
    /// </para>
    /// <para>
    /// URL parameters are the mechanism the bulk insert already uses, and the one path in this
    /// class that was never in doubt: the server reads settings from the query string before it
    /// parses anything, so no client library sits between the intent and the effect.
    /// </para>
    /// </remarks>
    private async Task ExecuteHttpAsync(
        string sql, IReadOnlyDictionary<string, string> extraParameters, CancellationToken ct)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["query"] = sql,
            ["database"] = _database,

            // Measured, not guessed. ClickHouse builds one hash table per thread, so the thread
            // count multiplies what a GROUP BY needs: the churn aggregate over one day takes
            // ~1.9 GiB at three threads and 565 MiB at one, in 3.9 seconds either way. Three
            // times the memory for no useful speed-up, on a job that runs once a day.
            ["max_threads"] = "1",
            ["max_memory_usage"] = "1200000000",

            // Spill well before the ceiling, so a heavy GROUP BY writes to disk rather than
            // approaching the limit. The spill count in ProfileEvents is also the proof that
            // these settings arrived: 122 external parts cannot happen if they did not.
            ["max_bytes_before_external_group_by"] = "300000000",
            ["max_bytes_before_external_sort"] = "300000000",
        };

        foreach (var (key, value) in extraParameters)
        {
            query[key] = value;
        }

        var url = new UriBuilder(_httpEndpoint)
        {
            Query = string.Join('&', query.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")),
        }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (_authentication is not null)
        {
            request.Headers.Authorization = _authentication;
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(FirstLine(detail));
        }
    }

    public Task ExecuteMartStatementAsync(string sql, int sequence, CancellationToken ct) =>
        // The script's statements carry {seq:UInt16}; it is bound as a ClickHouse query
        // parameter, the same as everywhere else, rather than substituted into the text.
        ExecuteHttpAsync(
            sql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["param_seq"] = sequence.ToString(CultureInfo.InvariantCulture),
            },
            ct);

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();

        command.CommandText =
            $"SELECT partition_key FROM system.tables WHERE database = '{_database}' "
            + "AND name = 'binding_event'";

        var key = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;

        if (key is null)
        {
            throw new InvalidOperationException(
                $"{_database}.binding_event does not exist. Apply the analytics migrations first.");
        }

        // Day-level idempotency is a partition drop. On a monthly-partitioned table the same
        // statement removes a whole month, so this refuses to start rather than discovering it
        // when a corrected file for one day deletes the other thirty.
        if (!key.Contains("data_date", StringComparison.Ordinal)
            || key.Contains("toYYYYMM", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{_database}.binding_event is partitioned by '{key}'. The import worker needs it "
                + "partitioned by day (PARTITION BY data_date), because removing a day before "
                + "re-importing it is implemented as DROP PARTITION. Apply migration "
                + "015_daily_partitioning.sql.");
        }
    }

    public async Task SetMartsReadyAsync(
        int sequence, bool ready, int statements, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();

        // ReplacingMergeTree keyed on seq, so publishing is an insert and withdrawing is a
        // delete of one row. max(seq) is unaffected by a duplicate row that has not merged yet,
        // so the reader needs no FINAL.
        //
        // mutations_sync=2 on the withdraw is the part that matters. ALTER ... DELETE is an
        // asynchronous mutation: without waiting for it, this method would return while the
        // delivery was still published, and the refresh would start dropping partitions out
        // from under a dashboard that was still reading them. Waiting costs milliseconds on a
        // table of a few rows.
        command.CommandText = ready
            ? $"INSERT INTO {_database}.mart_ready (seq, completed_at, statements) "
              + $"VALUES ({sequence}, now64(3), {statements})"
            : $"ALTER TABLE {_database}.mart_ready DELETE WHERE seq = {sequence} "
              + "SETTINGS mutations_sync = 2";

        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<long?> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.QueryTimeoutSeconds;

        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task SetMergesEnabledAsync(bool enabled, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = enabled ? "SYSTEM START MERGES" : "SYSTEM STOP MERGES";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DateOnly>> GetBuiltMartDatesAsync(CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();

        // agg_change_summary_daily is the last of the four day-level marts a refresh writes, so
        // a day present here had all four succeed. Checking the cheapest one would report days
        // as done that are only partly built.
        command.CommandText =
            $"SELECT DISTINCT toString(data_date) FROM {_database}.agg_change_summary_daily "
            + "ORDER BY data_date";

        var dates = new List<DateOnly>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (DateOnly.TryParseExact(
                reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date))
            {
                dates.Add(date);
            }
        }

        return dates;
    }

    public async Task<IReadOnlyList<DateOnly>> GetBusinessDatesAsync(
        DateOnly? fromDate, DateOnly? toDate, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();

        // Reads the partition list rather than the data. With one partition per day, the set of
        // days is metadata: this answers in milliseconds where a DISTINCT over the column would
        // read a billion rows.
        var clauses = new List<string>();
        if (fromDate is { } lower)
        {
            clauses.Add($"partition >= '{lower:yyyy-MM-dd}'");
        }

        if (toDate is { } upper)
        {
            clauses.Add($"partition <= '{upper:yyyy-MM-dd}'");
        }

        var where = clauses.Count > 0 ? " AND " + string.Join(" AND ", clauses) : string.Empty;

        command.CommandText =
            $"SELECT DISTINCT partition FROM system.parts WHERE database = '{_database}' "
            + $"AND table = 'binding_event' AND active{where} ORDER BY partition";

        var dates = new List<DateOnly>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (DateOnly.TryParseExact(
                reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date))
            {
                dates.Add(date);
            }
        }

        return dates;
    }

    public async Task<int> GetMaxSequenceAsync(CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT max(seq) FROM {_database}.binding_event";

        var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is null or DBNull ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    /// <remarks>
    /// <para>
    /// A day that already has a sequence keeps it, so a corrected file replaces the day in place
    /// rather than appearing as a new one at the end of the series. A day that does not gets the
    /// next number - including a missing day that arrives late, whose number then does not match
    /// its calendar position. Nothing that decides state reads the number as an order: the fold
    /// re-derives a late day's bindings by date, and the dashboard marts follow the latest date.
    /// </para>
    /// <para>
    /// <b>Unless another day shares it.</b> "Next" is the highest number plus one, read without a
    /// lock, so two files processed at once can both read it: on 2026-09-27 two workers gave
    /// 14 August and 26 September the same sequence, 216. Rows of both days then carried one
    /// version, whichever write landed last won, and 7,011 bindings kept a state from before
    /// 26 September. Only one day of a source runs at a time now, so it cannot happen again;
    /// a day found sharing its number is given a new one when it is next imported, which is
    /// what reprocessing it does - and the fold that follows re-derives its bindings by date.
    /// </para>
    /// </remarks>
    public async Task<int> ResolveSequenceForDateAsync(DateOnly businessDate, CancellationToken ct)
    {
        if (await GetSequenceForDateAsync(businessDate, ct).ConfigureAwait(false) is { } own)
        {
            await using var connection = CreateConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = $$"""
                SELECT count() FROM {{_database}}.binding_event
                WHERE seq = {seq:UInt16} AND data_date != {businessDate:Date}
                """;
            AddDateParameter(command, "businessDate", businessDate);
            var seq = command.CreateParameter();
            seq.ParameterName = "seq";
            seq.Value = own;
            command.Parameters.Add(seq);

            var shared = Convert.ToInt64(
                await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);

            if (shared == 0)
            {
                return own;
            }
        }

        return await GetMaxSequenceAsync(ct).ConfigureAwait(false) + 1;
    }

    public async Task<int?> GetSequenceForDateAsync(DateOnly businessDate, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();

        command.CommandText = $$"""
            SELECT any(seq) FROM {{_database}}.binding_event
            WHERE data_date = {businessDate:Date}
            """;
        AddDateParameter(command, "businessDate", businessDate);

        var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (scalar is null or DBNull)
        {
            return null;
        }

        var value = Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
        return value == 0 ? null : value;
    }

    /// <summary>Binds a date as a ClickHouse query parameter.</summary>
    private static void AddDateParameter(
        System.Data.Common.DbCommand command, string name, DateOnly value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        command.Parameters.Add(parameter);
    }

    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        var line = newline >= 0 ? text[..newline] : text;
        return line.Length > 400 ? line[..400] : line;
    }
}
