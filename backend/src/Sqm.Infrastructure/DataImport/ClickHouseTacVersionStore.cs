using System.Data.Common;
using System.Globalization;
using System.Net.Http.Headers;
using ClickHouse.Client.ADO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;
using Sqm.Infrastructure.ClickHouse;

namespace Sqm.Infrastructure.DataImport;

/// <summary>ClickHouse-backed TAC version store.</summary>
/// <remarks>
/// <para>
/// All versions live in <c>sqm.tac_all</c>, partitioned by <c>version_id</c>. The partitioning
/// is what makes a failed load cheap to undo: dropping a bad version is a metadata operation on
/// its own partition, not a delete that rewrites parts belonging to versions that are fine.
/// </para>
/// <para>
/// <c>sqm.tac</c> is a view onto whichever version <c>sqm.tac_active</c> names, so activating a
/// version is one small insert and every existing query keeps working without knowing versions
/// exist at all.
/// </para>
/// </remarks>
public sealed partial class ClickHouseTacVersionStore : ITacVersionStore
{
    [LoggerMessage(EventId = 3600, Level = LogLevel.Information,
        Message = "Loaded {Rows} TAC rows as version {VersionId} in {ElapsedMs} ms")]
    private partial void LogLoaded(long rows, int versionId, long elapsedMs);

    [LoggerMessage(EventId = 3601, Level = LogLevel.Warning,
        Message = "TAC version {VersionId} activated")]
    private partial void LogActivated(int versionId);

    /// <summary>
    /// The GSMA column contract, in order.
    /// </summary>
    /// <remarks>
    /// Order is part of the contract, not just membership. These columns are all strings, so a
    /// file whose columns were reordered would load without a single type error and quietly put
    /// model names in the manufacturer column - the kind of defect that is invisible in the data
    /// and obvious on every screen.
    ///
    /// The names were restored in migration 006 after an earlier version renamed eleven of them
    /// to placeholders: CSVWithNames matches by header name, so those columns silently received
    /// nothing while the row count stayed exactly right.
    /// </remarks>
    public static readonly string[] Columns =
    [
        "tac", "manufacturer", "modelName", "marketingName", "brandName", "allocationDate",
        "lastUpdatedDate", "organisationId", "deviceType", "bluetooth", "nfc", "wlan",
        "authenticatedIMSEmergencyCallSupport", "unauthenticatedIMSEmergencyCallSupport",
        "imsEmergencyCallWithoutUICC", "removableUICC", "removableEUICC", "nonremovableUICC",
        "nonremovableEUICC", "networkSpecificIdentifier", "ntnConnectivity", "simSlot",
        "imeiQuantity", "operatingSystem", "oem", "bandDetails",
    ];

    private readonly ClickHouseOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ClickHouseTacVersionStore> _logger;
    private readonly Uri _httpEndpoint;
    private readonly AuthenticationHeaderValue? _authentication;
    private readonly string _database;

    public ClickHouseTacVersionStore(
        IOptions<ClickHouseOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<ClickHouseTacVersionStore> logger)
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

    public async Task<int?> GetActiveVersionIdAsync(CancellationToken ct)
    {
        var value = await ScalarAsync(
            $"SELECT version_id FROM {_database}.tac_active FINAL ORDER BY activated_at DESC LIMIT 1",
            ct).ConfigureAwait(false);

        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task<long> LoadVersionAsync(
        int versionId, Stream csv, Action<long>? onBytesRead, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(csv);

        // The file's 26 columns are declared to input(); version_id is supplied by the importer.
        // Every column is String because the GSMA export is: "Not Known" appears in date and
        // boolean-looking columns alike, and parsing it into a typed column would mean either
        // rejecting valid rows or inventing a value that is not in the source.
        var declaration = string.Join(", ", Columns.Select(c => $"{c} String"));
        var columnList = string.Join(", ", Columns);

        var insert = $$"""
            INSERT INTO {{_database}}.tac_all (version_id, {{columnList}})
            SELECT {versionId:UInt32}, {{columnList}}
            FROM input('{{declaration}}')
            FORMAT CSVWithNames
            """;

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["query"] = insert,
            ["param_versionId"] = versionId.ToString(CultureInfo.InvariantCulture),
            ["database"] = _database,
            ["max_memory_usage"] = "2000000000",

            // The bandDetails column contains commas inside quotes, so the file genuinely needs
            // RFC 4180 quoting rules. ClickHouse's CSV reader applies them; a line splitter
            // would tear those rows apart.
            ["format_csv_allow_single_quotes"] = "0",
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
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"ClickHouse rejected TAC version {versionId}: {Truncate(detail)}");
        }

        stopwatch.Stop();

        var rows = Convert.ToInt64(
            await ScalarAsync(
                $"SELECT count() FROM {_database}.tac_all WHERE version_id = {versionId}", ct)
                .ConfigureAwait(false),
            CultureInfo.InvariantCulture);

        LogLoaded(rows, versionId, stopwatch.ElapsedMilliseconds);
        return rows;
    }

    public async Task<TacVersionCheck> CheckVersionAsync(int versionId, CancellationToken ct)
    {
        var sql = $$"""
            SELECT
                count()                                                    AS row_count,
                uniqExact(tac)                                             AS distinct_tacs,
                countIf(NOT match(tac, '^[0-9]{8}$'))                      AS malformed,
                countIf(manufacturer = '' OR manufacturer = 'Not Known')   AS blank_manufacturer,
                max(lastUpdatedDate)                                       AS latest_update
            FROM {{_database}}.tac_all
            WHERE version_id = {{versionId}}
            """;

        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new TacVersionCheck(0, 0, 0, 0, null);
        }

        return new TacVersionCheck(
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    public async Task<TacVersionDiff> DiffAsync(
        int versionId, int againstVersionId, CancellationToken ct)
    {
        // A full outer join on TAC, with a row fingerprint deciding "updated" versus "unchanged".
        // Hashing the whole row rather than comparing 25 columns by name means a new GSMA column
        // is picked up as a change automatically instead of being silently ignored until someone
        // remembers to extend the comparison.
        var fingerprint = "cityHash64(" + string.Join(", ", Columns.Skip(1)) + ")";

        var sql = $"""
            WITH
                current AS (
                    SELECT tac, {fingerprint} AS fp FROM {_database}.tac_all
                    WHERE version_id = {versionId}
                ),
                previous AS (
                    SELECT tac, {fingerprint} AS fp FROM {_database}.tac_all
                    WHERE version_id = {againstVersionId}
                ),
                joined AS (
                    SELECT
                        c.tac AS new_tac, p.tac AS old_tac,
                        c.fp  AS new_fp,  p.fp  AS old_fp
                    FROM current AS c
                    FULL OUTER JOIN previous AS p ON c.tac = p.tac
                )
            SELECT
                countIf(old_tac = '')                              AS added,
                countIf(new_tac = '')                              AS removed,
                countIf(new_tac != '' AND old_tac != '' AND new_fp != old_fp) AS updated,
                countIf(new_tac != '' AND old_tac != '' AND new_fp = old_fp)  AS unchanged
            FROM joined
            """;

        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.QueryTimeoutSeconds;

        int added = 0, removed = 0, updated = 0, unchanged = 0;

        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                added = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                removed = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                updated = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
                unchanged = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
            }
        }

        var affected = await CountAffectedBindingsAsync(versionId, againstVersionId, ct)
            .ConfigureAwait(false);

        return new TacVersionDiff(added, removed, updated, unchanged, affected);
    }

    /// <summary>
    /// How many active bindings sit on a TAC this version changes.
    /// </summary>
    /// <remarks>
    /// This is the number that decides whether a diff is worth a human's attention. The counts
    /// above describe the file; this one describes the effect on what people will see.
    /// </remarks>
    private async Task<long> CountAffectedBindingsAsync(
        int versionId, int againstVersionId, CancellationToken ct)
    {
        var fingerprint = "cityHash64(" + string.Join(", ", Columns.Skip(1)) + ")";

        var sql = $"""
            WITH changed AS (
                SELECT tac FROM (
                    SELECT tac, {fingerprint} AS fp FROM {_database}.tac_all
                    WHERE version_id IN ({versionId}, {againstVersionId})
                    GROUP BY tac, fp
                )
                GROUP BY tac
                HAVING count() = 1
            )
            SELECT count()
            FROM {_database}.binding_current FINAL
            WHERE active = 1 AND tac IN (SELECT tac FROM changed)
            """;

        var value = await ScalarAsync(sql, ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task ActivateAsync(int versionId, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();

        // One insert. The view reads the newest row, so the switch is atomic from a reader's
        // point of view: a query either sees the old version or the new one, never a mixture.
        command.CommandText =
            $"INSERT INTO {_database}.tac_active (singleton, version_id, activated_at) "
            + $"VALUES (1, {versionId}, now64(3))";

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        LogActivated(versionId);
    }

    public async Task DropVersionAsync(int versionId, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"ALTER TABLE {_database}.tac_all DROP PARTITION {versionId}";

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<object?> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static string Truncate(string text)
    {
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        var line = newline >= 0 ? text[..newline] : text;
        return line.Length > 400 ? line[..400] : line;
    }
}
