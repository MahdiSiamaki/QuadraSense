using System.Globalization;
using Microsoft.Extensions.Logging;
using Sqm.Application.Abstractions;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>
/// IMSI search: the queries, and why they are shaped the way they are.
/// </summary>
/// <remarks>
/// <para>
/// Reads <c>sqm.binding_by_imsi</c> and <c>sqm.binding_event</c>. Neither touches
/// <c>sqm.binding_current</c>, whose key is <c>(msisdn, imsi, imei)</c> and whose second column
/// prunes nothing: four of five measured IMSIs read all 36,013 granules - the whole 295-million
/// row table - at about 1.3 s each. ADR-008 has the measurements and the options rejected.
/// </para>
/// <para>
/// These queries go over HTTP rather than through the ADO driver, for the server's own
/// <c>rows_read</c> accounting and for per-query limits that actually arrive. See
/// <see cref="ClickHouseJsonQuery"/>.
/// </para>
/// <para>
/// Every value is bound as a server-side query parameter. The only text assembled at runtime is a
/// WHERE fragment whose wording is fixed at compile time and whose presence is decided by whether
/// a filter was supplied.
/// </para>
/// </remarks>
public sealed partial class ClickHouseAnalyticsStore
{
    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Information,
        Message = "IMSI search: {Digits} digits, {Kind}, {Total} match(es), {ElapsedMs} ms, "
                  + "{RowsExamined} rows examined")]
    private partial void LogImsiSearch(
        int digits, string kind, int total, long elapsedMs, long rowsExamined);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Warning,
        Message = "IMSI search read {RowsExamined} rows for a {Kind} term. The IMSI-ordered table "
                  + "may be missing rows, or its primary index is not being used")]
    private partial void LogImsiSearchScan(long rowsExamined, string kind);

    /// <summary>
    /// How many rows a healthy query may read before it is worth a warning.
    /// </summary>
    /// <remarks>
    /// The worst measured ten-digit prefix bucket is 557,158 rows, and the observed reads are
    /// 69,143 for an exact term and 183,831 for a ten-digit prefix. Five million is an order of
    /// magnitude past anything the index should produce, so crossing it means the index is not
    /// being used - a regression that otherwise surfaces as somebody saying search "went slow"
    /// weeks later.
    /// </remarks>
    private const long ScanWarningThreshold = 5_000_000;

    private ClickHouseJsonQuery? _jsonQuery;

    private ClickHouseJsonQuery JsonQuery =>
        _jsonQuery ??= new ClickHouseJsonQuery(_options.ConnectionString, _httpClientFactory);

    /// <inheritdoc />
    public async Task<ImsiSearchOutcome> SearchByImsiAsync(
        ImsiSearchCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["low"] = criteria.Term.Low.ToString(CultureInfo.InvariantCulture),
            ["high"] = criteria.Term.High.ToString(CultureInfo.InvariantCulture),
            ["limit"] = criteria.Limit.ToString(CultureInfo.InvariantCulture),
            ["offset"] = criteria.Offset.ToString(CultureInfo.InvariantCulture),
        };

        var filters = BuildImsiFilters(criteria, parameters);

        // count() OVER () rather than a second COUNT query: the total and the page come from one
        // pass over the same range, so they cannot disagree and the round trip is paid once.
        //
        // $$ so a single brace stays literal - ClickHouse's own {name:Type} parameter syntax - and
        // {{filters}} is the C# interpolation. The rest of this store uses the same pattern.
        var sql = $$"""
            SELECT
                b.imsi, b.msisdn, b.imei, b.tac,
                t.manufacturer, t.marketingName, t.deviceType, t.operatingSystem,
                b.active,
                -- NULL when no daily file has ever named this binding: it is present only because
                -- the initial dump listed it, and nothing has confirmed or removed it since.
                b.last_change_date,
                count() OVER () AS total
            FROM sqm.binding_by_imsi AS b FINAL
            LEFT JOIN sqm.tac AS t ON t.tac = b.tac
            WHERE b.imsi >= {low:UInt64} AND b.imsi <= {high:UInt64}
            {{filters}}
            ORDER BY b.imsi, b.active DESC, b.msisdn, b.imei
            LIMIT {limit:UInt32} OFFSET {offset:UInt32}
            """;

        var result = await JsonQuery.ExecuteAsync(sql, parameters, ct).ConfigureAwait(false);

        var rows = new List<ImsiBindingRow>(result.Rows.Count);
        var total = 0;

        foreach (var row in result.Rows)
        {
            rows.Add(new ImsiBindingRow(
                Imsi: ClickHouseJsonResult.UInt64(row, 0),
                Msisdn: ClickHouseJsonResult.UInt64(row, 1),
                Imei: ClickHouseJsonResult.Text(row, 2) ?? string.Empty,
                Tac: ClickHouseJsonResult.NullIfEmpty(row, 3),
                Manufacturer: ClickHouseJsonResult.NullIfEmpty(row, 4),
                MarketingName: ClickHouseJsonResult.NullIfEmpty(row, 5),
                DeviceType: ClickHouseJsonResult.NullIfEmpty(row, 6),
                OperatingSystem: ClickHouseJsonResult.NullIfEmpty(row, 7),
                IsActive: ClickHouseJsonResult.Int32(row, 8) == 1,
                LastChangeDate: ClickHouseJsonResult.Date(row, 9)));

            total = ClickHouseJsonResult.Int32(row, 10);
        }

        // Facts are only meaningful for one SIM. Computing them over a prefix would fold thousands
        // of unrelated SIMs into a single misleading "2 handsets".
        var facts = criteria.Term.IsExact && rows.Count > 0
            ? await GetImsiFactsAsync(criteria.Term.Low, ct).ConfigureAwait(false)
            : null;

        var kind = criteria.Term.IsExact ? "exact" : "prefix";
        LogImsiSearch(criteria.Term.DigitCount, kind, total, result.ElapsedMs, result.RowsRead);

        if (result.RowsRead > ScanWarningThreshold)
        {
            LogImsiSearchScan(result.RowsRead, kind);
        }

        return new ImsiSearchOutcome(rows, total, facts, result.ElapsedMs, result.RowsRead);
    }

    /// <inheritdoc />
    public async Task<ImsiHistoryOutcome> GetImsiHistoryAsync(
        ulong imsi, DateOnly? fromDate, DateOnly? toDate, int limit, CancellationToken ct)
    {
        // The bounds are applied to the partition key, so a narrow range never opens the other
        // partitions at all - the difference between 586 ms across 133 days and 87 ms across 14.
        // An absent bound becomes the extreme of the range rather than NULL, because a NULL
        // comparison would defeat the pruning entirely.
        const string Sql = """
            SELECT e.data_date, e.seq, e.msisdn, e.imei, t.manufacturer, t.marketingName, e.label
            FROM sqm.binding_event AS e
            LEFT JOIN sqm.tac AS t ON t.tac = e.tac
            WHERE e.imsi = {imsi:UInt64}
              AND e.data_date >= {fromDate:Date}
              AND e.data_date <= {toDate:Date}
            ORDER BY e.data_date DESC, e.seq DESC
            LIMIT {limit:UInt32}
            """;

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["imsi"] = imsi.ToString(CultureInfo.InvariantCulture),
            ["fromDate"] = (fromDate ?? new DateOnly(1970, 1, 1)).ToString(
                "yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["toDate"] = (toDate ?? new DateOnly(2100, 1, 1)).ToString(
                "yyyy-MM-dd", CultureInfo.InvariantCulture),

            // One more than asked for, so "there are more" is a fact rather than a guess.
            ["limit"] = (limit + 1).ToString(CultureInfo.InvariantCulture),
        };

        var result = await JsonQuery.ExecuteAsync(Sql, parameters, ct).ConfigureAwait(false);

        var events = new List<ImsiHistoryRow>(Math.Min(limit, result.Rows.Count));

        foreach (var row in result.Rows)
        {
            events.Add(new ImsiHistoryRow(
                Date: ClickHouseJsonResult.Date(row, 0) ?? default,
                Sequence: ClickHouseJsonResult.Int32(row, 1),
                Msisdn: ClickHouseJsonResult.UInt64(row, 2),
                Imei: ClickHouseJsonResult.Text(row, 3) ?? string.Empty,
                Manufacturer: ClickHouseJsonResult.NullIfEmpty(row, 4),
                MarketingName: ClickHouseJsonResult.NullIfEmpty(row, 5),
                Added: string.Equals(
                    ClickHouseJsonResult.Text(row, 6), "add", StringComparison.OrdinalIgnoreCase)));
        }

        var truncated = events.Count > limit;
        if (truncated)
        {
            events.RemoveAt(events.Count - 1);
        }

        return new ImsiHistoryOutcome(events, truncated, result.ElapsedMs, result.RowsRead);
    }

    /// <summary>Aggregate facts for one IMSI.</summary>
    private async Task<ImsiFacts> GetImsiFactsAsync(ulong imsi, CancellationToken ct)
    {
        const string Sql = """
            SELECT
                uniqExact(msisdn)                    AS subscribers,
                uniqExactIf(imei, length(imei) = 14) AS handsets,
                countIf(active = 1)                  AS active_bindings,
                min(last_change_date)                AS first_seen,
                max(last_change_date)                AS last_seen,
                -- By date, not by sequence: a binding re-derived after a late or replaced day
                -- carries the log's highest sequence as its version, dated or not.
                countIf(last_change_date IS NOT NULL) > 0 AS ever_touched
            FROM sqm.binding_by_imsi AS b FINAL
            WHERE b.imsi = {imsi:UInt64}
            """;

        var result = await JsonQuery.ExecuteAsync(
            Sql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["imsi"] = imsi.ToString(CultureInfo.InvariantCulture),
            },
            ct).ConfigureAwait(false);

        if (result.Rows.Count == 0)
        {
            return new ImsiFacts(0, 0, 0, null, null, false);
        }

        var row = result.Rows[0];

        return new ImsiFacts(
            DistinctSubscribers: ClickHouseJsonResult.Int32(row, 0),
            DistinctHandsets: ClickHouseJsonResult.Int32(row, 1),
            ActiveBindings: ClickHouseJsonResult.Int32(row, 2),
            FirstSeen: ClickHouseJsonResult.Date(row, 3),
            LastSeen: ClickHouseJsonResult.Date(row, 4),
            EverTouchedByDailyFile: ClickHouseJsonResult.Int32(row, 5) == 1);
    }

    /// <summary>
    /// Builds the optional WHERE fragments, binding every value.
    /// </summary>
    /// <remarks>
    /// The clause text is fixed at compile time; only which clauses appear is decided at runtime,
    /// and each references a bound parameter rather than a value.
    /// </remarks>
    private static string BuildImsiFilters(
        ImsiSearchCriteria criteria, Dictionary<string, string> parameters)
    {
        var clauses = new List<string>(4);

        if (criteria.ActiveOnly)
        {
            clauses.Add("AND b.active = 1");
        }

        if (!string.IsNullOrWhiteSpace(criteria.DeviceType))
        {
            clauses.Add("AND t.deviceType = {deviceType:String}");
            parameters["deviceType"] = criteria.DeviceType;
        }

        if (!string.IsNullOrWhiteSpace(criteria.Manufacturer))
        {
            clauses.Add("AND t.manufacturer = {manufacturer:String}");
            parameters["manufacturer"] = criteria.Manufacturer;
        }

        // A date filter excludes every binding the daily feed has never mentioned, because their
        // last_change_date is NULL. That is the right answer to "changed in this window" and a
        // surprising one to anybody not told, so the UI says so beside the control.
        if (criteria.From is { } from)
        {
            clauses.Add("AND b.last_change_date >= {fromDate:Date}");
            parameters["fromDate"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (criteria.To is { } to)
        {
            clauses.Add("AND b.last_change_date <= {toDate:Date}");
            parameters["toDate"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return clauses.Count == 0 ? string.Empty : string.Join("\n            ", clauses);
    }
}
