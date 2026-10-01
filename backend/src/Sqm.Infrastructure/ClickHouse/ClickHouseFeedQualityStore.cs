using System.Globalization;
using Microsoft.Extensions.Options;
using Sqm.Application.Quality;
using Sqm.Domain.Quality;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>Reads the per-day feed-quality counts.</summary>
/// <remarks>
/// Through <see cref="ClickHouseJsonQuery"/>, so the read is read-only on the server. Table names
/// are unqualified and resolve against the connection string's database, which is what lets the
/// tests point it at a scratch database.
/// </remarks>
public sealed class ClickHouseFeedQualityStore : IFeedQualityReader
{
    private readonly ClickHouseJsonQuery _query;

    /// <summary>Creates the store over the analytics connection.</summary>
    public ClickHouseFeedQualityStore(IOptions<ClickHouseOptions> options, IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _query = new ClickHouseJsonQuery(options.Value.ConnectionString, httpClientFactory);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeedQualityDay>> GetDaysAsync(
        DateOnly? fromDate, DateOnly? toDate, CancellationToken ct)
    {
        // One row per day, about 240 a year: nothing here needs a budget.
        const string Sql = """
            SELECT data_date, rows_total, sims_total, unknown_device_rows, malformed_imei_rows,
                   unknown_tac_rows, shifted_imei_rows, multi_number_sims, multi_number_sim_rows,
                   tac_version_id
            FROM dq_daily FINAL
            WHERE data_date >= {from:Date} AND data_date <= {to:Date}
            ORDER BY data_date
            """;

        var result = await _query.ExecuteAsync(Sql, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["from"] = (fromDate ?? new DateOnly(1970, 1, 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["to"] = (toDate ?? new DateOnly(2100, 1, 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }, ct).ConfigureAwait(false);

        return [.. result.Rows.Select(row => new FeedQualityDay(
            Date: ClickHouseJsonResult.Date(row, 0) ?? default,
            Rows: ClickHouseJsonResult.Int64(row, 1),
            Sims: ClickHouseJsonResult.Int64(row, 2),
            UnknownDeviceRows: ClickHouseJsonResult.Int64(row, 3),
            MalformedImeiRows: ClickHouseJsonResult.Int64(row, 4),
            UnknownTacRows: ClickHouseJsonResult.Int64(row, 5),
            ShiftedImeiRows: ClickHouseJsonResult.Int64(row, 6),
            MultiNumberSims: ClickHouseJsonResult.Int64(row, 7),
            MultiNumberSimRows: ClickHouseJsonResult.Int64(row, 8),
            TacVersionId: ClickHouseJsonResult.Int32(row, 9)))];
    }
}
