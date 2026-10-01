using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// Days that arrive late, out of order, or corrected leave current state exactly as if every day
/// had arrived on time.
/// </summary>
/// <remarks>
/// <para>
/// Against the real ClickHouse, through the real <see cref="ClickHouseIngestionStore"/>, in a
/// scratch database created for the run and dropped after it. Nothing here is lifted SQL: the
/// statements that run are the ones an import runs.
/// </para>
/// <para>
/// The truth each case is checked against is computed here, independently and in the plainest
/// possible way: for every binding, its last event by date decides; with no event left, the
/// initial dump does.
/// </para>
/// <para>
/// What this does not prove is scale. The late-day fold failed on real data only because a real
/// day touches millions of bindings, which is why it now works through msisdn ranges; that was
/// measured on the real event log (see <c>ClickHouseIngestionStore.FoldFromHistoryAsync</c>).
/// Here the ranges are forced by a range size of two, so the same code path runs on eleven
/// bindings.
/// </para>
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class LateDayFoldTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private const string User = "sqm_ingest";
    private const string Password = "sqm_dev";

    private static readonly DateOnly D1 = new(2026, 5, 1);
    private static readonly DateOnly D2 = new(2026, 5, 2);
    private static readonly DateOnly D3 = new(2026, 5, 3);
    private static readonly DateOnly D4 = new(2026, 5, 4);
    private static readonly DateOnly D5 = new(2026, 5, 5);
    private static readonly DateOnly D6 = new(2026, 5, 6);
    private static readonly DateOnly D7 = new(2026, 5, 7);
    private static readonly DateOnly D8 = new(2026, 5, 8);
    private static readonly DateOnly D9 = new(2026, 5, 9);

    private readonly string _database = "itest_late_" + Guid.NewGuid().ToString("N")[..12];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private string? _unavailable;

    public async ValueTask InitializeAsync()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}")));

        try
        {
            // The scratch tables copy the live ones, so a change to the real DDL is tested here
            // without anyone having to remember to change this file.
            if (await QueryAsync("EXISTS TABLE sqm.binding_event") != "1")
            {
                _unavailable = "ClickHouse is up but has no sqm schema to copy";
                return;
            }

            await QueryAsync($"CREATE DATABASE {_database}");
            foreach (var table in new[]
                     {
                         "binding_event", "binding_current", "binding_snapshot",
                         "agg_change_daily", "agg_change_summary_daily",
                         "agg_sim_change_daily", "agg_device_change_daily",
                     })
            {
                await QueryAsync($"CREATE TABLE {_database}.{table} AS sqm.{table}");
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

    private ClickHouseIngestionStore Store(int keysPerRange) => new(
        Options.Create(new ClickHouseOptions
        {
            ConnectionString =
                $"Host=localhost;Port=18123;Database={_database};Username={User};Password={Password}",
            FoldKeysPerRange = keysPerRange,
        }),
        new Factory(),
        NullLogger<ClickHouseIngestionStore>.Instance);

    private async Task<string> QueryAsync(string sql)
    {
        using var response = await _http.PostAsync(
            Endpoint, new StringContent(sql, Encoding.UTF8, "text/plain"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ClickHouse refused: {body}");
        }

        return body.Trim();
    }

    // ------------------------------------------------------------------ the scenario

    private sealed record Binding(ulong Msisdn, ulong Imsi, string Imei);

    private sealed record Event(Binding Key, bool Add);

    private static Binding K(int n) => new((ulong)(9_120_000_000 + n), (ulong)(432_110_000_000_000 + n), $"3500000000{n:0000}");

    /// <summary>
    /// Eleven bindings, each a case that a fold by arrival order - or by sequence number, which is
    /// arrival order - gets wrong.
    /// </summary>
    private static readonly Binding[] Dump = [K(1), K(5), K(9)];

    private static Dictionary<DateOnly, Event[]> Days(bool correctedDay6) => new()
    {
        [D1] = [new(K(1), false)],
        [D2] = [new(K(2), true), new(K(6), true)],

        // The late day. Arrives after days 4 to 8.
        [D3] =
        [
            new(K(1), true),   // dump, removed on 1, re-added here, removed again on 4: inactive
            new(K(2), false),  // added on 2, removed here: inactive
            new(K(3), true),   // only here: active
            new(K(4), false),  // removed here, added on 4 - which is LATER by date: active
            new(K(9), false),  // in the dump, removed here, nothing after: inactive
        ],
        [D4] = [new(K(1), false), new(K(4), true)],
        [D5] = [new(K(6), false)],

        // Replaced later by a corrected file that no longer mentions K10.
        [D6] = correctedDay6 ? [new(K(11), true)] : [new(K(10), true), new(K(11), true)],

        // 25 and 26 August in miniature: the add arrives a day after its remove.
        [D7] = [new(K(7), true)],
        [D8] = [new(K(7), false)],

        // A normal day after all of it, on the fast path.
        [D9] = [new(K(8), true)],
    };

    /// <summary>The state each binding must end in: last event by date, else the dump.</summary>
    private static Dictionary<Binding, bool> Truth(Dictionary<DateOnly, Event[]> days)
    {
        var state = Dump.ToDictionary(k => k, _ => true);
        foreach (var (_, events) in days.OrderBy(d => d.Key))
        {
            foreach (var e in events)
            {
                state[e.Key] = e.Add;
            }
        }

        // K10 was only ever in the replaced file. It still has a row in binding_current, which
        // must now say inactive rather than keep the withdrawn add.
        state.TryAdd(K(10), false);
        return state;
    }

    /// <summary>Exactly what SqmDailyProcessor does with one file, minus validation and marts.</summary>
    private static async Task ImportAsync(ClickHouseIngestionStore store, DateOnly day, Event[] events)
    {
        var ct = TestContext.Current.CancellationToken;

        var existing = await store.CountEventsForDateAsync(day, ct);
        var sequence = await store.ResolveSequenceForDateAsync(day, ct);

        if (existing > 0)
        {
            await store.RemoveDayAsync(day, ct);
        }

        var csv = new StringBuilder("msisdn,imsi,imei,label\n");
        foreach (var e in events)
        {
            csv.Append(CultureInfo.InvariantCulture,
                $"{e.Key.Msisdn},{e.Key.Imsi},{e.Key.Imei},{(e.Add ? "add" : "remove")}\n");
        }

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv.ToString()));
        await store.LoadDailyEventsAsync(day, sequence, stream, null, ct);
        await store.FoldDayAsync(day, ct);
    }

    [Theory]
    [InlineData(2)]          // several msisdn ranges, as a real late day takes
    [InlineData(1_000_000)]  // one range
    public async Task Late_out_of_order_and_corrected_days_end_exactly_as_if_all_had_arrived_on_time(
        int keysPerRange)
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var store = Store(keysPerRange);

        // The initial dump, as the bulk load left it: in binding_snapshot, and active at version 0.
        var dump = string.Join(", ", Dump.Select(k => $"({k.Msisdn}, {k.Imsi}, '{k.Imei}')"));
        await QueryAsync($"INSERT INTO {_database}.binding_snapshot (msisdn, imsi, imei) VALUES {dump}");
        await QueryAsync($"""
            INSERT INTO {_database}.binding_current (msisdn, imsi, imei, active, last_change_seq, last_change_date)
            SELECT msisdn, imsi, imei, 1, 0, NULL FROM {_database}.binding_snapshot
            """);

        var original = Days(correctedDay6: false);
        var corrected = Days(correctedDay6: true);

        // In order until a gap, then the missing days late - the newer of them first - then a
        // corrected file for a day already loaded, then an ordinary new day.
        foreach (var day in new[] { D1, D2, D4, D5, D6, D8 })
        {
            await ImportAsync(store, day, original[day]);
        }

        await ImportAsync(store, D7, original[D7]);
        await ImportAsync(store, D3, original[D3]);
        await ImportAsync(store, D6, corrected[D6]);
        await ImportAsync(store, D9, corrected[D9]);

        var actual = (await QueryAsync($"""
                SELECT msisdn, imsi, imei, active FROM {_database}.binding_current FINAL
                ORDER BY msisdn FORMAT TSV
                """))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .ToDictionary(
                f => new Binding(ulong.Parse(f[0], CultureInfo.InvariantCulture), ulong.Parse(f[1], CultureInfo.InvariantCulture), f[2]),
                f => f[3] == "1");

        var expected = Truth(corrected);

        foreach (var (key, active) in expected.OrderBy(e => e.Key.Msisdn))
        {
            Assert.True(actual.TryGetValue(key, out var got), $"binding {key.Msisdn} has no row in binding_current");
            Assert.True(active == got, $"binding {key.Msisdn}: expected {(active ? "active" : "inactive")}, found {(got ? "active" : "inactive")}");
        }

        Assert.Equal(expected.Count, actual.Count);

        // The scratch table a replaced day leaves behind is dropped once its fold lands.
        Assert.Equal("0", await QueryAsync($"EXISTS TABLE {_database}.fold_replaced_20260506"));
    }

    /// <summary>
    /// SIM and handset changes are counted exactly, and in memory that grows with a day's rows,
    /// not with the square of what one number carries.
    /// </summary>
    /// <remarks>
    /// The array form of these marts copied each number's removed identifiers once per added one.
    /// On 2026-08-06 one number carried 8,886 handsets in a day, the handset mart needed 1.18 GiB
    /// against a 1.12 GiB cap, and the day failed five times and blocked every day after it. Number
    /// 9 below carries 9,500 removed and 9,500 added - past what the array form can hold.
    /// </remarks>
    [Fact]
    public async Task Sim_and_handset_changes_are_counted_exactly_even_for_a_number_with_thousands_of_devices()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var store = Store(1_000_000);
        var day = new DateOnly(2026, 8, 6);

        var csv = new StringBuilder("msisdn,imsi,imei,label\n");
        void Row(int number, long imsi, string imei, string label) =>
            csv.Append(CultureInfo.InvariantCulture, $"{9_120_000_000 + number},{imsi},{imei},{label}\n");

        Row(1, 1, "35000000000001", "remove"); Row(1, 1, "35000000000002", "add");   // handset change
        Row(2, 2, "35000000000003", "add");                                          // added only
        Row(3, 3, "35000000000004", "remove");                                       // removed only
        Row(4, 4, "35000000000005", "remove"); Row(4, 4, "35000000000005", "add");   // same handset back
        Row(5, 5, "35000000000006", "remove"); Row(5, 5, "35000000000006", "add");
        Row(5, 5, "35000000000007", "add");                                          // one back, one new: change
        Row(6, 61, "35000000000008", "remove"); Row(6, 62, "35000000000008", "add"); // SIM change, same handset

        for (var i = 0; i < 9_500; i++)
        {
            Row(9, 9, $"3510{i:0000000000}", "remove");
            Row(9, 9, $"3520{i:0000000000}", "add");
        }

        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv.ToString())))
        {
            await store.LoadDailyEventsAsync(day, 1, stream, null, ct);
        }

        await store.RefreshChangeMartsForDayAsync(day, 1, ct);

        Assert.Equal("3", await QueryAsync($"SELECT sum(msisdn_changed) FROM {_database}.agg_device_change_daily WHERE data_date = '2026-08-06'"));
        Assert.Equal("1", await QueryAsync($"SELECT sum(msisdn_changed) FROM {_database}.agg_sim_change_daily WHERE data_date = '2026-08-06'"));
    }

    /// <summary>
    /// A day counts as built only when all four of its day-level marts are, and a day with no
    /// changes at all is built too.
    /// </summary>
    /// <remarks>
    /// On 2026-09-30, 54 real days had a summary and no SIM or handset figures. The first backfill
    /// lost those two marts to memory, and every later run skipped the days as done because the
    /// check read the summary alone. Here the same partial state is made directly.
    /// </remarks>
    [Fact]
    public async Task A_day_is_built_only_when_all_four_of_its_marts_are()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var store = Store(1_000_000);
        var quiet = new DateOnly(2026, 5, 1);
        var busy = new DateOnly(2026, 5, 2);

        // Adds only: no SIM change and no handset change anywhere on the day.
        await ImportAsync(store, quiet, [new(K(1), true), new(K(2), true)]);

        // K(1)'s number moves to another handset.
        await ImportAsync(store, busy, [new(K(1), false), new(new Binding(K(1).Msisdn, K(1).Imsi, "35000000009999"), true)]);

        await store.RefreshChangeMartsForDayAsync(quiet, 1, ct);
        await store.RefreshChangeMartsForDayAsync(busy, 2, ct);

        Assert.Equal([quiet, busy], await store.GetBuiltMartDatesAsync(ct));
        Assert.Equal("1\t0", await QueryAsync($"SELECT count(), sum(msisdn_changed) FROM {_database}.agg_device_change_daily WHERE data_date = '2026-05-01'"));
        Assert.Equal("1", await QueryAsync($"SELECT sum(msisdn_changed) FROM {_database}.agg_device_change_daily WHERE data_date = '2026-05-02'"));

        // What the lost backfill left: the refresh got past the summary and no further.
        await QueryAsync($"ALTER TABLE {_database}.agg_device_change_daily DROP PARTITION '2026-05-02'");

        Assert.Equal([quiet], await store.GetBuiltMartDatesAsync(ct));
    }

    /// <summary>
    /// A day found sharing its sequence with another is given its own when imported again, and
    /// the bindings the collision left wrong are re-derived by date.
    /// </summary>
    /// <remarks>
    /// The state 2026-09-27 left behind: 14 August (late) and 26 September both loaded as delivery
    /// 216 by two workers at once, and 14 August's fold - which had not seen 26 September - landed
    /// last with the same version, so bindings kept a state from before 26 September. Built here
    /// directly, then repaired the way it is repaired for real: by importing the late day again.
    /// </remarks>
    [Fact]
    public async Task Reimporting_a_day_that_shares_its_sequence_gives_it_its_own_and_repairs_the_bindings()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var store = Store(2);
        var late = new DateOnly(2026, 8, 14);
        var newest = new DateOnly(2026, 9, 26);
        var events = new[] { new Event(K(1), true), new Event(K(2), true) };

        // 26 September: both bindings removed. The truth is that both are inactive.
        await ImportAsync(store, D1, [new(K(1), true), new(K(2), true)]);
        await ImportAsync(store, newest, [new(K(1), false), new(K(2), false)]);
        var shared = await QueryAsync($"SELECT any(seq) FROM {_database}.binding_event WHERE data_date = '2026-09-26'");

        // The collision: 14 August loaded under the SAME sequence, and a stale fold of it - one
        // that never saw 26 September - written last, at that same version.
        await QueryAsync($"""
            INSERT INTO {_database}.binding_event (seq, data_date, msisdn, imsi, imei, label)
            VALUES ({shared}, '2026-08-14', {K(1).Msisdn}, {K(1).Imsi}, '{K(1).Imei}', 'add'),
                   ({shared}, '2026-08-14', {K(2).Msisdn}, {K(2).Imsi}, '{K(2).Imei}', 'add')
            """);
        await QueryAsync($"""
            INSERT INTO {_database}.binding_current (msisdn, imsi, imei, active, last_change_seq, last_change_date)
            VALUES ({K(1).Msisdn}, {K(1).Imsi}, '{K(1).Imei}', 1, {shared}, '2026-08-14'),
                   ({K(2).Msisdn}, {K(2).Imsi}, '{K(2).Imei}', 1, {shared}, '2026-08-14')
            """);
        Assert.Equal("2", await QueryAsync($"SELECT sum(active) FROM {_database}.binding_current FINAL"));

        // The repair: 14 August imported again, through the real code.
        await ImportAsync(store, late, events);

        Assert.Equal("0", await QueryAsync($"""
            SELECT count() FROM (SELECT seq FROM (SELECT DISTINCT seq, data_date FROM {_database}.binding_event)
                                 GROUP BY seq HAVING count() > 1)
            """));
        Assert.Equal(shared, await QueryAsync($"SELECT any(seq) FROM {_database}.binding_event WHERE data_date = '2026-09-26'"));
        Assert.Equal("0", await QueryAsync($"SELECT sum(active) FROM {_database}.binding_current FINAL"));
    }
}
