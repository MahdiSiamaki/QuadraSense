using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Sqm.Infrastructure.ClickHouse;

namespace Sqm.Integration.Tests;

/// <summary>
/// The read path's limits are enforced by the server, and it cannot write.
/// </summary>
/// <remarks>
/// Against the real ClickHouse, through <see cref="ClickHouseJsonQuery"/> as the API uses it,
/// connected as the API's own user. The queries read <c>numbers()</c> or a scratch table made for
/// the run, so nothing here depends on - or touches - the imported data.
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class ClickHouseQueryBudgetTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";

    private readonly string _database = "itest_budget_" + Guid.NewGuid().ToString("N")[..12];
    private readonly HttpClient _admin = new() { Timeout = TimeSpan.FromMinutes(1) };
    private string? _unavailable;

    public async ValueTask InitializeAsync()
    {
        _admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("sqm_ingest:sqm_dev")));

        try
        {
            await AdminAsync($"CREATE DATABASE {_database}");

            // A million keys, 8,192 to a granule: a key read is one or two granules, a scan all 123.
            await AdminAsync($"""
                CREATE TABLE {_database}.keyed (k UInt64, v String)
                ENGINE = MergeTree ORDER BY k SETTINGS index_granularity = 8192
                """);
            await AdminAsync($"INSERT INTO {_database}.keyed SELECT number, toString(number) FROM numbers(1000000)");
            await AdminAsync($"OPTIMIZE TABLE {_database}.keyed FINAL");
        }
        catch (HttpRequestException ex)
        {
            _unavailable = $"no ClickHouse at {Endpoint}: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_unavailable is null)
        {
            await AdminAsync($"DROP DATABASE IF EXISTS {_database}");
        }

        _admin.Dispose();
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });
    }

    /// <summary>The runner as the API builds it, as the API's user.</summary>
    private ClickHouseJsonQuery Runner() => new(
        $"Host=localhost;Port=18123;Database={_database};Username=sqm_app;Password=sqm_dev",
        new Factory());

    private async Task<string> AdminAsync(string sql)
    {
        using var response = await _admin.PostAsync(
            Endpoint, new StringContent(sql, Encoding.UTF8, "text/plain"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return response.IsSuccessStatusCode
            ? body.Trim()
            : throw new InvalidOperationException($"ClickHouse refused: {body}");
    }

    private static readonly Dictionary<string, string> None = [];

    [Fact]
    public async Task A_read_within_budget_returns_its_rows_and_what_it_read()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var result = await Runner().ExecuteAsync(
            "SELECT count() FROM numbers(1000)", None, TestContext.Current.CancellationToken,
            new QueryBudget(MaxRowsToRead: 10_000, MaxExecutionSeconds: 10, MaxResultRows: 10));

        Assert.Equal(1000, ClickHouseJsonResult.Int64(result.Rows[0], 0));
        Assert.Equal(1000, result.RowsRead);
    }

    [Fact]
    public async Task A_read_past_its_row_budget_is_stopped_by_the_server()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ex = await Assert.ThrowsAsync<QueryBudgetExceededException>(() => Runner().ExecuteAsync(
            "SELECT count() FROM numbers(10000000)", None, TestContext.Current.CancellationToken,
            new QueryBudget(MaxRowsToRead: 1000)));

        Assert.Equal("rows read", ex.Limit);
        Assert.Equal(158, ex.Code);
    }

    [Fact]
    public async Task A_query_past_its_time_budget_is_stopped_by_the_server()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var clock = Stopwatch.StartNew();

        // Unbounded, it would sum a hundred billion numbers.
        var ex = await Assert.ThrowsAsync<QueryBudgetExceededException>(() => Runner().ExecuteAsync(
            "SELECT sum(number) FROM numbers(100000000000)", None, TestContext.Current.CancellationToken,
            new QueryBudget(MaxExecutionSeconds: 1)));

        Assert.Equal("execution time", ex.Limit);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"stopped after {clock.Elapsed}");
    }

    [Fact]
    public async Task A_result_past_its_size_budget_is_refused_rather_than_cut_short()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        // A result truncated at five rows would read as the whole answer.
        var ex = await Assert.ThrowsAsync<QueryBudgetExceededException>(() => Runner().ExecuteAsync(
            "SELECT number FROM numbers(100)", None, TestContext.Current.CancellationToken,
            new QueryBudget(MaxResultRows: 5)));

        Assert.Equal("result size", ex.Limit);
    }

    [Fact]
    public async Task Nothing_sent_through_the_read_path_can_write_to_a_table()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        var insert = await Assert.ThrowsAsync<InvalidOperationException>(() => Runner().ExecuteAsync(
            $"INSERT INTO {_database}.keyed VALUES (1, 'x')", None, ct));
        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() => Runner().ExecuteAsync(
            $"ALTER TABLE {_database}.keyed DELETE WHERE 1", None, ct));

        Assert.Contains("READONLY", insert.Message, StringComparison.Ordinal);
        Assert.Contains("READONLY", delete.Message, StringComparison.Ordinal);
        Assert.IsNotType<QueryBudgetExceededException>(insert);
        Assert.Equal("1000000", await AdminAsync($"SELECT count() FROM {_database}.keyed"));
    }

    [Fact]
    public async Task An_estimate_tells_a_key_read_from_a_scan_before_either_runs()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var runner = Runner();

        var keyed = await runner.EstimateAsync(
            $"SELECT * FROM {_database}.keyed WHERE k = {{k:UInt64}}",
            new Dictionary<string, string> { ["k"] = "500000" }, ct);
        var scan = await runner.EstimateAsync(
            $"SELECT * FROM {_database}.keyed WHERE v = {{v:String}}",
            new Dictionary<string, string> { ["v"] = "500000" }, ct);

        Assert.Equal($"{_database}.keyed", Assert.Single(keyed.Tables).Table);
        Assert.InRange(keyed.Rows, 1, 2 * 8192);
        Assert.Equal(1_000_000, scan.Rows);
        Assert.True(scan.Marks > 100 * keyed.Marks, $"scan {scan.Marks} marks, key {keyed.Marks}");
    }

    [Theory]
    [InlineData("Code: 158. DB::Exception: Limit for rows (controlled by 'max_rows_to_read' setting) exceeded", "rows read")]
    [InlineData("Code: 159. DB::Exception: Timeout exceeded: elapsed 1000.04 ms, maximum: 1000 ms", "execution time")]
    [InlineData("Code: 241. DB::Exception: Query memory limit exceeded: would use 13.58 MiB", "memory")]
    [InlineData("Code: 396. DB::Exception: Limit for result exceeded, max rows: 5.00", "result size")]
    public void A_limit_the_server_names_becomes_a_budget_failure(string firstLine, string limit)
    {
        var ex = Assert.IsType<QueryBudgetExceededException>(
            ClickHouseJsonQuery.Failure(500, firstLine + "\nstack trace"));

        Assert.Equal(limit, ex.Limit);
        Assert.DoesNotContain("stack trace", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// In JSON formats the server puts the error inside the JSON document, and the message is
    /// read from there - the body's first line is only <c>{</c>.
    /// </summary>
    [Fact]
    public void An_error_written_into_the_json_document_is_read_from_it()
    {
        // As ClickHouse 25.8 sends it for a JSONCompact request, captured from the real server.
        const string Body = "{\n\t\"meta\":\n\t[\n\n\t],\n\n\t\"data\":\n\t[\n\n\t],\n\n\t\"rows\": 0,\n\n\t"
            + "\"exception\": \"Code: 158. DB::Exception: Limit for rows (controlled by 'max_rows_to_read' "
            + "setting) exceeded, max rows: 1.00 thousand, current rows: 10.00 million. (TOO_MANY_ROWS)\"\n}\n";

        var fromHeader = Assert.IsType<QueryBudgetExceededException>(ClickHouseJsonQuery.Failure(500, Body, "158"));
        var fromText = Assert.IsType<QueryBudgetExceededException>(ClickHouseJsonQuery.Failure(500, Body));
        var cutOff = Assert.IsType<QueryBudgetExceededException>(ClickHouseJsonQuery.Failure(500, Body[..^20]));

        Assert.Contains("Limit for rows", fromHeader.Message, StringComparison.Ordinal);
        Assert.Equal("rows read", fromText.Limit);
        Assert.Equal(158, cutOff.Code);
        Assert.DoesNotContain("returned 500: {", fromHeader.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Code: 60. DB::Exception: Table sqm.nope does not exist. (UNKNOWN_TABLE)")]
    [InlineData("Code: 164. DB::Exception: sqm_app: Cannot execute query in readonly mode. (READONLY)")]
    [InlineData("<html>502 Bad Gateway</html>")]
    public void Any_other_failure_stays_a_plain_one(string firstLine)
    {
        var ex = ClickHouseJsonQuery.Failure(500, firstLine);

        Assert.IsNotType<QueryBudgetExceededException>(ex);
        Assert.IsType<InvalidOperationException>(ex);
    }
}
