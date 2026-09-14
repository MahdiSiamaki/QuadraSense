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
        new(_options.ConnectionString, _httpClientFactory, ClickHouseAnalyticsStore.HttpClientName);

    /// <summary>
    /// A connection whose queries are bounded in memory and spill to disk rather than grow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the aggregates: the fold and the churn marts both group by a high-cardinality key, and
    /// without a ceiling they take as much as the server will give them. The server now has its
    /// own ceiling, so an unbounded query no longer brings the whole process down - but it does
    /// starve everything else for as long as it runs, which on a single-node deployment means
    /// the dashboard stops answering while a nightly job works.
    /// </para>
    /// <para>
    /// The spill threshold sits well below the ceiling so a heavy GROUP BY starts writing to disk
    /// long before it approaches being killed. Slower, and always correct - the same trade the
    /// batch jobs make.
    /// </para>
    /// </remarks>
    private ClickHouseConnection CreateBoundedConnection()
    {
        var connection = CreateConnection();

        connection.CustomSettings["max_memory_usage"] = 2_000_000_000L;
        connection.CustomSettings["max_bytes_before_external_group_by"] = 600_000_000L;
        connection.CustomSettings["max_threads"] = 3;

        return connection;
    }

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
        command.CommandText =
            $"ALTER TABLE {_database}.binding_event DROP PARTITION '{partition}'";

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        LogDroppedDay(businessDate);
    }

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

        var client = _httpClientFactory.CreateClient(ClickHouseAnalyticsStore.HttpClientName);
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
        await using var connection = CreateBoundedConnection();
        await using var command = connection.CreateCommand();

        // No delete first, and no replay. binding_current is a ReplacingMergeTree versioned by
        // last_change_seq, so a row written for a later day wins over an earlier one on merge -
        // which is exactly the fold, expressed as a write rather than as a computation.
        command.CommandText = $$"""
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

        AddDateParameter(command, "businessDate", businessDate);
        command.CommandTimeout = 0;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return await CountFoldedAsync(businessDate, ct).ConfigureAwait(false);
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
            // carrying a different one.
            $$"""
            INSERT INTO {{_database}}.agg_sim_change_daily (data_date, msisdn_changed)
            SELECT data_date, count()
            FROM (
                SELECT
                    data_date,
                    msisdn,
                    groupUniqArrayIf(imsi, label = 'add')    AS added_sims,
                    groupUniqArrayIf(imsi, label = 'remove') AS removed_sims
                FROM {{_database}}.binding_event
                WHERE data_date = {businessDate:Date}
                GROUP BY data_date, msisdn
                HAVING length(added_sims) > 0
                   AND length(removed_sims) > 0
                   AND length(arrayFilter(x -> NOT has(removed_sims, x), added_sims)) > 0
            )
            GROUP BY data_date
            """,

            // Handset changes: the same shape, on IMEI. Measured over the whole window at 51.3%
            // of subscribers, so this is the largest churn signal in the dataset.
            $$"""
            INSERT INTO {{_database}}.agg_device_change_daily (data_date, msisdn_changed)
            SELECT data_date, count()
            FROM (
                SELECT
                    data_date,
                    msisdn,
                    groupUniqArrayIf(imei, label = 'add')    AS added_devices,
                    groupUniqArrayIf(imei, label = 'remove') AS removed_devices
                FROM {{_database}}.binding_event
                WHERE data_date = {businessDate:Date}
                GROUP BY data_date, msisdn
                HAVING length(added_devices) > 0
                   AND length(removed_devices) > 0
                   AND length(arrayFilter(x -> NOT has(removed_devices, x), added_devices)) > 0
            )
            GROUP BY data_date
            """,
        ];

        foreach (var sql in statements)
        {
            await using var connection = CreateBoundedConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 0;

            if (sql.Contains("businessDate", StringComparison.Ordinal))
            {
                AddDateParameter(command, "businessDate", businessDate);
            }

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

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
