using System.Globalization;
using Microsoft.Extensions.Logging;
using Sqm.Application.DataImport;
using Sqm.Domain.Timeline;

namespace Sqm.Infrastructure.DataImport;

/// <summary>
/// The binding history (analytics migration 022): every binding's dated events, one row per
/// binding per month, kept so that an entity's whole timeline is a key read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two paths, as the fold has.</b> A day the history has never seen is added - its aggregates
/// merge with the rest whatever order days arrive in, so this includes a late day. Anything else
/// rebuilds its month from the event log: a day seen before would be counted twice by the same
/// insert, and a month whose last write did not finish is wrong in a way no insert can repair.
/// </para>
/// <para>
/// <b>The ledger is what makes the cheap path safe.</b> A day is written 'pending' before its
/// events go in and 'done' after, so an attempt that died part way is never mistaken for one that
/// never ran.
/// </para>
/// </remarks>
public sealed partial class ClickHouseIngestionStore
{
    [LoggerMessage(EventId = 3210, Level = LogLevel.Information,
        Message = "Binding history: added {BusinessDate}, {Events} events")]
    private partial void LogHistoryAdded(DateOnly businessDate, long events);

    [LoggerMessage(EventId = 3211, Level = LogLevel.Information,
        Message = "Binding history: rebuilt month {Month} ({Reason}): {Events} events in {Ranges} range(s), {ElapsedMs} ms")]
    private partial void LogHistoryRebuilt(int month, string reason, long events, int ranges, long elapsedMs);

    [LoggerMessage(EventId = 3212, Level = LogLevel.Information,
        Message = "Binding history: loaded the initial dump, {Bindings} bindings, {ElapsedMs} ms")]
    private partial void LogHistoryDump(long bindings, long elapsedMs);

    /// <summary>The history and its two re-ordered copies. A DROP PARTITION reaches only the table named.</summary>
    private static readonly string[] HistoryTables = ["binding_history", "binding_history_by_imsi", "binding_history_by_imei"];

    /// <summary>One definition of what a history row holds, for the day and the month alike.</summary>
    private string HistoryInsert(string where) => $$"""
        INSERT INTO {{_database}}.binding_history
            (month, msisdn, imsi, imei, in_dump, first_date, last_date, adds, removes, events)
        SELECT
            toYYYYMM(data_date), msisdn, imsi, imei, 0,
            min(data_date), max(data_date),
            countIf(label = 'add'), countIf(label = 'remove'),
            groupArray((data_date, seq, toUInt8(label)))
        FROM {{_database}}.binding_event
        WHERE {{where}}
        GROUP BY toYYYYMM(data_date), msisdn, imsi, imei
        """;

    /// <inheritdoc />
    public async Task<HistoryRefresh> RefreshHistoryForDayAsync(DateOnly businessDate, CancellationToken ct)
    {
        if (!await HistoryDeployedAsync(ct).ConfigureAwait(false))
        {
            return HistoryRefresh.NotDeployed;
        }

        var date = Iso(businessDate);
        var month = Month(businessDate);

        var seen = await ScalarAsync(
            $"SELECT count() FROM {_database}.binding_history_day FINAL WHERE data_date = toDate('{date}')", ct)
            .ConfigureAwait(false) > 0;
        var unfinished = await ScalarAsync(
            $"SELECT count() FROM {_database}.binding_history_day FINAL WHERE toYYYYMM(data_date) = {month} AND state = 'pending'", ct)
            .ConfigureAwait(false) > 0;

        if (seen || unfinished)
        {
            var reason = seen
                ? $"{date} was already in the history - a retry, or a corrected file replacing the day"
                : $"an earlier write to {month} did not finish";

            var rebuilt = await RebuildMonthAsync(businessDate, reason, ct).ConfigureAwait(false);
            return new HistoryRefresh(HistoryRefreshKind.RebuiltMonth, rebuilt, reason);
        }

        var dayWhere = $"data_date = toDate('{date}')";
        await MarkHistoryDaysAsync(dayWhere, "pending", ct).ConfigureAwait(false);
        await ExecuteBoundedAsync(HistoryInsert("data_date = {businessDate:Date}"), businessDate, ct).ConfigureAwait(false);
        await MarkHistoryDaysAsync(dayWhere, "done", ct).ConfigureAwait(false);

        var events = await CountEventsForDateAsync(businessDate, ct).ConfigureAwait(false);
        LogHistoryAdded(businessDate, events);
        return new HistoryRefresh(HistoryRefreshKind.Added, events, null);
    }

    /// <inheritdoc />
    public async Task<long> RebuildHistoryMonthAsync(DateOnly anyDayOfMonth, CancellationToken ct)
    {
        await RequireHistoryAsync(ct).ConfigureAwait(false);
        return await RebuildMonthAsync(anyDayOfMonth, "backfill", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> RebuildHistoryDumpAsync(CancellationToken ct)
    {
        await RequireHistoryAsync(ct).ConfigureAwait(false);
        var started = System.Diagnostics.Stopwatch.StartNew();

        foreach (var table in HistoryTables)
        {
            await ExecuteHttpAsync($"ALTER TABLE {_database}.{table} DROP PARTITION 0", Empty, ct).ConfigureAwait(false);
        }

        // first_date is the window's first day - the earliest the binding can have been seen, and
        // what the product owner chose to sort by - and last_date the zero date, so that max() over
        // a binding's rows is its last event and nothing else.
        await ExecuteHttpAsync($$"""
            INSERT INTO {{_database}}.binding_history
                (month, msisdn, imsi, imei, in_dump, first_date, last_date, adds, removes, events)
            SELECT 0, msisdn, imsi, imei, 1, toDate('{{Iso(InitialDump.WindowStart)}}'), toDate(0), 0, 0,
                   CAST([] AS Array(Tuple(Date, UInt16, UInt8)))
            FROM {{_database}}.binding_snapshot
            """, Empty, ct).ConfigureAwait(false);

        var bindings = await ScalarAsync($"SELECT count() FROM {_database}.binding_snapshot", ct).ConfigureAwait(false) ?? 0;
        LogHistoryDump(bindings, started.ElapsedMilliseconds);
        return bindings;
    }

    private async Task<long> RebuildMonthAsync(DateOnly anyDayOfMonth, string reason, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var first = new DateOnly(anyDayOfMonth.Year, anyDayOfMonth.Month, 1);
        var month = Month(first);
        var monthWhere = $"data_date >= toDate('{Iso(first)}') AND data_date <= toDate('{Iso(first.AddMonths(1).AddDays(-1))}')";

        // Pending first: a rebuild that dies part way leaves the month marked unfinished, and the
        // next day to arrive in it rebuilds it again rather than adding to the remains.
        await MarkHistoryDaysAsync(monthWhere, "pending", ct).ConfigureAwait(false);

        foreach (var table in HistoryTables)
        {
            await ExecuteHttpAsync($"ALTER TABLE {_database}.{table} DROP PARTITION {month}", Empty, ct).ConfigureAwait(false);
        }

        var events = await ScalarAsync($"SELECT count() FROM {_database}.binding_event WHERE {monthWhere}", ct)
            .ConfigureAwait(false) ?? 0;
        var ranges = await EventRangesAsync(monthWhere, events, ct).ConfigureAwait(false);

        foreach (var (lo, hi) in ranges)
        {
            await ExecuteHttpAsync(
                HistoryInsert(monthWhere + " AND msisdn BETWEEN {lo:UInt64} AND {hi:UInt64}"),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["param_lo"] = lo.ToString(CultureInfo.InvariantCulture),
                    ["param_hi"] = hi.ToString(CultureInfo.InvariantCulture),
                },
                ct).ConfigureAwait(false);
        }

        await MarkHistoryDaysAsync(monthWhere, "done", ct).ConfigureAwait(false);
        LogHistoryRebuilt(month, reason, events, ranges.Count, started.ElapsedMilliseconds);
        return events;
    }

    /// <summary>
    /// Inclusive msisdn ranges of about <see cref="ClickHouse.ClickHouseOptions.HistoryEventsPerRange"/>
    /// events each, covering every msisdn. Approximate quantiles: a month is hundreds of millions of
    /// rows, too many to sort exactly under the memory cap, and a cut need only cover, not be even.
    /// </summary>
    private async Task<IReadOnlyList<(ulong Lo, ulong Hi)>> EventRangesAsync(
        string where, long events, CancellationToken ct)
    {
        var perRange = Math.Max(1, _options.HistoryEventsPerRange);
        var count = (int)Math.Max(1, (events + perRange - 1) / perRange);

        if (count == 1)
        {
            return [(0UL, ulong.MaxValue)];
        }

        var levels = string.Join(", ", Enumerable.Range(1, count - 1)
            .Select(i => ((double)i / count).ToString("R", CultureInfo.InvariantCulture)));

        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT arrayJoin(arraySort(arrayDistinct(arrayMap(x -> toUInt64(x), quantiles({levels})(msisdn))))) FROM {_database}.binding_event WHERE {where}";
        command.CommandTimeout = _options.QueryTimeoutSeconds;

        var cuts = new List<ulong>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                cuts.Add(Convert.ToUInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
            }
        }

        var ranges = new List<(ulong Lo, ulong Hi)>();
        var lo = 0UL;
        foreach (var cut in cuts.Where(c => c > 0))
        {
            ranges.Add((lo, cut - 1));
            lo = cut;
        }

        ranges.Add((lo, ulong.MaxValue));
        return ranges;
    }

    /// <summary>Records the state of every day the event log holds within <paramref name="dayWhere"/>.</summary>
    private Task MarkHistoryDaysAsync(string dayWhere, string state, CancellationToken ct) =>
        ExecuteHttpAsync($"""
            INSERT INTO {_database}.binding_history_day (data_date, state, events, updated_at)
            SELECT data_date, '{state}', count(), now64(3)
            FROM {_database}.binding_event
            WHERE {dayWhere}
            GROUP BY data_date
            """, Empty, ct);

    private async Task<bool> HistoryDeployedAsync(CancellationToken ct) =>
        await ScalarAsync($"EXISTS TABLE {_database}.binding_history_day", ct).ConfigureAwait(false) == 1;

    private async Task RequireHistoryAsync(CancellationToken ct)
    {
        if (!await HistoryDeployedAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"{_database}.binding_history does not exist. Apply analytics migration 022_binding_history.sql first.");
        }
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(StringComparer.Ordinal);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static int Month(DateOnly date) => (date.Year * 100) + date.Month;
}
