using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Application.Risk;
using Sqm.Domain.Risk;
using Sqm.Infrastructure.ClickHouse;
using Sqm.Infrastructure.DataImport;

namespace Sqm.Integration.Tests;

/// <summary>
/// The risk reader on the real published measures: every list, at its storage floor - the largest it
/// can be - and on its deepest reachable page, the per-rule counts, and the heaviest SIM and IMEI with
/// everything bound to them, each within the budget the server enforces.
/// </summary>
/// <remarks>
/// Skips without the stack or without a published run. The timings are written to the test output;
/// what is asserted is that each read completes under the server-side budget (30 s, 50M rows - a read
/// over it is stopped, not served), that lists come back in order, and that the worker and the reader
/// agree on whether the run is current.
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class RiskRealDataTests
{
    private static string Connection(string user) => $"Host=localhost;Port=18123;Database=sqm;Username={user};Password=sqm_dev";

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

    private static ClickHouseRiskReader Reader() => new(
        Options.Create(new ClickHouseOptions { ConnectionString = Connection("sqm_app") }),
        new Factory(), NullLogger<ClickHouseRiskReader>.Instance);

    private static ClickHouseIngestionStore Store() => new(
        Options.Create(new ClickHouseOptions { ConnectionString = Connection("sqm_ingest") }),
        new Factory(), NullLogger<ClickHouseIngestionStore>.Instance);

    private static void Write(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static async Task<RiskReadState?> StateAsync(RiskOptions options)
    {
        try
        {
            var state = await Reader().GetStateAsync(options.Floors, TestContext.Current.CancellationToken);
            return state.Run is null ? null : state;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    [Fact]
    public async Task Every_list_and_entity_reads_within_its_budget_and_the_worker_agrees_it_is_current()
    {
        var options = new RiskOptions();
        if (await StateAsync(options) is not { Run: { } run } state)
        {
            Assert.Skip("no ClickHouse with a published risk run (analytics migration 024, Sqm.Ingestion --refresh-risk)");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var reader = Reader();

        var inputs = await Store().ReadInputsAsync(options, ct);
        Assert.Equal(inputs.Fingerprint == run.Fingerprint, state.Stale is null);
        Write($"run {run.RunId} as of {run.AsOf:yyyy-MM-dd}, stale: {state.Stale is not null}");

        // At the floor every stored row is listed: the largest each list can be.
        var thresholds = RiskRules.Catalogue.ToDictionary(
            s => s.Rule, s => new RiskThreshold(RiskEvaluation.Floor(s.Rule, options.Floors) - 1, s.Rule == RiskRule.Randomisation20 ? 0 : null));

        var watch = Stopwatch.StartNew();
        var counts = (await reader.CountAsync(run, thresholds, 1.0, ct)).ToDictionary(c => c.Rule);
        Write($"counts, every rule: {watch.ElapsedMilliseconds} ms");

        foreach (var spec in RiskRules.Catalogue)
        {
            foreach (var view in RiskEvaluation.HasDataQualityView(spec.Rule) ? new[] { RiskView.Risk, RiskView.DataQuality } : [RiskView.Risk])
            {
                var first = await reader.ListAsync(run, new RiskListQuery(spec.Rule, view, thresholds[spec.Rule], 1.0, 1, 500), ct);
                var deep = await reader.ListAsync(run, new RiskListQuery(spec.Rule, view, thresholds[spec.Rule], 1.0, 20, 500), ct);
                Write($"{spec.Rule,-26} {view,-11} total {first.Total,10:N0}  page 1: {first.ElapsedMs,5} ms, {first.RowsRead,11:N0} rows read;  page 20: {deep.ElapsedMs,5} ms");

                Assert.Equal(view == RiskView.Risk ? counts[spec.Rule].Risk : counts[spec.Rule].DataQuality, first.Total);

                if (view == RiskView.Risk)
                {
                    var values = first.Rows.Select(r => RiskEvaluation.Evidence(r, run.AsOf, state.DaysWithData).Measures.Single(m => m.Rule == spec.Rule).Value).ToList();
                    Assert.Equal(values.OrderDescending(), values);
                }
            }
        }

        // The heaviest SIM and IMEI, and everything bound to them.
        foreach (var (rule, family) in new[] { (RiskRule.HighDeviceCount30, RiskFamily.Sim), (RiskRule.SharedImeiSimsEver, RiskFamily.Imei) })
        {
            var top = (await reader.ListAsync(run, new RiskListQuery(rule, RiskView.Risk, thresholds[rule], 1.0, 1, 1), ct)).Rows.Single();

            watch.Restart();
            Assert.NotNull(await reader.GetEntityAsync(run, family, top.Key, ct));
            var entityMs = watch.ElapsedMilliseconds;

            watch.Restart();
            var linked = await reader.GetLinkedAsync(run, family, top.Key, ct);
            Write($"heaviest {family}: entity {entityMs} ms; {linked.Count:N0} bound entities with measures in {watch.ElapsedMilliseconds} ms");
        }
    }
}
