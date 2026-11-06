using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>What the worker reads to decide its lanes, as the worker's own user (ADR-021).</summary>
/// <remarks>
/// Against the real server, because the point is that its system tables say these things to this user.
/// Skips, saying so, without one. Read-only.
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class EngineCapacityTests : IAsyncLifetime
{
    private const long Limit = 123_456_789;

    private string? _unavailable;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });
    }

    private static ClickHouseIngestionStore Store(string database) => new(
        Options.Create(new ClickHouseOptions
        {
            ConnectionString = $"Host=localhost;Port=18123;Database={database};Username=sqm_ingest;Password=sqm_dev",
            BoundedMemoryBytes = Limit,
        }),
        new Factory(),
        NullLogger<ClickHouseIngestionStore>.Instance);

    public async ValueTask InitializeAsync()
    {
        try
        {
            await Store("sqm").ScalarAsync("SELECT 1", Ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _unavailable = $"no analytics store on localhost:18123: {ex.Message}";
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_engine_tells_the_workers_user_what_it_has_to_give()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        var capacity = await Store("sqm").ReadCapacityAsync(Ct);

        Assert.True(capacity.MemoryBytes > 1L << 30, $"{capacity.MemoryBytes} bytes");
        Assert.True(capacity.Cores >= 1, $"{capacity.Cores} cores");
        Assert.InRange(capacity.Lanes, 1, Math.Max(1, capacity.Cores));
    }

    [Fact]
    public async Task The_peak_is_learned_from_the_runs_on_this_database()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        var ran = await Store("sqm").ScalarAsync("""
            SELECT count() FROM system.query_log
            WHERE event_date >= today() - 14 AND type = 'QueryFinish' AND query_kind = 'Insert'
              AND match(query, '^\\s*INSERT INTO sqm\\.(quality_chunk|risk_|planning_)')
            """, Ct);

        if (ran == 0)
        {
            Assert.Skip("no Risk or Planning run in the last 14 days to learn from");
        }

        var capacity = await Store("sqm").ReadCapacityAsync(Ct);

        Assert.NotEqual(Limit, capacity.StepPeakBytes);
        Assert.True(capacity.StepPeakBytes > 1L << 20, $"{capacity.StepPeakBytes} bytes");
    }

    [Fact]
    public async Task What_the_api_has_read_lately_is_counted_and_what_the_worker_reads_is_not()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        var store = Store("sqm");
        var window = TimeSpan.FromSeconds(5);

        // Past the last test's reads: this collection runs alone, and the log holds them until it flushes.
        await Task.Delay(window + TimeSpan.FromSeconds(1), Ct);

        // The worker's own user reads more than the API does, and the API a known amount.
        await store.ScalarAsync("SELECT count() FROM numbers(300000)", Ct);

        using var api = new HttpClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("sqm_app:sqm_dev")));
        using var body = new StringContent("SELECT count() FROM numbers(200000)", Encoding.UTF8, "text/plain");
        using var read = await api.PostAsync(new Uri("http://localhost:18123/"), body, Ct);
        read.EnsureSuccessStatusCode();

        await store.ScalarAsync("SYSTEM FLUSH LOGS", Ct);

        Assert.InRange(await store.ReadPeoplesRowsAsync(window, Ct), 200_000, 299_999);
    }

    [Fact]
    public async Task With_no_run_to_learn_from_the_limit_is_all_there_is_to_go_by()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        // Another database's runs say nothing about this one.
        var capacity = await Store("system").ReadCapacityAsync(Ct);

        Assert.Equal(Limit, capacity.StepPeakBytes);
    }
}
