using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using Sqm.Application.Planning;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion;

/// <summary>Builds the planning snapshot to the end from the command line, with the worker's own steps.</summary>
internal static class PlanningSnapshotRefresh
{
    /// <summary>Runs steps until the run is published or cannot go on, and returns a process exit code.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        var settings = services.GetRequiredService<IOptions<PlanningOptions>>().Value;
        var problems = settings.Problems();
        if (problems.Count > 0)
        {
            Console.Error.WriteLine("the Planning settings are invalid:");
            foreach (var problem in problems)
            {
                Console.Error.WriteLine("  " + problem);
            }

            return 2;
        }

        var snapshot = services.GetRequiredService<PlanningSnapshot>();
        var total = Stopwatch.StartNew();
        var first = true;
        var failures = 0;

        while (true)
        {
            var watch = Stopwatch.StartNew();
            PlanningSnapshotStep step;
            try
            {
                step = await snapshot.StepAsync(settings, force && first, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"  step failed after {watch.Elapsed.TotalSeconds:F1}s: {ex.Message}");
                if (++failures >= PlanningSnapshot.MaxAttempts * 2)
                {
                    return 1;
                }

                continue;
            }

            first = false;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {step,-10} {watch.Elapsed.TotalSeconds,7:F1}s"));

            switch (step)
            {
                case PlanningSnapshotStep.Planned or PlanningSnapshotStep.Built:
                    continue;
                case PlanningSnapshotStep.Published or PlanningSnapshotStep.Current:
                    Console.WriteLine($"planning snapshot {step.ToString().ToLowerInvariant()} in {total.Elapsed.TotalMinutes:F1} minutes");
                    return 0;
                case PlanningSnapshotStep.NotDeployed:
                    Console.Error.WriteLine("the planning snapshot tables do not exist; apply analytics migrations 029 and 030");
                    return 1;
                case PlanningSnapshotStep.Waiting:
                    Console.Error.WriteLine("not built: an SQM import is running, or the binding history or radio profiles are incomplete");
                    return 1;
                default:
                    Console.Error.WriteLine($"not built: the run was {step.ToString().ToLowerInvariant()}; see the planning_run ledger for why");
                    return 1;
            }
        }
    }
}
