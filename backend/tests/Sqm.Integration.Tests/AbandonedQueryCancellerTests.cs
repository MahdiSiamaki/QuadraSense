using System.Net.Http.Headers;
using System.Text;
using ClickHouse.Client.ADO;
using Sqm.Infrastructure.ClickHouse;

namespace Sqm.Integration.Tests;

/// <summary>
/// A query the API stops waiting for does not go on running on the server.
/// </summary>
/// <remarks>
/// Against the real ClickHouse, as the API's own user, through the driver the API uses. The query
/// scans <c>numbers()</c> and matches nothing, so it would run for minutes and touches no data.
/// Each test both proves its claim and shows the opposite without the handler, so it cannot pass
/// for an unrelated reason (CLAUDE.md, "Prove it").
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class AbandonedQueryCancellerTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private const string Connection = "Host=localhost;Port=18123;Database=default;Username=sqm_app;Password=sqm_dev";

    private readonly HttpClient _admin = new() { Timeout = TimeSpan.FromMinutes(1) };
    private string? _unavailable;

    public async ValueTask InitializeAsync()
    {
        _admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("sqm_ingest:sqm_dev")));

        try
        {
            await AdminAsync("SELECT 1");
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
            await AdminAsync("KILL QUERY WHERE query LIKE '%abandoned-query-test%' AND query NOT LIKE '%KILL%' SYNC");
        }

        _admin.Dispose();
    }

    private sealed class Factory(bool withCanceller) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            HttpMessageHandler handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            };

            if (withCanceller)
            {
                handler = new AbandonedQueryCanceller { InnerHandler = handler };
            }

            return new HttpClient(handler);
        }
    }

    private async Task<string> AdminAsync(string sql)
    {
        using var response = await _admin.PostAsync(
            Endpoint, new StringContent(sql, Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return response.IsSuccessStatusCode ? body.Trim() : throw new InvalidOperationException($"ClickHouse refused: {body}");
    }

    /// <summary>Whether a query carrying the marker is running, waiting up to a few seconds for it to go.</summary>
    private async Task<bool> StillRunningAfterAsync(string marker, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var running = await AdminAsync(
                $"SELECT count() FROM system.processes WHERE query LIKE '%{marker}%' AND query NOT LIKE '%system.processes%'");
            if (running == "0")
            {
                return false;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return true;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Starts the slow query, gives up on it after two seconds, and says whether it went.</summary>
    private async Task<bool> AbandonAsync(bool withCanceller)
    {
        var marker = $"abandoned-query-test-{Guid.NewGuid():N}";

        await using var connection = new ClickHouseConnection(Connection, new Factory(withCanceller), "clickhouse");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count() FROM numbers(1000000000000) WHERE number % 7 = 9 /* {marker} */ SETTINGS max_execution_time = 120";

        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteScalarAsync(giveUp.Token));

        return await StillRunningAfterAsync(marker, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_query_the_api_gave_up_on_is_killed_on_the_server()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        Assert.False(await AbandonAsync(withCanceller: true), "the abandoned query was still running 5 s later");
    }

    [Fact]
    public async Task Without_the_canceller_the_server_runs_an_abandoned_query_on()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        // The control: this is the behaviour the canceller exists for. If it ever stops holding,
        // the test above proves nothing and should be revisited.
        Assert.True(await AbandonAsync(withCanceller: false), "ClickHouse stopped the query by itself");
    }

    [Fact]
    public void A_query_is_named_once_and_a_kill_carries_the_user_but_not_the_query()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "http://localhost:18123/?database=sqm&user=sqm_app&password=x&param_tac=35000000&max_execution_time=30");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "c3FtX2FwcDp4");

        var id = AbandonedQueryCanceller.NameQuery(request);
        Assert.NotNull(id);
        Assert.Contains($"query_id={id}", request.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal(id, AbandonedQueryCanceller.NameQuery(request));

        using var kill = AbandonedQueryCanceller.KillRequest(request, id);
        var query = kill.RequestUri!.Query;
        Assert.Contains("user=sqm_app", query, StringComparison.Ordinal);
        Assert.Contains("database=sqm", query, StringComparison.Ordinal);
        Assert.DoesNotContain("query_id", query, StringComparison.Ordinal);
        Assert.DoesNotContain("param_tac", query, StringComparison.Ordinal);
        Assert.Equal(request.Headers.Authorization, kill.Headers.Authorization);
    }

    [Fact]
    public void A_caller_id_that_could_not_be_put_into_sql_is_left_alone()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:18123/?query_id=x'%20OR%201=1");
        Assert.Null(AbandonedQueryCanceller.NameQuery(request));
    }
}
