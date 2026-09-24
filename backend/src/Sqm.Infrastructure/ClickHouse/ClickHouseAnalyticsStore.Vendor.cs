using System.Globalization;
using ClickHouse.Client.ADO;
using Sqm.Contracts.Dashboard;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>
/// The vendor widget: movement, share and growth, from one query.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three questions, and conflating them is how a vendor chart misleads.</b> The original
/// version of this widget answered only the first and was labelled "growth", which is the wrong
/// word for it:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Movement</b> counts add and remove EVENTS in a window. A SIM moved between two handsets
/// thirty times contributes thirty of each and changes the population by nothing. Measured over
/// the whole 133 days, HMD shows 41.7 million adds - far more devices than HMD has on this
/// network - because its devices move, not because it is winning.
/// </item>
/// <item>
/// <b>Share</b> counts the population right now. A vendor with ten million stable handsets
/// produces no events at all and is invisible under movement while dominating this.
/// </item>
/// <item>
/// <b>Growth</b> compares the population against where it started - and must be read relative to
/// the network, which fell 9.3% over the same span. Every vendor's absolute change is negative;
/// the ones that gained share are simply those that fell by less.
/// </item>
/// </list>
/// <para>
/// All three are returned together, so changing the widget's mode is a client-side re-sort rather
/// than a round trip. The whole thing costs about a second: movement reads
/// <c>agg_change_daily</c>, which is partitioned by day so a window prunes partitions, and both
/// population figures read <c>agg_dimension_daily</c>, which holds a few thousand rows per
/// delivery.
/// </para>
/// </remarks>
public sealed partial class ClickHouseAnalyticsStore
{
    /// <inheritdoc />
    public async Task<VendorMovementResponse> GetVendorMovementAsync(
        DateOnly? from, DateOnly? toDate, VendorRanking ranking, int limit, CancellationToken ct)
    {
        // ClampLimit returns the value already rendered for SQL; the int is for sizing the list.
        var takeSql = FilterBuilder.ClampLimit(limit, 50);
        var take = Math.Clamp(limit, 1, 50);
        var bounds = await GetChangeDateBoundsAsync(ct).ConfigureAwait(false);

        // An absent bound becomes the extreme of what the log holds, rather than being left out of
        // the WHERE clause: the column is the partition key, so a bound is what prunes.
        var start = from ?? bounds.Earliest ?? new DateOnly(1970, 1, 1);
        var end = toDate ?? bounds.Latest ?? new DateOnly(2100, 1, 1);

        // The same expression the dimension mart uses for its 'vendor' slice, so movement and
        // population name the same vendors. Two spellings here would silently produce rows that
        // have movement and no population, or the reverse.
        const string VendorExpression = """
            multiIf(c.tac = '', {unknown_device:String},
                    t.tac = '',  {unknown_tac:String},
                    coalesce(nullIf(v.vendor_canonical, ''), nullIf(t.manufacturer, ''),
                             {unknown_tac:String}))
            """;

        var sql = $$"""
            WITH
                population AS (
                    SELECT dim_value AS k,
                           sumIf(bindings, seq = (SELECT min(seq) FROM sqm.mart_ready)) AS at_start,
                           sumIf(bindings, seq = (SELECT max(seq) FROM sqm.mart_ready)) AS at_end
                    FROM sqm.agg_dimension_daily
                    WHERE dimension = 'vendor'
                    GROUP BY k
                ),
                movement AS (
                    SELECT {{VendorExpression}} AS k,
                           sumIf(c.n, c.label = 'add')    AS added,
                           sumIf(c.n, c.label = 'remove') AS removed
                    FROM sqm.agg_change_daily AS c
                    LEFT JOIN sqm.tac AS t ON t.tac = c.tac
                    LEFT JOIN sqm.tac_vendor_map AS v ON v.raw_manufacturer = t.manufacturer
                    WHERE c.data_date >= {fromDate:Date} AND c.data_date <= {toDate:Date}
                    GROUP BY k
                ),
                network AS (
                    SELECT sum(at_start) AS total_start, sum(at_end) AS total_end FROM population
                ),
                combined AS (
                    SELECT
                        p.k                                     AS vendor,
                        coalesce(m.added, 0)                    AS added,
                        coalesce(m.removed, 0)                  AS removed,
                        toInt64(coalesce(m.added, 0)) - toInt64(coalesce(m.removed, 0)) AS net,
                        p.at_end                                AS population,
                        p.at_start                              AS population_at_start,
                        toInt64(p.at_end) - toInt64(p.at_start) AS population_change
                    FROM population AS p
                    LEFT JOIN movement AS m ON m.k = p.k
                    -- A vendor with no population at either end is a mapping artefact, not a
                    -- vendor. Without this the list fills with names nobody recognises.
                    WHERE p.at_end > 0 OR p.at_start > 0
                )
            SELECT
                vendor, added, removed, net, population, population_at_start, population_change,
                round(100.0 * population / nullIf((SELECT total_end FROM network), 0), 4) AS share_pct,
                round(100.0 * population_change / nullIf(population_at_start, 0), 4) AS change_pct,
                round(100.0 * population_change / nullIf(population_at_start, 0)
                      - 100.0 * ((SELECT total_end FROM network) - (SELECT total_start FROM network))
                        / nullIf((SELECT total_start FROM network), 0), 4) AS vs_network,
                round(100.0 * net / nullIf(population, 0), 4) AS net_pct_of_population,
                (SELECT total_end FROM network)   AS network_now,
                (SELECT total_start FROM network) AS network_start
            FROM ({{RankingSql(ranking, takeSql)}})
            ORDER BY {{OrderSql(ranking)}}
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, sql);

        AddLabelParameters(command);
        AddDateParameter(command, "fromDate", start);
        AddDateParameter(command, "toDate", end);

        var rows = new List<VendorMovementRow>(take * 2);
        long networkNow = 0;
        long networkStart = 0;

        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new VendorMovementRow(
                    Vendor: reader.GetString(0),
                    Added: GetInt64(reader, 1),
                    Removed: GetInt64(reader, 2),
                    Net: GetInt64(reader, 3),
                    Population: GetInt64(reader, 4),
                    PopulationAtStart: GetInt64(reader, 5),
                    PopulationChange: GetInt64(reader, 6),
                    SharePercent: GetDouble(reader, 7),
                    // NULL, and so null here, when the base is zero: a vendor with no population
                    // at the start has no percentage change. Read as a double this threw, and the
                    // whole widget failed for the one vendor that was new.
                    PopulationChangePercent: reader.IsDBNull(8) ? null : GetDouble(reader, 8),
                    VsNetworkPoints: reader.IsDBNull(9) ? null : GetDouble(reader, 9),
                    NetPercentOfPopulation: reader.IsDBNull(10) ? null : GetDouble(reader, 10)));

                networkNow = GetInt64(reader, 11);
                networkStart = GetInt64(reader, 12);
            }
        }

        var networkChangePercent = networkStart == 0
            ? 0
            : Math.Round(100.0 * (networkNow - networkStart) / networkStart, 4);

        return new VendorMovementResponse(
            From: start,
            To: end,
            EarliestAvailable: bounds.Earliest,
            LatestAvailable: bounds.Latest,
            NetworkPopulation: networkNow,
            NetworkPopulationAtStart: networkStart,
            NetworkChangePercent: networkChangePercent,
            // Sequence 0 is the initial dump, which covers a 30-day window rather than an instant.
            StartIsInitialDump: true,
            Rows: rows);
    }

    /// <summary>
    /// Picks the rows to return for a ranking.
    /// </summary>
    /// <remarks>
    /// Movement and growth return <b>both ends</b> of the distribution, because a chart that shows
    /// only gainers hides the more interesting half. Share has no interesting bottom end - the
    /// smallest vendors are a long tail of single-digit populations - so it returns the top only.
    /// <para>
    /// The text is chosen from an enum and never from a request value.
    /// </para>
    /// </remarks>
    private static string RankingSql(VendorRanking ranking, string take) => ranking switch
    {
        VendorRanking.Share =>
            $"SELECT * FROM combined ORDER BY population DESC LIMIT {take}",

        VendorRanking.Growth =>
            // Ranked on the vendor's own percentage change. The network's shared decline is
            // subtracted later for display; ordering by the raw percentage keeps the two ends
            // meaningful even when every absolute change is negative.
            $"SELECT * FROM combined WHERE population_at_start >= 100000 ORDER BY "
            + $"population_change / nullIf(population_at_start, 0) DESC LIMIT {take} "
            + "UNION ALL "
            + $"SELECT * FROM combined WHERE population_at_start >= 100000 ORDER BY "
            + $"population_change / nullIf(population_at_start, 0) ASC LIMIT {take}",

        _ =>
            $"SELECT * FROM combined ORDER BY net DESC LIMIT {take} "
            + "UNION ALL "
            + $"SELECT * FROM combined ORDER BY net ASC LIMIT {take}",
    };

    private static string OrderSql(VendorRanking ranking) => ranking switch
    {
        VendorRanking.Share => "population DESC",
        VendorRanking.Growth => "vs_network DESC",
        _ => "net DESC",
    };

    /// <summary>The span the change mart actually covers.</summary>
    /// <remarks>
    /// Returned to the client so the date pickers can be bounded by what exists. A range control
    /// that lets somebody ask for a month the feed never delivered produces an empty chart and no
    /// explanation.
    /// </remarks>
    private async Task<(DateOnly? Earliest, DateOnly? Latest)> GetChangeDateBoundsAsync(
        CancellationToken ct)
    {
        const string Sql = "SELECT min(data_date), max(data_date) FROM sqm.agg_change_daily";

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, Sql);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return (null, null);
        }

        return (GetDateOrNull(reader, 0), GetDateOrNull(reader, 1));
    }

    private static void AddDateParameter(
        ClickHouseCommand command, string name, DateOnly value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        command.Parameters.Add(parameter);
    }
}
