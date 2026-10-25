using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// The initial dump lands as it did on the first machine: every row in <c>binding_snapshot</c>,
/// current state seeded from it, the two ordered copies filled by their views.
/// </summary>
/// <remarks>
/// Against the real ClickHouse, in a scratch database whose tables copy the live ones and whose two
/// materialized views are the live ones' DDL pointed at it. The target state - active, change
/// sequence 0, no change date - is what a dump-only binding looks like in the first machine's
/// binding_current (read 2026-10-24).
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class DumpLoadStoreTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private const string User = "sqm_ingest";
    private const string Password = "sqm_dev";

    // The dump's own column order. Row 2's IMEI is the feed's "000000" sentinel and row 3's number
    // is four digits: the dump carries such rows, and the first machine loaded them as they were.
    private const string Dump = "imei,imsi,msisdn\n"
        + "35004012000001,432110000000001,9120000001\n"
        + "000000,432110000000002,9120000002\n"
        + "86453906000003,432110000000003,1234\n";

    private readonly string _database = "itest_dump_" + Guid.NewGuid().ToString("N")[..12];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private string? _unavailable;

    public async ValueTask InitializeAsync()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}")));

        try
        {
            if (await QueryAsync("EXISTS TABLE sqm.mv_binding_by_imsi") != "1")
            {
                _unavailable = "ClickHouse is up but has no sqm schema to copy";
                return;
            }

            await QueryAsync($"CREATE DATABASE {_database}");
            foreach (var table in new[] { "binding_snapshot", "binding_current", "binding_by_imsi", "binding_by_imei" })
            {
                await QueryAsync($"CREATE TABLE {_database}.{table} AS sqm.{table}");
            }

            foreach (var view in new[] { "mv_binding_by_imsi", "mv_binding_by_imei" })
            {
                var ddl = await QueryAsync($"SHOW CREATE TABLE sqm.{view} FORMAT TSVRaw");
                await QueryAsync(ddl.Replace("sqm.", _database + ".", StringComparison.Ordinal));
            }
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
            await QueryAsync($"DROP DATABASE IF EXISTS {_database}");
        }

        _http.Dispose();
    }

    [Fact]
    public async Task Every_row_lands_in_the_snapshot_by_header_name_and_seeds_current_state()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        var store = Store();

        var written = await store.LoadDumpAsync(Stream(Dump), null, TestContext.Current.CancellationToken);
        Assert.Equal(3, written);

        Assert.Equal(
            "1234\t432110000000003\t86453906000003\n"
            + "9120000001\t432110000000001\t35004012000001\n"
            + "9120000002\t432110000000002\t000000",
            await QueryAsync($"SELECT msisdn, imsi, imei FROM {_database}.binding_snapshot ORDER BY msisdn FORMAT TSV"));

        var seeded = await store.SeedCurrentFromDumpAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, seeded);

        Assert.Equal(
            "1234\t86453906000003\t1\t0\t\\N\t86453906\n"
            + "9120000001\t35004012000001\t1\t0\t\\N\t35004012\n"
            + "9120000002\t000000\t1\t0\t\\N\t",
            await QueryAsync(
                "SELECT msisdn, imei, active, last_change_seq, last_change_date, tac "
                + $"FROM {_database}.binding_current ORDER BY msisdn FORMAT TSV"));

        Assert.Equal("3", await QueryAsync($"SELECT count() FROM {_database}.binding_by_imsi"));
        Assert.Equal("3", await QueryAsync($"SELECT count() FROM {_database}.binding_by_imei"));
    }

    [Fact]
    public async Task A_number_that_is_not_a_number_fails_the_load()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        var broken = Dump + "35004012000004,432110000000004,not-a-number\n";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Store().LoadDumpAsync(Stream(broken), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Clearing_empties_the_snapshot_current_state_and_both_copies()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        var store = Store();
        await store.LoadDumpAsync(Stream(Dump), null, TestContext.Current.CancellationToken);
        await store.SeedCurrentFromDumpAsync(TestContext.Current.CancellationToken);

        await store.ClearDumpAsync(TestContext.Current.CancellationToken);

        foreach (var table in new[] { "binding_snapshot", "binding_current", "binding_by_imsi", "binding_by_imei" })
        {
            Assert.Equal("0", await QueryAsync($"SELECT count() FROM {_database}.{table}"));
        }
    }

    /// <summary>
    /// The per-statement memory cap is the configured one, as the server received it.
    /// </summary>
    /// <remarks>
    /// The department VM's dashboard rebuild needed more than the laptop's 1.2 GB (2026-10-25), so
    /// the cap became ClickHouse:BoundedMemoryBytes. A setting that never arrives looks exactly like
    /// a generous one, so this reads it back from the server's own query log.
    /// </remarks>
    [Fact]
    public async Task Statements_run_under_the_configured_memory_cap()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        var store = Store(boundedMemoryBytes: 1_234_567_890);
        await store.LoadDumpAsync(Stream(Dump), null, TestContext.Current.CancellationToken);
        await store.SeedCurrentFromDumpAsync(TestContext.Current.CancellationToken);

        // The query log is flushed every 7.5 s.
        var cap = string.Empty;
        for (var attempt = 0; attempt < 20 && cap.Length == 0; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            cap = await QueryAsync(
                "SELECT Settings['max_memory_usage'] FROM system.query_log "
                + $"WHERE type = 'QueryFinish' AND query LIKE 'INSERT INTO {_database}.binding_current%' "
                + "AND event_time > now() - INTERVAL 10 MINUTE LIMIT 1");
        }

        Assert.Equal("1234567890", cap);
    }

    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    private sealed class Factory : IHttpClientFactory
    {
        // As the application registers it: the ClickHouse client asks for compressed responses.
        public HttpClient CreateClient(string name) => new(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
    }

    private ClickHouseIngestionStore Store(long boundedMemoryBytes = 1_200_000_000) => new(
        Options.Create(new ClickHouseOptions
        {
            ConnectionString =
                $"Host=localhost;Port=18123;Database={_database};Username={User};Password={Password}",
            BoundedMemoryBytes = boundedMemoryBytes,
        }),
        new Factory(),
        NullLogger<ClickHouseIngestionStore>.Instance);

    private async Task<string> QueryAsync(string sql)
    {
        using var response = await _http.PostAsync(
            Endpoint, new StringContent(sql, Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(body);
        }

        return body.TrimEnd('\n').ToString(CultureInfo.InvariantCulture);
    }
}
