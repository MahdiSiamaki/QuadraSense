using System.Reflection;
using Microsoft.Extensions.Logging;
using Sqm.Application.DataImport;
using Sqm.Application.Sql;

namespace Sqm.Ingestion.Processing;

/// <summary>
/// Rebuilds the dashboard marts for one delivery sequence.
/// </summary>
/// <remarks>
/// <para>
/// The change marts are day-partitioned and rebuilt per day by the importer. These are the other
/// half: the KPI counters, the device rollup, the capability and dimension marts - a snapshot of
/// current state per delivery, partitioned by <c>seq</c>.
/// </para>
/// <para>
/// They matter because an import that is not followed by this leaves the event log holding today
/// and every headline figure showing yesterday, with nothing on screen to say so. That is the
/// most confusing state the system can be in, and it is the reason this runs as part of the
/// import rather than as a job someone remembers to schedule.
/// </para>
/// <para>
/// The SQL is embedded from <c>db/analytics/jobs/refresh_marts.sql</c> rather than duplicated in
/// C#. It stays reviewable as SQL and runnable by hand, and the worker cannot drift from what
/// that file says.
/// </para>
/// </remarks>
internal sealed partial class MartRefresh(
    IAnalyticsIngestionStore analytics,
    ILogger<MartRefresh> logger)
{
    [LoggerMessage(EventId = 3800, Level = LogLevel.Information,
        Message = "Mart refresh for seq {Sequence}: {Completed} of {Total} statements in {Seconds}s")]
    private partial void LogFinished(int sequence, int completed, int total, long seconds);

    [LoggerMessage(EventId = 3801, Level = LogLevel.Warning,
        Message = "Mart refresh statement failed: {Label}")]
    private partial void LogStatementFailed(string label, Exception exception);

    private const string ResourceName = "Sqm.Ingestion.refresh_marts.sql";

    /// <summary>Runs the refresh, reporting each statement as it goes.</summary>
    /// <returns>How many statements failed.</returns>
    public async Task<int> RunAsync(
        int sequence, Func<string, Task>? onProgress, CancellationToken ct)
    {
        var statements = SqlScript.Split(LoadScript());
        var started = DateTime.UtcNow;
        var failures = 0;

        for (var i = 0; i < statements.Count; i++)
        {
            var label = SqlScript.Label(statements[i], i + 1);

            try
            {
                await analytics.ExecuteMartStatementAsync(statements[i], sequence, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One statement failing does not invalidate the ones that succeeded, and the
                // marts are independent of each other. Reporting the failure and continuing
                // leaves the operator with a partly-refreshed set and a precise list of what to
                // re-run, rather than nothing and a stack trace.
                failures++;
                LogStatementFailed(label, ex);

                if (onProgress is not null)
                {
                    await onProgress($"Mart statement failed: {label} - {ex.Message}")
                        .ConfigureAwait(false);
                }
            }
        }

        var seconds = (long)(DateTime.UtcNow - started).TotalSeconds;
        LogFinished(sequence, statements.Count - failures, statements.Count, seconds);

        return failures;
    }

    private static string LoadScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"the embedded mart refresh script '{ResourceName}' is missing from the build");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
