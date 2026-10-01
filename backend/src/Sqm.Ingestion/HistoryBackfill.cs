using System.Globalization;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion;

/// <summary>
/// Fills the binding history (analytics migration 022) from the event log and the initial dump.
/// </summary>
/// <remarks>
/// <para>
/// Run once after the migration; the daily import keeps the history current from then on. A month
/// at a time - the unit the import also rebuilds - each in msisdn ranges, through the same code the
/// import uses, so the backfilled months cannot differ from the ones a daily file builds.
/// </para>
/// <para>
/// <b>Resumable.</b> A month whose every day the ledger records as written, and whose events match
/// the log in all three tables, is skipped; an interrupted run is finished by running it again.
/// <c>--force</c> rebuilds everything.
/// </para>
/// <para>
/// <b>Merges paused while it writes</b>, for the reason <see cref="CurrentStateBackfill"/> gives:
/// on this node a merge of a multi-gigabyte part and a heavy statement reach the memory ceiling
/// together. Resumed to compact, paused again to verify.
/// </para>
/// <para>
/// <b>Run it while nothing imports.</b> An import adding a day to a month this is rebuilding would
/// be dropped with the month, or counted in it twice. The month's ledger would say so and the next
/// import or run would repair it - but the timeline would be wrong in between.
/// </para>
/// </remarks>
internal static class HistoryBackfill
{
    private static readonly string[] Tables = ["binding_history", "binding_history_by_imsi", "binding_history_by_imei"];

    /// <summary>Runs the backfill and returns a process exit code.</summary>
    /// <returns>0 reconciled, 1 a month failed or did not reconcile, 2 could not verify.</returns>
    public static async Task<int> RunAsync(
        IAnalyticsIngestionStore analytics, DateOnly? from, DateOnly? to, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analytics);

        var days = await analytics.GetBusinessDatesAsync(from, to, ct).ConfigureAwait(false);
        var months = days.Select(d => new DateOnly(d.Year, d.Month, 1)).Distinct().Order().ToList();
        var includeDump = from is null;

        Console.WriteLine($"backfilling the binding history: {months.Count} month(s)"
            + (includeDump ? " and the initial dump" : string.Empty));

        var failures = 0;
        var started = DateTimeOffset.UtcNow;

        await MergeControl.PauseAsync(analytics, ct).ConfigureAwait(false);

        try
        {
            if (includeDump)
            {
                if (!force && await DumpReconcilesAsync(analytics, ct).ConfigureAwait(false))
                {
                    Console.WriteLine("  initial dump      already loaded, skipping");
                }
                else
                {
                    failures += await StepAsync("initial dump     ", () => analytics.RebuildHistoryDumpAsync(ct), "bindings")
                        .ConfigureAwait(false);
                }
            }

            foreach (var month in months)
            {
                var label = $"{month:yyyy-MM}           ";

                if (!force && await MonthReconcilesAsync(analytics, month, ct).ConfigureAwait(false))
                {
                    Console.WriteLine($"  {label}already built, skipping");
                    continue;
                }

                failures += await StepAsync(label, () => analytics.RebuildHistoryMonthAsync(month, ct), "events")
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await MergeControl.ResumeAsync(analytics, ct).ConfigureAwait(false);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  written in {(DateTimeOffset.UtcNow - started).TotalMinutes:N1} min, {failures} failure(s)"));

        try
        {
            foreach (var table in Tables)
            {
                await MergeControl.WaitForCompactionAsync(analytics, table, ct).ConfigureAwait(false);
            }

            await MergeControl.PauseAsync(analytics, ct).ConfigureAwait(false);
            var verdict = await ReconcileAsync(analytics, months, includeDump, ct).ConfigureAwait(false);
            return failures > 0 ? 1 : verdict;
        }
        finally
        {
            await MergeControl.ResumeAsync(analytics, ct).ConfigureAwait(false);
        }
    }

    private static async Task<int> StepAsync(string label, Func<Task<long>> step, string unit)
    {
        var started = DateTimeOffset.UtcNow;

        try
        {
            var n = await step().ConfigureAwait(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {label}{n,15:N0} {unit}  {(DateTimeOffset.UtcNow - started).TotalSeconds,6:N0}s"));
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"  {label}FAILED: {ex.Message}");
            Console.Error.WriteLine("  re-run to retry; a month is dropped and rebuilt whole, so a retry is exact");
            return 1;
        }
    }

    /// <summary>Every event of the month is in all three tables, and every day of it is recorded as written.</summary>
    private static async Task<bool> MonthReconcilesAsync(IAnalyticsIngestionStore analytics, DateOnly month, CancellationToken ct)
    {
        var m = (month.Year * 100) + month.Month;
        var log = await analytics.ScalarAsync(
            $"SELECT count() FROM sqm.binding_event WHERE toYYYYMM(data_date) = {m}", ct).ConfigureAwait(false);
        var days = await analytics.ScalarAsync(
            $"SELECT uniqExact(data_date) FROM sqm.binding_event WHERE toYYYYMM(data_date) = {m}", ct).ConfigureAwait(false);
        var written = await analytics.ScalarAsync(
            $"SELECT countIf(state = 'done') FROM sqm.binding_history_day FINAL WHERE toYYYYMM(data_date) = {m}", ct)
            .ConfigureAwait(false);

        if (written < days)
        {
            return false;
        }

        foreach (var table in Tables)
        {
            var held = await analytics.ScalarAsync(
                $"SELECT sum(adds) + sum(removes) FROM sqm.{table} WHERE month = {m}", ct).ConfigureAwait(false);
            if (held != log)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> DumpReconcilesAsync(IAnalyticsIngestionStore analytics, CancellationToken ct)
    {
        var snapshot = await analytics.ScalarAsync("SELECT count() FROM sqm.binding_snapshot", ct).ConfigureAwait(false);

        foreach (var table in Tables)
        {
            var held = await analytics.ScalarAsync($"SELECT count() FROM sqm.{table} WHERE month = 0", ct)
                .ConfigureAwait(false);
            if (held != snapshot)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Proves the history matches the log before claiming success; see CurrentStateBackfill.ReconcileAsync.</summary>
    private static async Task<int> ReconcileAsync(
        IAnalyticsIngestionStore analytics, IReadOnlyList<DateOnly> months, bool includeDump, CancellationToken ct)
    {
        var wrong = new List<string>();

        try
        {
            if (includeDump && !await DumpReconcilesAsync(analytics, ct).ConfigureAwait(false))
            {
                wrong.Add("initial dump");
            }

            foreach (var month in months)
            {
                if (!await MonthReconcilesAsync(analytics, month, ct).ConfigureAwait(false))
                {
                    wrong.Add($"{month:yyyy-MM}");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"written, but it could not be verified: {ex.Message}");
            Console.Error.WriteLine("Re-run once the server is quiet: dotnet run -- --backfill-history (months that reconcile are skipped)");
            return 2;
        }

        Console.WriteLine();

        if (wrong.Count == 0)
        {
            Console.WriteLine("reconciled. Every month's events are in the history, by number, by SIM and by IMEI,");
            Console.WriteLine(includeDump ? "and the initial dump's bindings with them." : "for the months in range.");
            return 0;
        }

        Console.Error.WriteLine($"NOT reconciled: {string.Join(", ", wrong)}. Timelines would be missing events there.");
        Console.Error.WriteLine("Re-run; the months that do reconcile are skipped and these are rebuilt.");
        return 1;
    }
}
