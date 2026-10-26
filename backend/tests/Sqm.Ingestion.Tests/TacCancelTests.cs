using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sqm.Application.DataImport;
using Sqm.Infrastructure.DataImport;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>A TAC file can be cancelled until its version is registered, and leaves nothing behind.</summary>
/// <remarks>
/// A TAC job never read the cancel flag at all. Until FINALIZING registers it, a version is a
/// partition nothing reads, so an operator's cancel anywhere before that drops it.
/// </remarks>
public sealed class TacCancelTests
{
    private sealed class Run
    {
        public List<(string Name, int? Version)> Calls { get; } = [];

        public bool LoadIsStoppedByOperator { get; init; }

        public bool GateOpen { get; init; } = true;

        public async Task<ImportOutcome> ProcessAsync()
        {
            using var stop = new CancellationTokenSource();
            var stoppedByOperator = false;

            var store = Stub.Create<ITacVersionStore>((method, args) =>
            {
                Calls.Add((method.Name, args.Length > 0 && args[0] is int v ? v : null));
                switch (method.Name)
                {
                    case "LoadVersionAsync" when LoadIsStoppedByOperator:
                        stoppedByOperator = true;
                        stop.Cancel();
                        throw new OperationCanceledException(stop.Token);
                    case "LoadVersionAsync":
                        return Task.FromResult(2L);
                    case "CheckVersionAsync":
                        return Task.FromResult(new TacVersionCheck(2, 2, 0, 0, "2026-09-01"));
                    default:
                        return Stub.Default(method);
                }
            });

            var repository = Stub.Create<IImportJobRepository>((m, args) =>
            {
                Calls.Add((m.Name, null));
                return m.Name == "ResolveSchemaAsync"
                    ? Task.FromResult(new SchemaResolution(SchemaVerdict.Known, 1, "v1", "known"))
                    : Stub.Default(m);
            });

            var processor = new TacSnapshotProcessor(store, repository, NullLogger<TacSnapshotProcessor>.Instance);

            var columns = ClickHouseTacVersionStore.Columns;
            string Row(string tac) => string.Join(',', columns.Select((_, i) => i == 0 ? tac : "x"));
            var file = $"{string.Join(',', columns)}\n{Row("35004012")}\n{Row("35308690")}\n";

            var context = Stub.Create<IImportContext>((m, _) => m.Name switch
            {
                "OpenFileAsync" => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(file))),
                "EnterStageUnlessCancelledAsync" => Task.FromResult(GateOpen),
                "get_CancelledByOperator" => stoppedByOperator,
                _ => Stub.Default(m),
            });

            var job = new ClaimedJob(612, "TAC", 1, "stored", "tac_2026-09-16.csv", "sha", file.Length,
                null, Attempt: 1, MaxAttempts: 3, Priority: 0, ReprocessOfJobId: null);

            return await processor.ProcessAsync(job, context, stop.Token);
        }
    }

    [Fact]
    public async Task An_operators_cancel_during_the_load_drops_the_version_and_ends_cancelled()
    {
        var run = new Run { LoadIsStoppedByOperator = true };

        var outcome = await run.ProcessAsync();

        Assert.Equal(ImportJobStatus.Cancelled, outcome.Status);
        Assert.Equal(("DropVersionAsync", 612), run.Calls[^1]);
        Assert.DoesNotContain(run.Calls, c => c.Name == "CreateTacVersionAsync");
    }

    [Fact]
    public async Task A_cancel_that_came_before_registration_drops_the_version()
    {
        var run = new Run { GateOpen = false };

        var outcome = await run.ProcessAsync();

        Assert.Equal(ImportJobStatus.Cancelled, outcome.Status);
        Assert.Equal(("DropVersionAsync", 612), run.Calls[^1]);
        Assert.DoesNotContain(run.Calls, c => c.Name == "CreateTacVersionAsync");
    }
}
