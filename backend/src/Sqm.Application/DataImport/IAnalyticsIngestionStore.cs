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

    /// <summary>
    /// Folds one day's events into the current-state table.
    /// </summary>
    /// <returns>Rows written.</returns>
    /// <remarks>
    /// <para>
    /// Only the new day is aggregated, never the whole history. That is correct because
    /// <c>binding_current</c> is a ReplacingMergeTree versioned by the last change sequence: the
    /// new day carries the highest sequence, so its row wins on merge over anything written
    /// before it. Nothing has to be deleted first and nothing has to be replayed in order.
    /// </para>
    /// <para>
    /// The difference is not marginal. Rebuilding the fold from the full 1.05-billion-row event
    /// log took roughly forty-five minutes on the development machine, in sixteen chunks,
    /// because the aggregate does not fit in memory. One day is about eight million rows and
    /// finishes in seconds.
    /// </para>
    /// <para>
    /// This depends on the set semantics proven in discovery: a binding's final state is decided
    /// by its last event alone, not by the path taken to it, verified over 8,062,257 transitions
    /// with zero double-adds. Without that proof an incremental fold would be unsound and the
    /// full replay would be the only correct option.
    /// </para>
    /// </remarks>
    Task<long> FoldDayAsync(DateOnly businessDate, CancellationToken ct);

    /// <summary>
    /// Rebuilds the change marts for one day.
    /// </summary>
    /// <remarks>
    /// Drops the day's partition before inserting, so re-running an import produces the same
    /// marts rather than adding to them. An earlier insert-only version of the mart refresh
    /// doubled every count in <c>agg_device_daily</c> to 251,879,046 against a real 125,939,523,
    /// silently, because a SummingMergeTree sums duplicates without complaint.
    /// </remarks>
    Task RefreshChangeMartsForDayAsync(DateOnly businessDate, int sequence, CancellationToken ct);

    /// <summary>
    /// Confirms the analytics schema is the one this code requires.
    /// </summary>
    /// <remarks>
    /// Specifically that <c>binding_event</c> is partitioned by day. Day-level idempotency is
    /// implemented as a partition drop, which on a monthly-partitioned table would remove a
    /// whole month. Failing at startup with a clear message is far better than discovering it
    /// when a corrected file for one day silently deletes the other thirty.
    /// </remarks>
    Task EnsureSchemaAsync(CancellationToken ct);

    /// <summary>
    /// Runs one statement from the mart refresh script, with <c>{seq}</c> bound.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: it takes a statement from the embedded script and a sequence number,
    /// not arbitrary SQL from a caller. The script is a reviewed file in the repository, which is
    /// what makes running it from code acceptable at all.
    /// </remarks>
    Task ExecuteMartStatementAsync(string sql, int sequence, CancellationToken ct);

    /// <summary>Every business date present in the event log, oldest first.</summary>
    Task<IReadOnlyList<DateOnly>> GetBusinessDatesAsync(
        DateOnly? fromDate, DateOnly? toDate, CancellationToken ct);

    /// <summary>Highest sequence number currently in the event log.</summary>
    Task<int> GetMaxSequenceAsync(CancellationToken ct);

    /// <summary>Sequence already assigned to a business date, or <see langword="null"/>.</summary>
    Task<int?> GetSequenceForDateAsync(DateOnly businessDate, CancellationToken ct);
}
