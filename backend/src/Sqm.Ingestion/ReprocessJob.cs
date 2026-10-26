using Sqm.Application.DataImport;

namespace Sqm.Ingestion;

/// <summary>
/// "Import again" from the shell: queues a finished import's file as a new job - its day's next
/// revision, which replaces the day.
/// </summary>
/// <remarks>
/// <c>--reprocess &lt;job id&gt;</c>. Added 2026-10-26 to repair the first machine: its bulk load had
/// written only part of four days, and a day's file can be imported again only through its job -
/// uploaded anew, the same content is recognised as already imported. The rules are
/// <see cref="ImportIntake.ReprocessAsync"/>'s, shared with the Import Center's "Import again".
/// </remarks>
internal static class ReprocessJob
{
    /// <summary>Who history says asked for it.</summary>
    internal const string Actor = "operator (--reprocess)";

    /// <summary>Queues the job's file again; returns a process exit code.</summary>
    /// <returns>0 queued; 1 its original cannot be read; 2 no such import.</returns>
    public static async Task<int> RunAsync(
        IImportJobRepository repository, IImportFileStore files, long jobId, CancellationToken ct)
    {
        var result = await ImportIntake.ReprocessAsync(repository, files, jobId, Actor, null, ct).ConfigureAwait(false);

        switch (result.Outcome)
        {
            case ReprocessOutcome.NotFound:
                Console.Error.WriteLine($"no import {jobId}");
                return 2;
            case ReprocessOutcome.FileGone:
                Console.Error.WriteLine(
                    $"refusing: import {jobId}'s original cannot be read at {result.StoredPath}, so a new job could only fail");
                return 1;
            default:
                Console.WriteLine($"import {jobId} queued again as import {result.QueuedJobId}");
                return 0;
        }
    }
}
