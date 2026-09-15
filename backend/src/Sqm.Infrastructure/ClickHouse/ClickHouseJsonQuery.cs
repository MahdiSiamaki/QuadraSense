using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClickHouse.Client.ADO;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>
/// Runs a query over ClickHouse's HTTP interface and returns the rows <i>and</i> what they cost.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the ADO driver.</b> Two things this needs are not available through it, and both
/// were learned the expensive way in this project.
/// </para>
/// <para>
/// First, <c>rows_read</c>. ClickHouse reports it in the <c>statistics</c> object of the
/// <c>JSONCompact</c> format, in the same response as the data. The alternative is
/// <c>system.query_log</c>, which lags by its flush interval - a search that had just run reported
/// <c>rowsExamined: 0</c> - and forcing a flush costs about 500 ms of server work against a 20 ms
/// query. Reading it from the response costs nothing and is exact.
/// </para>
/// <para>
/// Second, per-query settings. <c>CustomSettings</c>, <c>set_*</c> in the connection string and a
/// trailing <c>SETTINGS</c> clause were each tried during the memory investigation and each
/// silently did nothing; URL query parameters are the mechanism that works. See
/// <c>docs/architecture/11-clickhouse-memory.md</c> section 3.
/// </para>
/// <para>
/// Used only for the IMSI search. The rest of the store stays on the driver, which is a better fit
/// where the cost of a query is already understood and fixed.
/// </para>
/// </remarks>
internal sealed class ClickHouseJsonQuery
{
    private readonly Uri _endpoint;
    private readonly string _database;
    private readonly AuthenticationHeaderValue? _authentication;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Builds the runner from the same connection string the driver uses.</summary>
    public ClickHouseJsonQuery(string connectionString, IHttpClientFactory httpClientFactory)
    {
        var builder = new ClickHouseConnectionStringBuilder(connectionString);

        _endpoint = new Uri(
            $"{(builder.Protocol is "https" ? "https" : "http")}://{builder.Host}:{builder.Port}/");
        _database = string.IsNullOrWhiteSpace(builder.Database) ? "default" : builder.Database;
        _httpClientFactory = httpClientFactory;

        if (!string.IsNullOrWhiteSpace(builder.Username))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{builder.Username}:{builder.Password}"));
            _authentication = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    /// <summary>Runs a query and returns its rows with the server's own accounting.</summary>
    /// <param name="sql">SQL using ClickHouse's <c>{name:Type}</c> parameter syntax.</param>
    /// <param name="parameters">Values for those parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ClickHouseJsonResult> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["query"] = sql,
            ["database"] = _database,
            ["default_format"] = "JSONCompact",

            // One thread. ClickHouse builds one hash table per thread, and for a search that reads
            // tens of thousands of rows there is nothing to parallelise - measured at the same
            // duration either way on this shape of query. It also keeps a burst of concurrent
            // searches from multiplying memory on a node that shares it with the import worker.
            ["max_threads"] = "1",

            // A search that needs more than this is not a search. The cap turns a runaway query
            // into an error the caller sees rather than pressure the whole server feels.
            ["max_memory_usage"] = "500000000",

        };


        foreach (var (key, value) in parameters)
        {
            query["param_" + key] = value;
        }

        var url = new UriBuilder(_endpoint)
        {
            Query = string.Join('&', query.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")),
        }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (_authentication is not null)
        {
            request.Headers.Authorization = _authentication;
        }

        var client = _httpClientFactory.CreateClient(ClickHouseAnalyticsStore.HttpClientName);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // ClickHouse returns a multi-line stack; the first line is the part a caller can act on.
            var firstLine = body.Split('\n', 2)[0];
            throw new InvalidOperationException(
                $"ClickHouse returned {(int)response.StatusCode}: {firstLine}");
        }

        return Parse(body);
    }

    private static ClickHouseJsonResult Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var rows = new List<JsonElement>();
        if (root.TryGetProperty("data", out var data))
        {
            foreach (var row in data.EnumerateArray())
            {
                rows.Add(row.Clone());
            }
        }

        long rowsRead = 0;
        double elapsed = 0;

        if (root.TryGetProperty("statistics", out var statistics))
        {
            if (statistics.TryGetProperty("rows_read", out var read))
            {
                rowsRead = read.GetInt64();
            }

            if (statistics.TryGetProperty("elapsed", out var seconds))
            {
                elapsed = seconds.GetDouble();
            }
        }

        return new ClickHouseJsonResult(rows, rowsRead, (long)Math.Round(elapsed * 1000));
    }
}

/// <summary>Rows from a query, and what the server says they cost.</summary>
/// <remarks>
/// <c>RowsRead</c> is the number that distinguishes an indexed lookup from a scan on this dataset -
/// tens of thousands against 295 million - far more reliably than elapsed time, which also moves
/// with cache warmth.
/// </remarks>
internal sealed record ClickHouseJsonResult(
    IReadOnlyList<JsonElement> Rows, long RowsRead, long ElapsedMs)
{
    /// <summary>Reads a column as a string, treating JSON null as null.</summary>
    public static string? Text(JsonElement row, int index)
    {
        var value = row[index];
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    /// <summary>Reads a column as a string, mapping null and empty alike to null.</summary>
    public static string? NullIfEmpty(JsonElement row, int index)
    {
        var value = Text(row, index);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Reads a 64-bit unsigned column.
    /// </summary>
    /// <remarks>
    /// ClickHouse serialises UInt64 as a JSON <i>string</i> in every JSON format, because the value
    /// can exceed what a JSON number safely represents. Reading it as a number returns nothing.
    /// </remarks>
    public static ulong UInt64(JsonElement row, int index)
    {
        var value = row[index];
        return value.ValueKind == JsonValueKind.String
            ? ulong.Parse(value.GetString()!, CultureInfo.InvariantCulture)
            : value.GetUInt64();
    }

    /// <summary>Reads a 32-bit integer column, which may arrive as a string.</summary>
    public static int Int32(JsonElement row, int index)
    {
        var value = row[index];
        return value.ValueKind == JsonValueKind.String
            ? int.Parse(value.GetString()!, CultureInfo.InvariantCulture)
            : value.GetInt32();
    }

    /// <summary>Reads a 64-bit signed column, which may arrive as a string.</summary>
    /// <remarks>
    /// Int64 and UInt64 both serialise as JSON strings when the value is large enough to lose
    /// precision as a double, and ClickHouse decides that per type rather than per value - so a
    /// column that arrived as a number yesterday can arrive as a string today after a cast.
    /// </remarks>
    public static long Int64(JsonElement row, int index)
    {
        var value = row[index];
        return value.ValueKind switch
        {
            JsonValueKind.Null => 0,
            JsonValueKind.String => long.Parse(value.GetString()!, CultureInfo.InvariantCulture),
            _ => value.GetInt64(),
        };
    }

    /// <summary>Reads a floating-point column, treating null as zero.</summary>
    /// <remarks>
    /// Null here is division by zero upstream - a percentage against a population of nothing -
    /// which the SQL guards with nullIf. Zero is the honest rendering of "no change to report".
    /// </remarks>
    public static double Double(JsonElement row, int index)
    {
        var value = row[index];
        return value.ValueKind switch
        {
            JsonValueKind.Null => 0,
            JsonValueKind.String => double.Parse(value.GetString()!, CultureInfo.InvariantCulture),
            _ => value.GetDouble(),
        };
    }

    /// <summary>Reads a Date column, or null.</summary>
    public static DateOnly? Date(JsonElement row, int index)
    {
        var value = Text(row, index);
        return string.IsNullOrEmpty(value)
            ? null
            : DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
