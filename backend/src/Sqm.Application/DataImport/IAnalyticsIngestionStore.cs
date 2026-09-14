namespace Sqm.Application.DataImport;

/// <summary>
/// Write access to the analytics store, for the ingestion worker only.
/// </summary>
/// <remarks>
/// Kept separate from <c>IDeviceAnalyticsStore</c>, which is read-only and serves the API. The
/// split is not ceremony: it means the process that answers dashboard requests has no method
/// available to it that can write to or drop anything, so a bug in an endpoint cannot delete a
/// day's data however badly it behaves.
/// </remarks>
public interface IAnalyticsIngestionStore
{
    /// <summary>How many events are already recorded for a business date.</summary>
    /// <remarks>
    /// Asked before every load. Removing a day is cheap but not free, and issuing the removal
    /// unconditionally during a bulk load made the whole load quadratic once already.
    /// </remarks>
    Task<long> CountEventsForDateAsync(DateOnly businessDate, CancellationToken ct);

    /// <summary>
    /// Removes every event for one business date, so the day can be loaded again cleanly.
    /// </summary>
    /// <remarks>
    /// This is what makes an import idempotent: a corrected file for a day that already loaded
    /// replaces it rather than doubling it. The table is partitioned by day precisely so this can
    /// be a partition drop - a metadata operation - rather than a row-level delete, which in
    /// ClickHouse is a mutation that rewrites every part it touches.
    /// </remarks>
    Task RemoveDayAsync(DateOnly businessDate, CancellationToken ct);

    /// <summary>
    /// Streams a day's CSV into the event log.
    /// </summary>
    /// <param name="businessDate">The day the file describes.</param>
    /// <param name="sequence">Delivery sequence number for the day.</param>
    /// <param name="csv">The file, positioned at the start, including its header row.</param>
    /// <param name="onBytesRead">Called as bytes leave the stream, for progress reporting.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Rows written, as reported by the server.</returns>
    Task<long> LoadDailyEventsAsync(
        DateOnly businessDate,
        int sequence,
        Stream csv,
        Action<long>? onBytesRead,
        CancellationToken ct);

    /// <summary>Highest sequence number currently in the event log.</summary>
    Task<int> GetMaxSequenceAsync(CancellationToken ct);

    /// <summary>Sequence already assigned to a business date, or <see langword="null"/>.</summary>
    Task<int?> GetSequenceForDateAsync(DateOnly businessDate, CancellationToken ct);
}
