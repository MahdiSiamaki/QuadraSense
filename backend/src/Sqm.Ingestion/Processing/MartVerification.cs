using System.Globalization;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion.Processing;

/// <summary>
/// Checks that a delivery's marts are complete, not merely present.
/// </summary>
/// <remarks>
/// <para>
/// This exists because "the partition exists" is a much weaker statement than it sounds, and
/// believing otherwise produced a wrong report in this project. The refresh writes 15 INSERTs
/// across 6 marts - three measures into the device-class mart, three into the capability mart,
/// six dimensions into the dimension mart - so a run where a third of them failed still leaves
/// every mart holding rows for the delivery.
/// </para>
/// <para>
/// What was actually true after such a run: <c>agg_device_class_daily</c> had only the
/// subscribers measure, <c>agg_capability_daily</c> had only bindings, and the dimension mart
/// was missing <c>vendor</c> - the one the Top Vendors widget reads. Every partition existed.
/// </para>
/// <para>
/// So this checks two things a partition count cannot: that every slice the refresh is supposed
/// to write is there, and that the row counts agree with the KPI mart's active-binding total.
/// A mart that disagrees with the headline figure is worse than a missing one, because it will
/// be believed.
/// </para>
/// </remarks>
internal static class MartVerification
{
    /// <summary>The measures the refresh writes into the per-measure marts.</summary>
    private static readonly string[] Measures = ["bindings", "subscribers", "handsets"];

    /// <summary>The dimensions the refresh writes into the dimension mart.</summary>
    private static readonly string[] Dimensions =
        ["deviceType", "manufacturer", "model", "os", "tac", "vendor"];

    /// <summary>Runs the checks and prints what is wrong.</summary>
    /// <returns>How many problems were found.</returns>
    public static async Task<int> RunAsync(
        IAnalyticsIngestionStore analytics, int sequence, CancellationToken ct)
    {
        Console.WriteLine($"verifying dashboard marts for delivery {sequence}");

        var problems = new List<string>();

        var active = await ScalarAsync(analytics,
            $"SELECT active_bindings FROM sqm.agg_kpi_daily WHERE seq = {sequence}", ct)
            .ConfigureAwait(false);

        if (active is null or 0)
        {
            Console.WriteLine("  agg_kpi_daily has no row for this delivery - nothing else can be");
            Console.WriteLine("  checked against it");
            return 1;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  agg_kpi_daily        active_bindings = {active:N0}"));

        // The device rollup is what every vendor, model, OS and type widget reads. If its total
        // disagrees with the KPI card, two numbers on the same screen contradict each other.
        await CheckTotalAsync(analytics, problems, "agg_device_daily",
            $"SELECT sum(n) FROM sqm.agg_device_daily WHERE seq = {sequence} AND active = 1",
            active.Value, ct).ConfigureAwait(false);

        foreach (var measure in Measures)
        {
            await CheckPresentAsync(analytics, problems,
                $"agg_device_class_daily [{measure}]",
                $"SELECT count() FROM sqm.agg_device_class_daily "
                + $"WHERE seq = {sequence} AND measure = '{measure}'", ct).ConfigureAwait(false);

            await CheckPresentAsync(analytics, problems,
                $"agg_capability_daily [{measure}]",
                $"SELECT count() FROM sqm.agg_capability_daily "
                + $"WHERE seq = {sequence} AND measure = '{measure}'", ct).ConfigureAwait(false);
        }

        foreach (var dimension in Dimensions)
        {
            await CheckPresentAsync(analytics, problems,
                $"agg_dimension_daily [{dimension}]",
                $"SELECT count() FROM sqm.agg_dimension_daily "
                + $"WHERE seq = {sequence} AND dimension = '{dimension}'", ct).ConfigureAwait(false);
        }

        // deviceType is the one dimension where every active binding must land in exactly one
        // bucket, so its total is a real reconciliation rather than a sanity check.
        await CheckTotalAsync(analytics, problems, "agg_dimension_daily [deviceType]",
            $"SELECT sum(bindings) FROM sqm.agg_dimension_daily "
            + $"WHERE seq = {sequence} AND dimension = 'deviceType'",
            active.Value, ct).ConfigureAwait(false);

        Console.WriteLine();

        if (problems.Count == 0)
        {
            Console.WriteLine("all marts complete and reconciling");
            return 0;
        }

        Console.WriteLine($"{problems.Count} problem(s):");
        foreach (var problem in problems)
        {
            Console.WriteLine("  " + problem);
        }

        Console.WriteLine();
        Console.WriteLine("re-run: --refresh-dashboard --pause-merges");
        return problems.Count;
    }

    private static async Task CheckPresentAsync(
        IAnalyticsIngestionStore analytics, List<string> problems,
        string label, string sql, CancellationToken ct)
    {
        var rows = await ScalarAsync(analytics, sql, ct).ConfigureAwait(false) ?? 0;

        if (rows == 0)
        {
            problems.Add($"{label} is MISSING");
        }
        else
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {label,-36} {rows,12:N0} rows"));
        }
    }

    private static async Task CheckTotalAsync(
        IAnalyticsIngestionStore analytics, List<string> problems,
        string label, string sql, long expected, CancellationToken ct)
    {
        var total = await ScalarAsync(analytics, sql, ct).ConfigureAwait(false) ?? 0;

        if (total != expected)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"{label} totals {total:N0}, KPI says {expected:N0} "
                + $"(difference {total - expected:N0})"));
        }
        else
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {label,-36} {total,12:N0} = KPI"));
        }
    }

    private static async Task<long?> ScalarAsync(
        IAnalyticsIngestionStore analytics, string sql, CancellationToken ct)
    {
        try
        {
            return await analytics.ScalarAsync(sql, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  query failed: {ex.Message}");
            return null;
        }
    }
}
