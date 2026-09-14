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
