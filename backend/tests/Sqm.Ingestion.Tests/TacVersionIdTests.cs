using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sqm.Application.DataImport;
using Sqm.Infrastructure.DataImport;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>A TAC version is numbered by its job, so two jobs can never load into one version.</summary>
/// <remarks>
/// The number was max(version_id) + 1, read back from ClickHouse. Two TAC jobs running at once
/// read the same maximum and loaded into the same version; its duplicate check then failed, and
/// each dropped the partition both had written.
/// </remarks>
public sealed class TacVersionIdTests
{
    [Fact]
    public async Task The_version_is_the_job_id_and_a_retry_clears_its_own_leftovers_first()
    {
        var calls = new List<(string Name, int? Version)>();
        var store = Stub.Create<ITacVersionStore>((method, args) =>
        {
            calls.Add((method.Name, args.Length > 0 && args[0] is int v ? v : null));
            return method.Name == "LoadVersionAsync"
                ? throw new OperationCanceledException("stop after the load is addressed")
                : Stub.Default(method);
        });

        var processor = new TacSnapshotProcessor(
            store,
            Stub.Create<IImportJobRepository>((m, _) => m.Name == "ResolveSchemaAsync"
                ? Task.FromResult(new SchemaResolution(SchemaVerdict.Known, 1, "v1", "known"))
                : Stub.Default(m)),
            NullLogger<TacSnapshotProcessor>.Instance);

        // A structurally valid export: the store's own column list, two rows of the same width.
        var columns = ClickHouseTacVersionStore.Columns;
        string Row(string tac) => string.Join(',', columns.Select((_, i) => i == 0 ? tac : "x"));
        var file = $"{string.Join(',', columns)}\n{Row("35004012")}\n{Row("35308690")}\n";
        var context = Stub.Create<IImportContext>((m, _) => m.Name switch
        {
            "OpenFileAsync" => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(file))),
            _ => Stub.Default(m),
        });

        var job = new ClaimedJob(612, "TAC", 1, "stored", "tac_2026-09-16.csv", "sha", file.Length,
            null, Attempt: 2, MaxAttempts: 3, Priority: 0, ReprocessOfJobId: null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processor.ProcessAsync(job, context, CancellationToken.None));

        var load = calls.FindIndex(c => c.Name == "LoadVersionAsync");
        Assert.Equal(612, calls[load].Version);
        Assert.Contains(calls.Take(load), c => c is { Name: "DropVersionAsync", Version: 612 });
    }
}
