using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Sqm.Application.DataImport;
using Sqm.Domain.Devices;
using Sqm.Infrastructure.ClickHouse;

namespace Sqm.Infrastructure.DataImport;

/// <summary>The radio profile parse of a TAC version (analytics migration 029).</summary>
/// <remarks>
/// <para>
/// <b>Read in slices, written in slices.</b> A version's <c>bandDetails</c> come to 149 MB of text
/// (271,830 records, up to 23,236 characters each). The records are read in
/// <see cref="Slices"/> hash slices and each slice is parsed and written before the next is read, so
/// the worker never holds the catalogue.
/// </para>
/// <para>
/// <b>Swapped in whole.</b> Each slice goes to <c>tac_radio_staging</c>; the version's partition is
/// then moved over with <c>REPLACE PARTITION</c>, which is atomic. A reader of
/// <c>tac_radio_active</c> - a dashboard rebuild, a planning page - sees the previous parse or this
/// one, never part of each.
/// </para>
/// </remarks>
public sealed partial class ClickHouseTacVersionStore
{
    /// <summary>Hash slices a version is read in: about 19 MB of band text each.</summary>
    internal const int Slices = 8;

    private const int InsertBatch = 10_000;

    /// <inheritdoc />
    public async Task<TacRadioParse?> GetRadioParseAsync(int versionId, CancellationToken ct)
    {
        if (!await RadioDeployedAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT parser_version, rows, unparsed_tacs
            FROM {_database}.tac_radio_parsed FINAL
            WHERE version_id = {versionId}
            """;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new TacRadioParse(
            versionId,
            Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public async Task<TacRadioParse?> ParseRadioAsync(int versionId, CancellationToken ct)
    {
        if (!await RadioDeployedAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await ExecuteAsync($"ALTER TABLE {_database}.tac_radio_staging DROP PARTITION {versionId}", ct).ConfigureAwait(false);

        long rows = 0;
        long unparsed = 0;

        for (var slice = 0; slice < Slices; slice++)
        {
            var profiles = await ReadSliceAsync(versionId, slice, ct).ConfigureAwait(false);
            rows += profiles.Count;
            unparsed += profiles.Count(p => p.Profile.Unparsed.Count > 0);

            foreach (var batch in profiles.Chunk(InsertBatch))
            {
                await InsertStagingAsync(versionId, batch, ct).ConfigureAwait(false);
            }
        }

        await ExecuteAsync(
            $"ALTER TABLE {_database}.tac_radio REPLACE PARTITION {versionId} FROM {_database}.tac_radio_staging", ct)
            .ConfigureAwait(false);
        await ExecuteAsync($"ALTER TABLE {_database}.tac_radio_staging DROP PARTITION {versionId}", ct).ConfigureAwait(false);

        await ExecuteAsync($"""
            INSERT INTO {_database}.tac_radio_parsed (version_id, parser_version, rows, unparsed_tacs, parsed_at)
            VALUES ({versionId}, {RadioProfileParser.Version}, {rows}, {unparsed}, now64(3))
            """, ct).ConfigureAwait(false);

        stopwatch.Stop();
        LogRadioParsed(versionId, rows, unparsed, stopwatch.ElapsedMilliseconds);

        return new TacRadioParse(versionId, RadioProfileParser.Version, rows, unparsed);
    }

    [Microsoft.Extensions.Logging.LoggerMessage(EventId = 3602, Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Parsed the radio profiles of TAC version {VersionId}: {Rows} TACs, {Unparsed} with an unrecognised token, in {ElapsedMs} ms")]
    private partial void LogRadioParsed(int versionId, long rows, long unparsed, long elapsedMs);

    /// <summary>False until migration 029 has created the tables.</summary>
    private async Task<bool> RadioDeployedAsync(CancellationToken ct)
    {
        var value = await ScalarAsync(
            $"SELECT count() FROM system.tables WHERE database = '{_database}' AND name IN ('tac_radio', 'tac_radio_staging', 'tac_radio_parsed')",
            ct).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 3;
    }

    private async Task<List<(string Tac, RadioProfile Profile)>> ReadSliceAsync(int versionId, int slice, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        command.CommandText = $"""
            SELECT tac, deviceType, bandDetails, removableEUICC, nonremovableEUICC, ntnConnectivity,
                   allocationDate, authenticatedIMSEmergencyCallSupport
            FROM {_database}.tac_all
            WHERE version_id = {versionId} AND cityHash64(tac) % {Slices} = {slice}
            """;

        var profiles = new List<(string, RadioProfile)>(capacity: 40_000);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var record = new TacRecord(
                Tac: reader.GetString(0),
                DeviceType: reader.GetString(1),
                BandDetails: reader.GetString(2),
                RemovableEuicc: reader.GetString(3),
                NonremovableEuicc: reader.GetString(4),
                NtnConnectivity: reader.GetString(5),
                AllocationDate: reader.GetString(6),
                ImsEmergency: reader.GetString(7));

            profiles.Add((record.Tac, RadioProfileParser.Parse(record)));
        }

        return profiles;
    }

    private async Task InsertStagingAsync(int versionId, IReadOnlyList<(string Tac, RadioProfile Profile)> batch, CancellationToken ct)
    {
        using var body = new MemoryStream();
        foreach (var (tac, profile) in batch)
        {
            WriteRow(body, versionId, tac, profile);
        }

        body.Position = 0;

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["query"] = $"INSERT INTO {_database}.tac_radio_staging FORMAT JSONEachRow",
            ["database"] = _database,
            ["input_format_skip_unknown_fields"] = "0",
        };

        var url = new UriBuilder(_httpEndpoint)
        {
            Query = string.Join('&', query.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")),
        }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (_authentication is not null)
        {
            request.Headers.Authorization = _authentication;
        }

        request.Content = new StreamContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");

        var client = _httpClientFactory.CreateClient(ClickHouseAnalyticsStore.HttpClientName);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"ClickHouse rejected the radio profiles of TAC version {versionId}: {Truncate(detail)}");
        }
    }

    /// <summary>One JSONEachRow line, its fields named as the columns of migration 029.</summary>
    internal static void WriteRow(Stream body, int versionId, string tac, RadioProfile p)
    {
        using (var json = new Utf8JsonWriter(body))
        {
            json.WriteStartObject();
            json.WriteNumber("version_id", versionId);
            json.WriteString("tac", tac);
            json.WriteNumber("parser_version", RadioProfileParser.Version);
            json.WriteString("device_class", p.DeviceClass);
            json.WriteNumber("bands_listed", Bit(p.BandsListed));
            json.WriteNumber("gsm", Bit(p.Gsm));
            json.WriteNumber("umts", Bit(p.Umts));
            json.WriteNumber("cdma", Bit(p.Cdma));
            json.WriteNumber("lte_broadband", Bit(p.LteBroadband));
            json.WriteNumber("lte_m", Bit(p.LteM));
            json.WriteNumber("nb_iot", Bit(p.NbIot));
            json.WriteNumber("ec_gsm_iot", Bit(p.EcGsmIot));
            json.WriteNumber("nr", Bit(p.Nr));
            json.WriteNumber("nr_redcap", Bit(p.NrRedCap));
            json.WriteNumber("lte", (byte)p.Lte);
            json.WriteNumber("five_g", (byte)p.FiveG);
            json.WriteNumber("sunset", (byte)p.Sunset);
            json.WriteNumber("sa", (byte)p.Sa);
            json.WriteNumber("nsa", (byte)p.Nsa);
            json.WriteString("architecture_options", p.ArchitectureOptions);
            Numbers(json, "lte_bands", p.LteBands);
            Numbers(json, "nr_bands", p.NrBands);
            Numbers(json, "lte_m_bands", p.LteMBands);
            Numbers(json, "nb_iot_bands", p.NbIotBands);
            Numbers(json, "umts_bands", p.UmtsBands);
            Numbers(json, "gsm_bands", p.GsmBands);
            json.WriteNumber("esim", (byte)p.Esim);
            json.WriteNumber("ntn", (byte)p.Ntn);
            json.WriteString("ntn_mode", p.NtnMode ?? string.Empty);
            json.WriteString("ntn_orbits", p.NtnOrbits ?? string.Empty);
            Strings(json, "ntn_bands", p.NtnBands);
            json.WriteNumber("satellite_listed", Bit(p.SatelliteListed));
            json.WriteNumber("ims_emergency", (byte)p.ImsEmergency);

            if (p.AllocationDate is { } allocated)
            {
                json.WriteString("allocation_date", allocated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
            else
            {
                json.WriteNull("allocation_date");
            }

            Strings(json, "unparsed", p.Unparsed);
            json.WriteEndObject();
        }

        body.WriteByte((byte)'\n');

        static int Bit(bool value) => value ? 1 : 0;

        static void Numbers(Utf8JsonWriter json, string name, IReadOnlyList<int> values)
        {
            json.WriteStartArray(name);
            foreach (var value in values)
            {
                json.WriteNumberValue(value);
            }

            json.WriteEndArray();
        }

        static void Strings(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
        {
            json.WriteStartArray(name);
            foreach (var value in values)
            {
                json.WriteStringValue(value);
            }

            json.WriteEndArray();
        }
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.QueryTimeoutSeconds;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
