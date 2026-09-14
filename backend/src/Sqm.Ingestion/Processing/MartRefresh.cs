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

    /// <summary>How many times to run the whole script before giving up.</summary>
    /// <remarks>
    /// <para>
    /// The script is idempotent by construction: every mart drops its own partition before
    /// inserting into it, which is what makes re-running the whole thing safe rather than
    /// hopeful. That property was put there to stop a re-run doubling the counts - an
    /// insert-only version once doubled agg_device_daily to 251,879,046 against a real
    /// 125,939,523 - and it turns out to buy this as well.
    /// </para>
    /// <para>
    /// It is needed because statements run back to back and ClickHouse releases memory lazily:
    /// each aggregate runs comfortably under its 1.2 GiB cap in isolation, and a run of them
    /// together can push the server total to its ceiling even though no single one is close.
    /// Retrying the pass lets the stragglers through once the previous ones have let go.
    /// </para>
    /// </remarks>
    private const int MaxPasses = 3;

    private static readonly TimeSpan BetweenStatements = TimeSpan.FromMilliseconds(750);

    private static readonly TimeSpan BetweenPasses = TimeSpan.FromSeconds(20);

    /// <summary>Runs the refresh, retrying the whole script until nothing fails.</summary>
    /// <returns>How many statements still failed after the last pass.</returns>
    public async Task<int> RunAsync(
        int sequence, Func<string, Task>? onProgress, CancellationToken ct)
    {
        var failures = 0;

        for (var pass = 1; pass <= MaxPasses; pass++)
        {
            failures = await RunPassAsync(sequence, pass, onProgress, ct).ConfigureAwait(false);

            if (failures == 0)
            {
                return 0;
            }

            if (pass < MaxPasses)
            {
                if (onProgress is not null)
                {
                    await onProgress(
                        $"{failures} statement(s) failed on pass {pass}; retrying the whole script "
                        + "- it drops each partition before rebuilding it, so a re-run is safe")
                        .ConfigureAwait(false);
                }

                await Task.Delay(BetweenPasses, ct).ConfigureAwait(false);
            }
        }

        return failures;
    }

    private async Task<int> RunPassAsync(
        int sequence, int pass, Func<string, Task>? onProgress, CancellationToken ct)
    {
        var statements = SqlScript.Split(LoadScript());
        var started = DateTime.UtcNow;
        var failures = 0;

        for (var i = 0; i < statements.Count; i++)
        {
            // A breath between statements. ClickHouse frees a query's memory after it reports
            // completion, and starting the next aggregate the same millisecond means competing
            // with the previous one's tail.
            if (i > 0)
            {
                await Task.Delay(BetweenStatements, ct).ConfigureAwait(false);
            }

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
        _ = pass;

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
