using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// What an administrator reads before activating a GSMA version: the newest lastUpdatedDate in the
/// file, and how many active bindings the new version would change.
/// </summary>
/// <remarks>
/// <para>
/// Both were wrong on the 4 October 2026 file. The newest date was a string max - "31-Oct-2025" beats
/// "02-Oct-2026" alphabetically - and the affected count kept the TACs with one fingerprint across the
/// two versions, which are the unchanged ones: 113,038,260 bindings reported for 153 changed TACs.
/// </para>
/// <para>Against the real ClickHouse, through the real store, in a scratch database.</para>
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class TacVersionStoreTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private const string User = "sqm_ingest";
    private const string Password = "sqm_dev";

    private readonly string _database = "itest_tacver_" + Guid.NewGuid().ToString("N")[..12];
    private readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5) })
    {
        Timeout = TimeSpan.FromMinutes(2),
    };
    private string? _unavailable;

    public async ValueTask InitializeAsync()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}")));

        try
        {
            if (await QueryAsync("EXISTS TABLE sqm.tac_all") != "1")
            {
                _unavailable = "ClickHouse is up but has no sqm schema to copy";
                return;
            }

            await QueryAsync($"CREATE DATABASE {_database}");
            await QueryAsync($"CREATE TABLE {_database}.tac_all AS sqm.tac_all");
            await QueryAsync($"CREATE TABLE {_database}.binding_current AS sqm.binding_current");

            // Version 1: A, B and C. Version 2: A as it was, B with new bands, C gone, D new.
            // D's date is the newest, and the one a string max gets wrong.
            await QueryAsync($"""
                INSERT INTO {_database}.tac_all (version_id, tac, manufacturer, lastUpdatedDate, bandDetails) VALUES
                    (1, '35000001', 'Samsung', '31-Oct-2025', 'GSM 900'),
                    (1, '35000002', 'Apple',   '02-Jan-2026', 'LTE'),
                    (1, '35000003', 'Nokia',   '15-Mar-2024', 'GSM 900'),
                    (2, '35000001', 'Samsung', '31-Oct-2025', 'GSM 900'),
                    (2, '35000002', 'Apple',   '14-Sep-2026', 'LTE, NR'),
                    (2, '35000004', 'Xiaomi',  '02-Oct-2026', 'LTE')
                """);

            // Active: 5 on A (unchanged), 3 on B (changed), 2 on C (removed), 1 on D (added).
            // Four removed bindings on B, which no longer show anywhere, must not count.
            await QueryAsync($"""
                INSERT INTO {_database}.binding_current (msisdn, imsi, imei, active, last_change_seq, last_change_date) VALUES
                    (9120000001, 432110000000001, '35000001000001', 1, 1, '2026-05-01'),
                    (9120000002, 432110000000002, '35000001000002', 1, 1, '2026-05-01'),
                    (9120000003, 432110000000003, '35000001000003', 1, 1, '2026-05-01'),
                    (9120000004, 432110000000004, '35000001000004', 1, 1, '2026-05-01'),
                    (9120000005, 432110000000005, '35000001000005', 1, 1, '2026-05-01'),
                    (9120000006, 432110000000006, '35000002000001', 1, 1, '2026-05-01'),
                    (9120000007, 432110000000007, '35000002000002', 1, 1, '2026-05-01'),
                    (9120000008, 432110000000008, '35000002000003', 1, 1, '2026-05-01'),
                    (9120000009, 432110000000009, '35000003000001', 1, 1, '2026-05-01'),
                    (9120000010, 432110000000010, '35000003000002', 1, 1, '2026-05-01'),
                    (9120000011, 432110000000011, '35000004000001', 1, 1, '2026-05-01'),
                    (9120000012, 432110000000012, '35000002000004', 0, 2, '2026-05-02'),
                    (9120000013, 432110000000013, '35000002000005', 0, 2, '2026-05-02'),
                    (9120000014, 432110000000014, '35000002000006', 0, 2, '2026-05-02'),
                    (9120000015, 432110000000015, '35000002000007', 0, 2, '2026-05-02')
                """);
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
    public async Task The_newest_update_is_read_as_a_date_not_as_text()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");

        var check = await Store().CheckVersionAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal("02-Oct-2026", check.LatestUpdateDate);
    }

    [Fact]
    public async Task Affected_bindings_are_the_active_ones_on_tacs_added_removed_or_changed()
    {
        Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");

        var diff = await Store().DiffAsync(2, 1, TestContext.Current.CancellationToken);

        Assert.Equal((1, 1, 1, 1), (diff.Added, diff.Removed, diff.Updated, diff.Unchanged));
        // B's three, C's two and D's one; never A's five, never B's removed four.
        Assert.Equal(6, diff.AffectedActiveBindings);
    }

    private ClickHouseTacVersionStore Store() => new(
        new Options<ClickHouseOptions>(new ClickHouseOptions
        {
            ConnectionString = $"Host=localhost;Port=18123;Database={_database};Username={User};Password={Password}",
        }),
        new Factory(),
        NullLogger<ClickHouseTacVersionStore>.Instance);

    private async Task<string> QueryAsync(string sql)
    {
        using var response = await _http.PostAsync(
            Endpoint, new StringContent(sql, Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return response.IsSuccessStatusCode ? body.Trim() : throw new InvalidOperationException($"ClickHouse refused: {body}");
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
    }

    private sealed class Options<T>(T value) : IOptions<T> where T : class
    {
        public T Value { get; } = value;
    }
}
