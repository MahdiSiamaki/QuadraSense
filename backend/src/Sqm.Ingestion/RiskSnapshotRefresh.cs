using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using Sqm.Application.Risk;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion;

/// <summary>Builds the risk snapshot to the end from the command line, with the worker's own steps.</summary>
internal static class RiskSnapshotRefresh
{
    /// <summary>Runs steps until the run is published or cannot go on, and returns a process exit code.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        var settings = services.GetRequiredService<IOptions<RiskOptions>>().Value;
        var problems = settings.Problems();
        if (problems.Count > 0)
        {
            Console.Error.WriteLine("the Risk settings are invalid:");
            foreach (var problem in problems)
            {
                Console.Error.WriteLine("  " + problem);
            }

            return 2;
        }

        var snapshot = services.GetRequiredService<RiskSnapshot>();
        var total = Stopwatch.StartNew();
        var first = true;

        while (true)
        {
            var watch = Stopwatch.StartNew();
            RiskSnapshotStep step;
            try
            {
                // --force applies to the first step only: it plans a run whatever the ledger says, and
                // every later step carries on with that run.
                step = await snapshot.StepAsync(settings, force && first, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"  step failed after {watch.Elapsed.TotalSeconds:F1}s: {ex.Message}");
                continue;
            }

            first = false;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {step,-10} {watch.Elapsed.TotalSeconds,7:F1}s"));

            switch (step)
            {
                case RiskSnapshotStep.Planned or RiskSnapshotStep.Built:
                    continue;
                case RiskSnapshotStep.Published or RiskSnapshotStep.Current:
                    Console.WriteLine($"risk snapshot {step.ToString().ToLowerInvariant()} in {total.Elapsed.TotalMinutes:F1} minutes");
                    return 0;
                case RiskSnapshotStep.NotDeployed:
                    Console.Error.WriteLine("the risk snapshot tables do not exist; apply analytics migration 024");
                    return 1;
                case RiskSnapshotStep.Waiting:
                    Console.Error.WriteLine("not built: an SQM import is running, or the binding history is incomplete");
                    return 1;
                default:
                    Console.Error.WriteLine($"not built: the run was {step.ToString().ToLowerInvariant()}; see the risk_run ledger for why");
                    return 1;
            }
        }
    }
}
