using System.Globalization;
using Microsoft.Extensions.Logging;
using Sqm.Application.Abstractions;
using Sqm.Domain.Identifiers;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>
/// The Devices module: catalogue, detail, timeline and identifiers.
/// </summary>
/// <remarks>
/// <para>
/// <b>A device here is a TAC - a model, not a thing you can hold.</b> A handset is an IMEI, and
/// its first eight digits <i>are</i> its TAC. That is not a convention this code hopes for; it was
/// checked against every one of the 284,341,927 well-formed rows of current state and found to
/// hold with <b>zero</b> exceptions. Everything below depends on it.
/// </para>
/// <para>
/// Three tables serve the module, and each is the cheap answer to a different question:
/// </para>
/// <list type="bullet">
/// <item><c>agg_device_model</c> - one row per model per delivery, ~98,000 rows. The catalogue
/// list sorts, filters and pages over this. The same answer from <c>binding_current</c> is a
/// 2.35 s aggregate over 295 million rows.</item>
/// <item><c>binding_by_imei</c> - current state ordered by IMEI, so a model is the contiguous
/// range <c>['&lt;tac&gt;000000', '&lt;tac&gt;999999']</c>. On <c>binding_current</c> the same
/// filter read all 295 million rows in 6.5-8.9 s, on every one of three models measured.</item>
/// <item><c>agg_change_daily</c> - day-partitioned movement, already there for the dashboard.</item>
/// </list>
/// <para>
/// These queries go over HTTP rather than through the ADO driver, for the server's own
/// <c>rows_read</c> accounting and for per-query limits that actually arrive. See
/// <see cref="ClickHouseJsonQuery"/>.
/// </para>
/// </remarks>
public sealed partial class ClickHouseAnalyticsStore
{
    [LoggerMessage(
        EventId = 2200,
        Level = LogLevel.Information,
        Message = "Device catalogue: {Total} model(s), {ElapsedMs} ms, {RowsExamined} rows examined")]
    private partial void LogDeviceList(int total, long elapsedMs, long rowsExamined);

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Warning,
        Message = "Device identifiers for TAC {Tac} read {RowsExamined} rows. The IMEI-ordered "
                  + "table may be missing rows, or its primary index is not being used")]
    private partial void LogDeviceIdentifierScan(string tac, long rowsExamined);

    /// <summary>
    /// How many rows a healthy identifier query may read before it is worth a warning.
    /// </summary>
    /// <remarks>
    /// The most populous model holds 296,686 bindings, and the range read should be close to that
    /// plus one granule of overshoot at each end. Five million is an order of magnitude past
    /// anything the primary index should produce, so crossing it means the index is not being
    /// used - a regression that otherwise surfaces weeks later as somebody saying it "went slow".
    /// </remarks>
    private const long IdentifierScanWarningThreshold = 5_000_000;

    /// <summary>
    /// The delivery every device query reads.
    /// </summary>
    /// <remarks>
    /// <c>mart_ready</c> and not <c>max(seq)</c> of the mart itself: a delivery being rebuilt has
    /// rows in the mart and is not ready, and reading it would show a half-built catalogue.
    /// </remarks>
    private const string LatestSeq = "(SELECT max(seq) FROM sqm.mart_ready)";

    private const string FirstSeq = "(SELECT min(seq) FROM sqm.mart_ready)";

    /// <summary>
    /// The joins every catalogue query needs, written once.
    /// </summary>
    /// <remarks>
    /// <c>sqm.tac</c> is a view over the active GSMA version, so this is also what makes the
    /// catalogue follow a TAC activation or rollback without anything here changing.
    /// </remarks>
    private const string CatalogueFrom = """
        FROM sqm.agg_device_model AS d
        LEFT JOIN sqm.tac AS t ON t.tac = d.tac
        LEFT JOIN (SELECT * FROM sqm.tac_vendor_map FINAL) AS v ON v.raw_manufacturer = t.manufacturer
        """;

    /// <summary>The vendor expression the dashboard uses, so both name the same vendors.</summary>
    private const string VendorExpression =
        "coalesce(nullIf(v.vendor_canonical, ''), nullIf(t.manufacturer, ''))";

    /// <inheritdoc />
    public async Task<DeviceListOutcome> SearchDevicesAsync(
        DeviceListCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["limit"] = criteria.Limit.ToString(CultureInfo.InvariantCulture),
            ["offset"] = criteria.Offset.ToString(CultureInfo.InvariantCulture),
        };

        var filters = BuildDeviceFilters(criteria, parameters);

        // count() OVER () rather than a second COUNT query: the total and the page come from one
        // pass over the same rows, so they cannot disagree and the round trip is paid once.
        //
        // The ORDER BY always ends in d.tac. Without a unique tiebreaker, two models with equal
        // populations - and there are thousands with a population of 1 - can swap places between
        // requests, which makes page 2 skip a row and repeat another. A stable sort is not a
        // nicety when the client is paging.
        var sql = $$"""
            SELECT
                d.tac,
                t.manufacturer,
                {{VendorExpression}} AS vendor,
                t.brandName, t.modelName, t.marketingName, t.deviceType, t.operatingSystem,
                d.bindings, d.handsets, d.sims, d.subscribers,
                d.first_seen, d.last_seen,
                count() OVER () AS total
            {{CatalogueFrom}}
            WHERE d.seq = {{LatestSeq}}
            {{filters}}
            ORDER BY {{DeviceSortSql(criteria.Sort)}} {{(criteria.Descending ? "DESC" : "ASC")}}, d.tac
            LIMIT {limit:UInt32} OFFSET {offset:UInt32}
            """;

        var result = await JsonQuery.ExecuteAsync(sql, parameters, ct).ConfigureAwait(false);

        var rows = new List<DeviceRow>(result.Rows.Count);
        var total = 0;

        foreach (var row in result.Rows)
        {
            rows.Add(ReadDeviceRow(row));
            total = ClickHouseJsonResult.Int32(row, 14);
        }

        LogDeviceList(total, result.ElapsedMs, result.RowsRead);

        return new DeviceListOutcome(rows, total, result.ElapsedMs, result.RowsRead);
    }

    /// <inheritdoc />
    public async Task<DeviceDetailRow?> GetDeviceAsync(string tac, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tac);

        // One query, four sources. The start population comes from agg_dimension_daily rather than
        // from agg_device_model, because agg_device_model is built per delivery from CURRENT state
        // and therefore cannot describe a delivery that has already passed. The dimension mart has
        // held a per-TAC slice at the first delivery since before this module existed.
        const string Sql = """
            SELECT
                d.tac,
                t.manufacturer,
                coalesce(nullIf(v.vendor_canonical, ''), nullIf(t.manufacturer, '')) AS vendor,
                t.brandName, t.modelName, t.marketingName, t.deviceType, t.operatingSystem,
                d.bindings, d.handsets, d.sims, d.subscribers, d.first_seen, d.last_seen,
                t.oem, t.organisationId, t.allocationDate, t.lastUpdatedDate,
                t.bluetooth, t.nfc, t.wlan, t.simSlot, t.imeiQuantity, t.bandDetails,
                c.has_lte, c.has_5g, c.has_esim, c.ims_emergency,
                -- 2 is the GSMA record's "Not Known", carried through rather than folded into "no".
                if(c.tac = '', 2, 0) AS capability_missing,
                (SELECT sum(bindings) FROM sqm.agg_dimension_daily
                 WHERE dimension = 'tac' AND seq = (SELECT min(seq) FROM sqm.mart_ready)
                   AND dim_value = {tac:String})                       AS bindings_at_start,
                (SELECT sum(bindings) FROM sqm.agg_dimension_daily
                 WHERE dimension = 'tac' AND seq = (SELECT max(seq) FROM sqm.mart_ready)) AS network_now,
                (SELECT sum(bindings) FROM sqm.agg_dimension_daily
                 WHERE dimension = 'tac' AND seq = (SELECT min(seq) FROM sqm.mart_ready)) AS network_start,
                t.tac != ''                                            AS known_to_gsma,
                (SELECT version_id FROM sqm.tac_active FINAL
                 ORDER BY activated_at DESC LIMIT 1)                   AS tac_version
            FROM sqm.agg_device_model AS d
            LEFT JOIN sqm.tac AS t ON t.tac = d.tac
            LEFT JOIN (SELECT * FROM sqm.tac_vendor_map FINAL) AS v ON v.raw_manufacturer = t.manufacturer
            LEFT JOIN sqm.tac_capability AS c ON c.tac = d.tac
            WHERE d.seq = (SELECT max(seq) FROM sqm.mart_ready) AND d.tac = {tac:String}
            LIMIT 1
            """;

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["tac"] = tac };
        var result = await JsonQuery.ExecuteAsync(Sql, parameters, ct).ConfigureAwait(false);

        if (result.Rows.Count == 0)
        {
            return null;
        }

        var row = result.Rows[0];
        var capabilityMissing = ClickHouseJsonResult.Int32(row, 28) == 2;

        return new DeviceDetailRow(
            Summary: ReadDeviceRow(row),
            Oem: ClickHouseJsonResult.NullIfEmpty(row, 14),
            OrganisationId: ClickHouseJsonResult.NullIfEmpty(row, 15),
            AllocationDate: ClickHouseJsonResult.NullIfEmpty(row, 16),
            LastUpdatedDate: ClickHouseJsonResult.NullIfEmpty(row, 17),
            Bluetooth: ClickHouseJsonResult.NullIfEmpty(row, 18),
            Nfc: ClickHouseJsonResult.NullIfEmpty(row, 19),
            Wlan: ClickHouseJsonResult.NullIfEmpty(row, 20),
            SimSlots: ClickHouseJsonResult.NullIfEmpty(row, 21),
            ImeiQuantity: ClickHouseJsonResult.NullIfEmpty(row, 22),
            Bands: ClickHouseJsonResult.NullIfEmpty(row, 23),
            HasLte: Flag(row, 24, capabilityMissing),
            Has5g: Flag(row, 25, capabilityMissing),
            HasEsim: Flag(row, 26, capabilityMissing),
            ImsEmergency: Flag(row, 27, capabilityMissing),
            BindingsAtStart: ClickHouseJsonResult.Int64(row, 29),
            NetworkNow: ClickHouseJsonResult.Int64(row, 30),
            NetworkAtStart: ClickHouseJsonResult.Int64(row, 31),
            KnownToGsma: ClickHouseJsonResult.Int32(row, 32) == 1,
            TacVersionId: ClickHouseJsonResult.Int32(row, 33));
    }

    /// <inheritdoc />
    public async Task<DeviceTimelineOutcome> GetDeviceTimelineAsync(
        string tac, DateOnly? fromDate, DateOnly? toDate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tac);

        var bounds = await GetChangeDateBoundsAsync(ct).ConfigureAwait(false);

        // An absent bound becomes the extreme of what the log holds rather than being left out of
        // the WHERE clause: data_date is the partition key, so a bound is what prunes. Leaving it
        // open would open all 133 partitions to say the same thing.
        var start = fromDate ?? bounds.Earliest ?? new DateOnly(1970, 1, 1);
        var end = toDate ?? bounds.Latest ?? new DateOnly(2100, 1, 1);

        // agg_change_daily is ORDER BY (data_date, tac, label) and PARTITION BY data_date. Within
        // one partition data_date is constant, so generic exclusion search CAN prune on tac here -
        // the one place in this system where a second key column does real work, and it works
        // precisely because the first one is constant inside the part.
        const string Sql = """
            SELECT
                data_date,
                sumIf(n, label = 'add')    AS added,
                sumIf(n, label = 'remove') AS removed
            FROM sqm.agg_change_daily
            WHERE tac = {tac:String}
              AND data_date >= {fromDate:Date}
              AND data_date <= {toDate:Date}
            GROUP BY data_date
            ORDER BY data_date
            """;

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tac"] = tac,
            ["fromDate"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["toDate"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

        var result = await JsonQuery.ExecuteAsync(Sql, parameters, ct).ConfigureAwait(false);

        var points = new List<DeviceTimelineRow>(result.Rows.Count);

        foreach (var row in result.Rows)
        {
            points.Add(new DeviceTimelineRow(
                Date: ClickHouseJsonResult.Date(row, 0) ?? default,
                Added: ClickHouseJsonResult.Int64(row, 1),
                Removed: ClickHouseJsonResult.Int64(row, 2)));
        }

        return new DeviceTimelineOutcome(
            points, bounds.Earliest, bounds.Latest, result.ElapsedMs, result.RowsRead);
    }

    /// <inheritdoc />
    public async Task<DeviceIdentifierOutcome> GetDeviceIdentifiersAsync(
        DeviceIdentifierCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        // A device model is a RANGE of the IMEI-ordered table, not a filter on it. Every
        // well-formed IMEI is TAC(8) + serial(6), so the model's handsets are exactly
        // ['<tac>000000', '<tac>999999'] - which the primary index seeks to. A predicate like
        // startsWith(imei, tac) says the same thing and reads the whole column to prove it.
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["low"] = criteria.Tac + "000000",
            ["high"] = criteria.Tac + "999999",
            ["limit"] = criteria.Limit.ToString(CultureInfo.InvariantCulture),
            ["offset"] = criteria.Offset.ToString(CultureInfo.InvariantCulture),
        };

        var filters = BuildIdentifierFilters(criteria, parameters);

        var sql = $$"""
            SELECT
                b.imei, b.imsi, b.msisdn, b.active, b.last_change_date,
                count() OVER () AS total
            FROM sqm.binding_by_imei AS b FINAL
            WHERE b.imei >= {low:String} AND b.imei <= {high:String}
            {{filters}}
            ORDER BY b.imei, b.active DESC, b.imsi, b.msisdn
            LIMIT {limit:UInt32} OFFSET {offset:UInt32}
            """;

        var result = await JsonQuery.ExecuteAsync(sql, parameters, ct).ConfigureAwait(false);

        var rows = new List<DeviceIdentifierRowData>(result.Rows.Count);
        var total = 0;

        foreach (var row in result.Rows)
        {
            rows.Add(new DeviceIdentifierRowData(
                Imei: ClickHouseJsonResult.Text(row, 0) ?? string.Empty,
                Imsi: ClickHouseJsonResult.UInt64(row, 1),
                Msisdn: ClickHouseJsonResult.UInt64(row, 2),
                IsActive: ClickHouseJsonResult.Int32(row, 3) == 1,
                LastChangeDate: ClickHouseJsonResult.Date(row, 4)));

            total = ClickHouseJsonResult.Int32(row, 5);
        }

        if (result.RowsRead > IdentifierScanWarningThreshold)
        {
            LogDeviceIdentifierScan(criteria.Tac, result.RowsRead);
        }

        return new DeviceIdentifierOutcome(rows, total, result.ElapsedMs, result.RowsRead);
    }

    /// <inheritdoc />
    public async Task<DeviceResolution> ResolveDeviceAsync(
        DeviceSearchKind kind, string digits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(digits);

        var (sql, parameterName, parameterValue) = kind switch
        {
            // One handset. Its model is arithmetic - the first eight digits - but the query still
            // runs, so that a handset this network has never seen is reported as unknown rather
            // than as a model page about somebody else's device.
            DeviceSearchKind.Imei => (
                """
                SELECT DISTINCT substring(imei, 1, 8) AS tac
                FROM sqm.binding_by_imei
                WHERE imei = {imei:String} AND length(imei) = 14
                """,
                "imei", digits),

            // One SIM, which may have been in several handsets of several models.
            DeviceSearchKind.Imsi => (
                """
                SELECT substring(imei, 1, 8) AS tac, count() AS n
                FROM sqm.binding_by_imsi
                WHERE imsi = {imsi:UInt64} AND length(imei) = 14
                GROUP BY tac ORDER BY n DESC LIMIT 25
                """,
                "imsi", digits),

            // One number. binding_current is ordered by MSISDN, so this is the one identifier
            // that the primary table already answers with a seek.
            DeviceSearchKind.Msisdn => (
                """
                SELECT substring(imei, 1, 8) AS tac, count() AS n
                FROM sqm.binding_current
                WHERE msisdn = {msisdn:UInt64} AND length(imei) = 14
                GROUP BY tac ORDER BY n DESC LIMIT 25
                """,
                "msisdn", digits),

            _ => (string.Empty, string.Empty, string.Empty),
        };

        if (sql.Length == 0)
        {
            return new DeviceResolution(kind, [], 0, 0);
        }

        var result = await JsonQuery.ExecuteAsync(
            sql,
            new Dictionary<string, string>(StringComparer.Ordinal) { [parameterName] = parameterValue },
            ct).ConfigureAwait(false);

        var tacs = new List<string>(result.Rows.Count);

        foreach (var row in result.Rows)
        {
            var value = ClickHouseJsonResult.NullIfEmpty(row, 0);
            if (value is not null)
            {
                tacs.Add(value);
            }
        }

        return new DeviceResolution(kind, tacs, result.ElapsedMs, result.RowsRead);
    }

    /// <inheritdoc />
    public async Task<DeviceModelIdentity?> GetModelIdentityAsync(string tac, CancellationToken ct)
    {
        // sqm.tac is a view onto the active version, ordered by tac, so this is a primary-key
        // read of a 270,885-row table. It exists so a device photograph - which belongs to the
        // MODEL, and therefore to all 18 TACs of a Redmi Note 12S or all 184 of a Galaxy A12 -
        // can be found from whichever TAC the caller happens to be looking at.
        const string Sql = """
            SELECT brandName, manufacturer, marketingName
            FROM sqm.tac
            WHERE tac = {tac:String}
            LIMIT 1
            """;

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["tac"] = tac };
        var result = await JsonQuery.ExecuteAsync(Sql, parameters, ct).ConfigureAwait(false);

        if (result.Rows.Count == 0)
        {
            return null;
        }

        var row = result.Rows[0];

        return new DeviceModelIdentity(
            ClickHouseJsonResult.NullIfEmpty(row, 0),
            ClickHouseJsonResult.NullIfEmpty(row, 1),
            ClickHouseJsonResult.NullIfEmpty(row, 2));
    }

    /// <inheritdoc />
    public async Task<DeviceFacetsData> GetDeviceFacetsAsync(CancellationToken ct)
    {
        // Three facets in one round trip. They are counted over device MODELS rather than
        // bindings because they fill filter controls: choosing "Tablet" should say how many rows
        // that leaves, not how many subscribers it covers.
        //
        // Device types are all returned - there are 19 - while manufacturers and operating systems
        // are capped, because the raw GSMA manufacturer field has 10,525 distinct values and a
        // dropdown is not a place to put them.
        var sql = $$"""
            SELECT 'deviceType' AS facet, deviceType AS value, models FROM (
                SELECT coalesce(nullIf(t.deviceType, ''), '(not stated)') AS deviceType,
                       count() AS models
                {{CatalogueFrom}}
                WHERE d.seq = {{LatestSeq}} AND d.tac != ''
                GROUP BY deviceType ORDER BY models DESC LIMIT 30
            )
            UNION ALL
            SELECT 'manufacturer', manufacturer, models FROM (
                SELECT coalesce(nullIf({{VendorExpression}}, ''), '(unknown TAC)') AS manufacturer,
                       count() AS models
                {{CatalogueFrom}}
                WHERE d.seq = {{LatestSeq}} AND d.tac != ''
                GROUP BY manufacturer ORDER BY models DESC LIMIT 40
            )
            UNION ALL
            SELECT 'os', os, models FROM (
                SELECT coalesce(nullIf(trim(t.operatingSystem), ''), '(not stated)') AS os,
                       count() AS models
                {{CatalogueFrom}}
                WHERE d.seq = {{LatestSeq}} AND d.tac != ''
                GROUP BY os ORDER BY models DESC LIMIT 30
            )
            """;

        var result = await JsonQuery.ExecuteAsync(
            sql, new Dictionary<string, string>(StringComparer.Ordinal), ct).ConfigureAwait(false);

        var types = new List<DeviceFacet>();
        var manufacturers = new List<DeviceFacet>();
        var systems = new List<DeviceFacet>();

        foreach (var row in result.Rows)
        {
            var facet = ClickHouseJsonResult.Text(row, 0);
            var value = ClickHouseJsonResult.Text(row, 1) ?? string.Empty;
            var models = ClickHouseJsonResult.Int64(row, 2);

            var target = facet switch
            {
                "deviceType" => types,
                "manufacturer" => manufacturers,
                _ => systems,
            };

            target.Add(new DeviceFacet(value, models));
        }

        return new DeviceFacetsData(types, manufacturers, systems);
    }

    // =======================================================================

    /// <summary>Reads the fourteen columns every catalogue query starts with.</summary>
    private static DeviceRow ReadDeviceRow(System.Text.Json.JsonElement row) => new(
        Tac: ClickHouseJsonResult.Text(row, 0) ?? string.Empty,
        Manufacturer: ClickHouseJsonResult.NullIfEmpty(row, 1),
        Vendor: ClickHouseJsonResult.NullIfEmpty(row, 2),
        Brand: ClickHouseJsonResult.NullIfEmpty(row, 3),
        Model: ClickHouseJsonResult.NullIfEmpty(row, 4),
        MarketingName: ClickHouseJsonResult.NullIfEmpty(row, 5),
        DeviceType: ClickHouseJsonResult.NullIfEmpty(row, 6),
        OperatingSystem: ClickHouseJsonResult.NullIfEmpty(row, 7),
        Bindings: ClickHouseJsonResult.Int64(row, 8),
        Handsets: ClickHouseJsonResult.Int64(row, 9),
        Sims: ClickHouseJsonResult.Int64(row, 10),
        Subscribers: ClickHouseJsonResult.Int64(row, 11),
        FirstSeen: ClickHouseJsonResult.Date(row, 12),
        LastSeen: ClickHouseJsonResult.Date(row, 13));

    /// <summary>
    /// Turns a capability column into a tri-state.
    /// </summary>
    /// <remarks>
    /// The mart stores 1 supported, 0 not supported, 2 the record does not say - and 2 covers 94%
    /// of TACs for IMS emergency. Null is carried all the way to the browser rather than collapsed
    /// into false, because "we do not know" and "no" are different answers and only one of them is
    /// honest here. <paramref name="missing"/> covers the other case: a TAC with no capability row
    /// at all, which is not the same as a row saying nothing.
    /// </remarks>
    private static bool? Flag(System.Text.Json.JsonElement row, int index, bool missing)
    {
        if (missing)
        {
            return null;
        }

        return ClickHouseJsonResult.Int32(row, index) switch
        {
            1 => true,
            0 => false,
            _ => null,
        };
    }

    /// <summary>
    /// The sort expression for a column.
    /// </summary>
    /// <remarks>
    /// Chosen from an enum and never from a request value; every branch is a literal written here
    /// at compile time. This is the allow-list that keeps ORDER BY injection-proof, and it is the
    /// same rule the dashboard's dimension mapping follows.
    /// </remarks>
    private static string DeviceSortSql(DeviceListSort sort) => sort switch
    {
        DeviceListSort.Handsets => "d.handsets",
        DeviceListSort.Sims => "d.sims",
        DeviceListSort.Subscribers => "d.subscribers",
        DeviceListSort.Model => "t.marketingName",
        DeviceListSort.Manufacturer => "t.manufacturer",
        DeviceListSort.Tac => "d.tac",

        // Nulls last whichever way it is sorted: a model no daily file has ever mentioned has
        // nothing to say about recency, and putting thousands of them at the top of "most recently
        // seen" would bury the answer.
        DeviceListSort.LastSeen => "isNull(d.last_seen) ASC, d.last_seen",

        _ => "d.bindings",
    };

    /// <summary>
    /// Builds the optional WHERE fragments, binding every value.
    /// </summary>
    /// <remarks>
    /// The clause text is fixed at compile time; only which clauses appear is decided at runtime,
    /// and each references a bound parameter rather than a value.
    /// </remarks>
    private static string BuildDeviceFilters(
        DeviceListCriteria criteria, Dictionary<string, string> parameters)
    {
        var clauses = new List<string>(6);

        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            // Five columns, case-insensitively. No index helps here and none is needed: the
            // catalogue is ~98,000 rows, which is a scan of something that fits in cache several
            // times over. The TAC is included so a partial code still finds its model.
            clauses.Add("""
                AND (positionCaseInsensitive(d.tac, {text:String}) > 0
                     OR positionCaseInsensitive(t.manufacturer, {text:String}) > 0
                     OR positionCaseInsensitive(t.brandName, {text:String}) > 0
                     OR positionCaseInsensitive(t.modelName, {text:String}) > 0
                     OR positionCaseInsensitive(t.marketingName, {text:String}) > 0)
                """);
            parameters["text"] = criteria.Text.Trim();
        }

        if (!string.IsNullOrWhiteSpace(criteria.Manufacturer))
        {
            // Matched against the curated vendor name, which is what the filter control offers:
            // picking "Samsung" must not return only the one spelling of six that happens to be
            // in the raw column.
            clauses.Add($"AND {VendorExpression} = {{manufacturer:String}}");
            parameters["manufacturer"] = criteria.Manufacturer;
        }

        if (!string.IsNullOrWhiteSpace(criteria.Brand))
        {
            clauses.Add("AND t.brandName = {brand:String}");
            parameters["brand"] = criteria.Brand;
        }

        if (!string.IsNullOrWhiteSpace(criteria.DeviceType))
        {
            clauses.Add("AND t.deviceType = {deviceType:String}");
            parameters["deviceType"] = criteria.DeviceType;
        }

        if (!string.IsNullOrWhiteSpace(criteria.OperatingSystem))
        {
            clauses.Add("AND trim(t.operatingSystem) = {os:String}");
            parameters["os"] = criteria.OperatingSystem;
        }

        if (criteria.MinBindings > 0)
        {
            clauses.Add("AND d.bindings >= {minBindings:UInt64}");
            parameters["minBindings"] = criteria.MinBindings.ToString(CultureInfo.InvariantCulture);
        }

        if (criteria.Tacs is { Count: > 0 } tacs)
        {
            // An identifier search has already resolved to a set of models. The values are TACs
            // that came back from a previous query rather than from the caller, and they are still
            // bound as an array parameter rather than interpolated.
            clauses.Add("AND d.tac IN {tacs:Array(String)}");
            parameters["tacs"] = "['" + string.Join("','", tacs) + "']";
        }

        return clauses.Count == 0 ? string.Empty : string.Join("\n            ", clauses);
    }

    private static string BuildIdentifierFilters(
        DeviceIdentifierCriteria criteria, Dictionary<string, string> parameters)
    {
        var clauses = new List<string>(3);

        if (criteria.ActiveOnly)
        {
            clauses.Add("AND b.active = 1");
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
