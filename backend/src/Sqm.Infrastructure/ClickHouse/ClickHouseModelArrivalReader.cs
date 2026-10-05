using System.Globalization;
using Microsoft.Extensions.Options;
using Sqm.Application.Devices;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>Reads models' first appearances from the per-model day counts (analytics migration 025).</summary>
/// <remarks>
/// A model's first appearance is the earliest day in <c>tac_day</c>; 1970-01-01 is the initial dump.
/// The table holds about 40,000 rows a day, so every read here is a GROUP BY over a few million rows,
/// under the server-enforced budget of every other read.
/// </remarks>
public sealed class ClickHouseModelArrivalReader : IModelArrivalReader
{
    private static readonly QueryBudget Budget = new(MaxRowsToRead: 50_000_000, MaxExecutionSeconds: 30);

    private static readonly IReadOnlyDictionary<string, string> NoParameters =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // Each model's first day and how many days it has been seen, the dump flagged apart.
    private const string Firsts = """
        SELECT tac,
               max(data_date = toDate(0))                    AS in_dump,
               minIf(data_date, data_date > toDate(0))        AS first_day,
               countIf(data_date > toDate(0))                 AS days_seen,
               argMinIf(imeis, data_date, data_date > toDate(0)) AS first_day_imeis
        FROM tac_day GROUP BY tac
        """;

    private readonly ClickHouseJsonQuery _query;

    /// <summary>Creates the reader over the analytics connection.</summary>
    public ClickHouseModelArrivalReader(IOptions<ClickHouseOptions> options, IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _query = new ClickHouseJsonQuery(options.Value.ConnectionString, httpClientFactory);
    }

    /// <inheritdoc />
    public async Task<ModelArrivalState> GetStateAsync(CancellationToken ct)
    {
        var deployed = await _query.ExecuteAsync(
            "SELECT count() FROM system.tables WHERE database = currentDatabase() AND name = 'tac_day'", NoParameters, ct)
            .ConfigureAwait(false);
        if (ClickHouseJsonResult.Int64(deployed.Rows[0], 0) == 0)
        {
            return new ModelArrivalState(false, false, 0, 0, null);
        }

        var state = await _query.ExecuteAsync("""
            SELECT
                (SELECT uniqExact(partition) FROM system.parts
                  WHERE database = currentDatabase() AND table = 'binding_event' AND active),
                (SELECT uniqExact(data_date) FROM tac_day WHERE data_date > toDate(0)),
                (SELECT count() FROM tac_day WHERE data_date = toDate(0)),
                (SELECT max(data_date) FROM tac_day)
            """, NoParameters, ct).ConfigureAwait(false);

        var row = state.Rows[0];
        var inLog = (int)ClickHouseJsonResult.Int64(row, 0);
        var written = (int)ClickHouseJsonResult.Int64(row, 1);
        var through = ClickHouseJsonResult.Date(row, 3) is { Year: > 1970 } d ? d : (DateOnly?)null;

        return new ModelArrivalState(true, written >= inLog && ClickHouseJsonResult.Int64(row, 2) > 0, written, inLog, through);
    }

    /// <inheritdoc />
    public async Task<NewModelPage> ListAsync(NewModelQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["from"] = Iso(query.From),
            ["to"] = Iso(query.To),
            ["brand"] = query.Brand ?? string.Empty,
            ["type"] = query.DeviceType ?? string.Empty,
            ["limit"] = query.PageSize.ToString(CultureInfo.InvariantCulture),
            ["offset"] = ((long)(query.Page - 1) * query.PageSize).ToString(CultureInfo.InvariantCulture),
        };

        var result = await _query.ExecuteAsync($$"""
            SELECT f.tac, t.brand, t.model, t.device_type, f.first_day, f.days_seen, f.first_day_imeis,
                   m.handsets, m.sims, count() OVER () AS total
            FROM ({{Firsts}}) AS f
            LEFT JOIN (SELECT tac,
                              nullIf(coalesce(nullIf(brandName, ''), manufacturer), '') AS brand,
                              nullIf(marketingName, '')                                 AS model,
                              nullIf(deviceType, '')                                    AS device_type
                       FROM tac) AS t ON t.tac = f.tac
            LEFT JOIN (SELECT tac, handsets, sims FROM agg_device_model
                       WHERE seq = (SELECT max(seq) FROM mart_ready)) AS m ON m.tac = f.tac
            WHERE f.in_dump = 0 AND f.days_seen > 0
              AND f.first_day BETWEEN {from:Date} AND {to:Date}
              {{(query.KnownOnly ? "AND t.tac IS NOT NULL" : string.Empty)}}
              AND ({brand:String} = '' OR positionCaseInsensitiveUTF8(coalesce(t.brand, ''), {brand:String}) > 0)
              AND ({type:String} = '' OR t.device_type = {type:String})
            ORDER BY f.first_day DESC, m.handsets DESC NULLS LAST, f.tac
            LIMIT {limit:UInt32} OFFSET {offset:UInt64}
            SETTINGS join_use_nulls = 1
            """, parameters, ct, Budget).ConfigureAwait(false);

        long total = 0;
        var rows = result.Rows.Select(r =>
        {
            total = ClickHouseJsonResult.Int64(r, 9);
            return new NewModelRow(
                ClickHouseJsonResult.Text(r, 0)!,
                ClickHouseJsonResult.Text(r, 1),
                ClickHouseJsonResult.Text(r, 2),
                ClickHouseJsonResult.Text(r, 3),
                ClickHouseJsonResult.Date(r, 4)!.Value,
                ClickHouseJsonResult.Int32(r, 5),
                ClickHouseJsonResult.Int64(r, 6),
                r[7].ValueKind == System.Text.Json.JsonValueKind.Null ? null : ClickHouseJsonResult.Int64(r, 7),
                r[8].ValueKind == System.Text.Json.JsonValueKind.Null ? null : ClickHouseJsonResult.Int64(r, 8));
        }).ToList();

        return new NewModelPage(rows, total, result.ElapsedMs);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelArrivalPeriod>> ArrivalsAsync(bool weekly, CancellationToken ct)
    {
        var period = weekly ? "toMonday(f.first_day)" : "toStartOfMonth(f.first_day)";
        var result = await _query.ExecuteAsync($$"""
            SELECT {{period}} AS p, count(), countIf(f.tac IN (SELECT tac FROM tac))
            FROM ({{Firsts}}) AS f
            WHERE f.in_dump = 0 AND f.days_seen > 0
            GROUP BY p ORDER BY p
            """, NoParameters, ct, Budget).ConfigureAwait(false);

        return [.. result.Rows.Select(r => new ModelArrivalPeriod(
            ClickHouseJsonResult.Date(r, 0)!.Value, ClickHouseJsonResult.Int64(r, 1), ClickHouseJsonResult.Int64(r, 2)))];
    }

    /// <inheritdoc />
    public async Task<ModelFirstSeen?> FirstSeenAsync(string tac, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tac);

        var result = await _query.ExecuteAsync("""
            SELECT max(data_date = toDate(0)), minIf(data_date, data_date > toDate(0)), countIf(data_date > toDate(0)), count()
            FROM tac_day WHERE tac = {tac:String}
            """, new Dictionary<string, string>(StringComparer.Ordinal) { ["tac"] = tac }, ct, Budget).ConfigureAwait(false);

        var row = result.Rows[0];
        if (ClickHouseJsonResult.Int64(row, 3) == 0)
        {
            return null;
        }

        var days = ClickHouseJsonResult.Int32(row, 2);
        return new ModelFirstSeen(
            tac, ClickHouseJsonResult.Int32(row, 0) == 1, days > 0 ? ClickHouseJsonResult.Date(row, 1) : null, days);
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
