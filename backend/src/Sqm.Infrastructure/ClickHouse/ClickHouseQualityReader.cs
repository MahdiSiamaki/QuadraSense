using System.Globalization;
using Microsoft.Extensions.Options;
using Sqm.Application.Quality;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>Reads the data-quality categories of the newest published measures run (analytics migration 026).</summary>
/// <remarks>
/// Sums a run's chunks: bindings and numbers add up exactly (a chunk is a range of numbers), SIMs and
/// IMEIs are uniq() states merged here. A run planned before migration 026 has no quality rows and is
/// skipped, so the page waits for the first run that has them rather than showing zeros.
/// </remarks>
public sealed class ClickHouseQualityReader : IQualityReader
{
    private static readonly QueryBudget Budget = new(MaxRowsToRead: 10_000_000, MaxExecutionSeconds: 30);

    private readonly ClickHouseJsonQuery _query;

    /// <summary>
    /// The last run's categories. A run's counts never change once it is published, and merging its
    /// SIM and IMEI estimates across 96 chunks takes 2.4 s (measured 2026-10-07), so they are read
    /// once per run: each request still asks which run is newest (23 ms), and a new run replaces this.
    /// </summary>
    private QualitySnapshot? _cached;

    /// <summary>Creates the reader over the analytics connection.</summary>
    public ClickHouseQualityReader(IOptions<ClickHouseOptions> options, IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _query = new ClickHouseJsonQuery(options.Value.ConnectionString, httpClientFactory);
    }

    /// <inheritdoc />
    public async Task<QualitySnapshot?> GetLatestAsync(CancellationToken ct)
    {
        var none = new Dictionary<string, string>(StringComparer.Ordinal);
        var deployed = await _query.ExecuteAsync(
            "SELECT count() FROM system.tables WHERE database = currentDatabase() AND name IN ('risk_run', 'quality_chunk')", none, ct)
            .ConfigureAwait(false);
        if (ClickHouseJsonResult.Int64(deployed.Rows[0], 0) < 2)
        {
            return null;
        }

        var run = await _query.ExecuteAsync("""
            SELECT run_id, as_of, toString(updated_at) FROM risk_run FINAL
            WHERE state = 'published' AND run_id IN (SELECT DISTINCT run_id FROM quality_chunk)
            ORDER BY run_id DESC LIMIT 1
            """, none, ct, Budget).ConfigureAwait(false);
        if (run.Rows.Count == 0)
        {
            return null;
        }

        var runId = ClickHouseJsonResult.UInt64(run.Rows[0], 0);
        if (Volatile.Read(ref _cached) is { } cached && cached.RunId == runId)
        {
            return cached;
        }

        var result = await _query.ExecuteAsync("""
            SELECT category, sum(bindings), sum(numbers), uniqMerge(sims), uniqMerge(imeis), sum(periods)
            FROM quality_chunk WHERE run_id = {run:UInt64}
            GROUP BY category ORDER BY category
            """, new Dictionary<string, string>(StringComparer.Ordinal) { ["run"] = runId.ToString(CultureInfo.InvariantCulture) },
            ct, Budget).ConfigureAwait(false);

        var snapshot = new QualitySnapshot(
            runId,
            ClickHouseJsonResult.Date(run.Rows[0], 1)!.Value,
            DateTimeOffset.Parse(ClickHouseJsonResult.Text(run.Rows[0], 2)!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            [.. result.Rows.Select(r => new QualityCategoryCount(
                ClickHouseJsonResult.Text(r, 0)!, ClickHouseJsonResult.Int64(r, 1), ClickHouseJsonResult.Int64(r, 2),
                ClickHouseJsonResult.Int64(r, 3), ClickHouseJsonResult.Int64(r, 4), ClickHouseJsonResult.Int64(r, 5)))]);

        Volatile.Write(ref _cached, snapshot);
        return snapshot;
    }
}
