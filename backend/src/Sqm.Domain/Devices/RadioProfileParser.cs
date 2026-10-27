using System.Globalization;
using System.Text.RegularExpressions;

namespace Sqm.Domain.Devices;

/// <summary>The GSMA columns a radio profile is read from, exactly as the catalogue holds them.</summary>
public sealed record TacRecord(
    string Tac,
    string DeviceType,
    string BandDetails,
    string RemovableEuicc,
    string NonremovableEuicc,
    string NtnConnectivity,
    string AllocationDate,
    string ImsEmergency);

/// <summary>Reads a device model's radios and bands out of its GSMA <c>bandDetails</c>.</summary>
/// <remarks>
/// <para>
/// <b>The field has a grammar, and it is used rather than searched.</b> Measured on the catalogue of
/// 2026-10 (271,830 TACs): sections separated by <c>" | "</c>, each a label and a colon -
/// <c>2G/3G</c> (259,842 TACs), <c>4G</c> (130,340), <c>LPWAN</c> (4,316), <c>5G NR</c> (37,163),
/// <c>5G NA Options</c> (36,286), <c>NTN</c> (963), <c>SATELLITE</c> (1,294), <c>Radio Interface</c>
/// (271,824), and band-combination sections (<c>4G CA</c>, <c>5G DC BC EN-DC …</c>) that describe
/// combinations rather than bands. The dashboard's capability flags were, until 2026-10-27, a search
/// for "LTE" and "5G NA" anywhere in the text - which also counted a phone listing only an architecture
/// option and no NR band as 5G.
/// </para>
/// <para>
/// <b>Nothing is guessed.</b> A token no rule here recognises is kept in
/// <see cref="RadioProfile.Unparsed"/> with its section, so an ambiguity is counted and shown rather
/// than silently becoming a band or silently disappearing.
/// </para>
/// </remarks>
public static partial class RadioProfileParser
{
    /// <summary>
    /// Bumped whenever a rule here changes what it reads, so every stored version is parsed again.
    /// </summary>
    public const int Version = 1;

    [GeneratedRegex(@"^LTE\s+(?:FDD|TDD|V2X)\s+BAND\s+(\d{1,3})$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex LteBand { get; }

    [GeneratedRegex(@"^WCDMA\s+TDD\s+Band\s+[A-F]$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex UtraTddLetteredBand { get; }

    [GeneratedRegex(@"^B(\d{1,3})$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex BareLteBand { get; }

    [GeneratedRegex(@"^(?:802\.11[a-z+]*|11[a-z]{1,3}|R\d{1,2}|MT\d{2,5})$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex NonCellularWord { get; }

    /// <summary>
    /// Words of the free-text <c>Radio Interface</c> field (and strays in <c>2G/3G</c>) that name a
    /// technology. Measured on the 2026-10 catalogue: the field holds a manufacturer's own words -
    /// "GSM/GPRS/EDGE/UMTS/WCDMA", "EDGE GPRS GPS HSUPA HSDPA" - in 419 distinct forms.
    /// </summary>
    private static readonly Dictionary<string, Technology> TechnologyWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GSM"] = Technology.Gsm, ["GPRS"] = Technology.Gsm, ["EGPRS"] = Technology.Gsm, ["EDGE"] = Technology.Gsm,
        ["GSM-R"] = Technology.Gsm,
        ["WCDMA"] = Technology.Umts, ["UMTS"] = Technology.Umts, ["HSDPA"] = Technology.Umts, ["HSUPA"] = Technology.Umts,
        ["HSPA"] = Technology.Umts, ["HSPA+"] = Technology.Umts, ["TD-SCDMA"] = Technology.Umts, ["TDS-CDMA"] = Technology.Umts,
        ["3G"] = Technology.Umts, ["FOMA"] = Technology.Umts,
        ["LTE"] = Technology.Lte, ["4G"] = Technology.Lte,
        ["CDMA"] = Technology.NonGsmLegacy, ["CDMA2000"] = Technology.NonGsmLegacy, ["3GPP2"] = Technology.NonGsmLegacy,
        ["AMPS"] = Technology.NonGsmLegacy, ["TDMA"] = Technology.NonGsmLegacy, ["PHS"] = Technology.NonGsmLegacy,
        ["SATELLITE"] = Technology.Satellite, ["BGAN"] = Technology.Satellite, ["Inmarsat"] = Technology.Satellite,
        ["L-Band"] = Technology.Satellite, ["EGAL"] = Technology.Satellite,
    };

    /// <summary>
    /// Words that are understood and say nothing about the cellular radios: other radios (GPS,
    /// Bluetooth, Wi-Fi, FM, DECT, NFC), filler ("NONE", "Other", "support"), and NTN, which is read
    /// from <c>ntnConnectivity</c> rather than from here.
    /// </summary>
    private static readonly HashSet<string> NeutralWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "NONE", "OTHER", "Other", "Radio", "Interface", "support", "Internal", "and", "&", "+", "FDD", "TDD", "FDD:", "TDD:",
        "GPS", "A-GPS", "aGPS", "AGPS", "GNSS", "Bluetooth", "BT", "WIFI", "Wi-Fi", "WLAN", "FM", "F.M", "MMS", "NFC",
        "DECT", "TDMB", "WAPI", "WiMax", "QVGA", "DSDA", "3GPP", "R99", "GAN", "NTN", "SIM", "1",
    };

    [GeneratedRegex(@"^n(\d{1,3})$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex NrBand { get; }

    [GeneratedRegex(@"^GSM\s*(\d{3,4})(?:\s*\(GSM\s*\d{3,4}\))?$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex GsmBand { get; }

    [GeneratedRegex(@"^WCDMA\s+(?:FDD|TDD)\s+BAND\s+(\d{1,3})$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex WcdmaBand { get; }

    [GeneratedRegex(@"^Cat-(NB|M)(?:\d|-LTE(?:FDD|TDD))?(?:\s+LTE\s+(?:FDD|TDD)\s+BAND\s+(\d{1,3}))?$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex LpwanToken { get; }

    [GeneratedRegex(@"^(?:E-UTRA\s+B(\d{1,3})|NR\s+FR\d\s+n(\d{1,3}))$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex NtnBand { get; }

    [GeneratedRegex(@"^5G-(\d)Series$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex ArchitectureOption { get; }

    [GeneratedRegex(@"^3GPP\s+NTN\s+(Only|&\s*TN)\s*(?:\(([^)]*)\))?$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex NtnStatement { get; }

    /// <summary>Parses one GSMA record.</summary>
    public static RadioProfile Parse(TacRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var state = new State();

        foreach (var section in Sections(record.BandDetails))
        {
            Read(state, section.Label, section.Values);
        }

        var nsa = state.Options.Overlaps([3, 4, 7]) || state.DualConnectivity
            ? Capability.Supported
            : Capability.NotStated;
        var sa = state.Options.Contains(2) ? Capability.Supported : Capability.NotStated;
        var (ntn, ntnMode, ntnOrbits) = ReadNtn(record.NtnConnectivity);

        return new RadioProfile(
            BandsListed: state.Technologies > 0,
            Gsm: state.Gsm || state.EcGsmIot,
            Umts: state.Umts,
            Cdma: state.Cdma,
            LteBroadband: state.LteBroadband,
            LteM: state.LteM,
            NbIot: state.NbIot,
            EcGsmIot: state.EcGsmIot,
            Nr: state.Nr,
            NrRedCap: state.NrRedCap,
            LteBands: Sorted(state.LteBands),
            NrBands: Sorted(state.NrBands),
            LteMBands: Sorted(state.LteMBands),
            NbIotBands: Sorted(state.NbIotBands),
            UmtsBands: Sorted(state.UmtsBands),
            GsmBands: Sorted(state.GsmBands),
            Sa: sa,
            Nsa: nsa,
            ArchitectureOptions: state.OptionsText,
            Esim: ReadEsim(record.RemovableEuicc, record.NonremovableEuicc),
            Ntn: ntn,
            NtnMode: ntnMode,
            NtnOrbits: ntnOrbits,
            NtnBands: state.NtnBands.Order(StringComparer.Ordinal).ToList(),
            SatelliteListed: state.Satellite,
            ImsEmergency: ReadYesNo(record.ImsEmergency),
            AllocationDate: ReadDate(record.AllocationDate),
            DeviceClass: DeviceClassOf(record.DeviceType),
            Unparsed: state.Unparsed);
    }

    /// <summary>
    /// The dashboard's coarse device class for a GSMA device type - the mapping
    /// <c>agg_device_class_daily</c> has used since migration 007, now in one place.
    /// </summary>
    public static string DeviceClassOf(string? deviceType) => deviceType?.Trim() switch
    {
        "Smartphone" => "Smartphone",
        "Mobile Phone/Feature phone" => "Feature phone",
        "Tablet" or "e-Book" => "Tablet",
        "Modem" or "Dongle" or "Module" or "WLAN Router" or "IoT Device" or "Vehicle" or "Vehicle TCU"
            or "Connected Computer" or "UAS/UAV" or "Satellite" => "IoT / M2M",
        "Wearable" => "Wearable",
        _ => "Other",
    };

    /// <summary>
    /// eSIM from the two eUICC counts. Supported when either is one or more; unsupported only when
    /// both say zero; otherwise not stated - <c>00</c> is the "field not filled" form, not a zero.
    /// </summary>
    public static Capability ReadEsim(string? removable, string? nonremovable)
    {
        var a = Count(removable);
        var b = Count(nonremovable);

        if (a > 0 || b > 0)
        {
            return Capability.Supported;
        }

        return a == 0 && b == 0 ? Capability.Unsupported : Capability.NotStated;

        // Null for "not stated": 00, blank, or anything that is not a count.
        static int? Count(string? value)
        {
            var text = value?.Trim();
            if (string.IsNullOrEmpty(text) || text == "00")
            {
                return null;
            }

            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        }
    }

    private static (Capability Ntn, string? Mode, string? Orbits) ReadNtn(string? value)
    {
        var text = value?.Trim() ?? string.Empty;

        if (string.Equals(text, "No", StringComparison.OrdinalIgnoreCase))
        {
            return (Capability.Unsupported, null, null);
        }

        var match = NtnStatement.Match(text);
        if (!match.Success)
        {
            // "Not Known" - 241,494 TACs, 88.8% - and anything this rule does not recognise.
            return (Capability.NotStated, null, null);
        }

        var mode = match.Groups[1].Value.StartsWith('&') ? "NTN and TN" : "NTN only";
        var orbits = match.Groups[2].Success
            ? string.Join(",", match.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            : null;

        return (Capability.Supported, mode, string.IsNullOrEmpty(orbits) ? null : orbits);
    }

    private static Capability ReadYesNo(string? value) => value?.Trim() switch
    {
        "Y" or "Yes" => Capability.Supported,
        "N" or "No" => Capability.Unsupported,
        _ => Capability.NotStated,
    };

    private static DateOnly? ReadDate(string? value) =>
        DateOnly.TryParseExact(value?.Trim(), ["dd-MMM-yyyy", "d-MMM-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static IEnumerable<(string Label, IReadOnlyList<string> Values)> Sections(string? bandDetails)
    {
        if (string.IsNullOrWhiteSpace(bandDetails))
        {
            yield break;
        }

        foreach (var part in bandDetails.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                yield return (part, []);
                continue;
            }

            var values = part[(colon + 1)..]
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            yield return (part[..colon].Trim(), values);
        }
    }

    private static void Read(State state, string label, IReadOnlyList<string> values)
    {
        switch (label)
        {
            case "2G/3G":
                state.Technologies++;
                foreach (var token in values)
                {
                    if (Number(GsmBand, token) is { } gsm)
                    {
                        state.Gsm = true;
                        state.GsmBands.Add(gsm);
                    }
                    else if (Number(WcdmaBand, token) is { } wcdma)
                    {
                        state.Umts = true;
                        state.UmtsBands.Add(wcdma);
                    }
                    else if (UtraTddLetteredBand.IsMatch(token))
                    {
                        // UTRA TDD bands are lettered, A to F (12,674 TACs list band A).
                        state.Umts = true;
                    }
                    else if (Number(LteBand, token) is { } lte)
                    {
                        // An LTE band filed under 2G/3G (bands 60-62, 26 TACs). It is still an LTE band.
                        state.LteBroadband = true;
                        state.LteBands.Add(lte);
                    }
                    else if (token.StartsWith("GSM-R", StringComparison.OrdinalIgnoreCase))
                    {
                        state.Gsm = true;
                    }
                    else if (!ReadWords(state, token))
                    {
                        state.Unparsed.Add($"{label}: {token}");
                    }
                }

                break;

            case "4G":
                state.Technologies++;
                state.LteBroadband = true;
                foreach (var token in values)
                {
                    if (Number(LteBand, token) is { } band)
                    {
                        state.LteBands.Add(band);
                    }
                    else
                    {
                        state.Unparsed.Add($"{label}: {token}");
                    }
                }

                break;

            case "5G NR":
            case "5G REDCAP":
                state.Technologies++;
                state.Nr = true;
                state.NrRedCap |= label == "5G REDCAP";
                foreach (var token in values)
                {
                    if (Number(NrBand, token) is { } band)
                    {
                        state.NrBands.Add(band);
                    }
                    else
                    {
                        state.Unparsed.Add($"{label}: {token}");
                    }
                }

                break;

            case "5G NA Options":
                state.OptionsText = string.Join(",", values);
                foreach (var token in values)
                {
                    if (Number(ArchitectureOption, token) is { } option)
                    {
                        state.Options.Add(option);
                    }
                    else
                    {
                        state.Unparsed.Add($"{label}: {token}");
                    }
                }

                break;

            case "LPWAN":
                state.Technologies++;
                ReadLpwan(state, label, values);
                break;

            case "NTN":
                foreach (var token in values)
                {
                    var match = NtnBand.Match(token);
                    if (match.Success)
                    {
                        state.NtnBands.Add(match.Groups[1].Success ? $"B{match.Groups[1].Value}" : $"n{match.Groups[2].Value}");
                    }
                    else
                    {
                        state.Unparsed.Add($"{label}: {token}");
                    }
                }

                break;

            case "SATELLITE":
                state.Satellite = true;
                break;

            case "Radio Interface":
                foreach (var token in values)
                {
                    if (!ReadWords(state, token))
                    {
                        state.Unparsed.Add($"{label}: {token}");
                    }
                }

                break;

            default:
                // Band combinations and categories: what a device can aggregate, not which bands
                // it has. Two of them still say something. An LTE category is a statement that the
                // device is LTE, and an EN-DC or NE-DC combination is non-standalone 5G by
                // definition.
                if (label.StartsWith("4G", StringComparison.Ordinal))
                {
                    state.Technologies++;
                    state.LteBroadband = true;
                }
                else if (label.StartsWith("5G", StringComparison.Ordinal))
                {
                    state.DualConnectivity |= label.Contains("EN-DC", StringComparison.Ordinal)
                                              || label.Contains("NE-DC", StringComparison.Ordinal);
                }
                else
                {
                    state.Unparsed.Add($"{label}: (section)");
                }

                break;
        }
    }

    private static void ReadLpwan(State state, string label, IReadOnlyList<string> values)
    {
        foreach (var token in values)
        {
            if (string.Equals(token, "EC-GSM-IoT", StringComparison.OrdinalIgnoreCase))
            {
                state.EcGsmIot = true;
                continue;
            }

            var match = LpwanToken.Match(token);
            if (!match.Success)
            {
                state.Unparsed.Add($"{label}: {token}");
                continue;
            }

            var narrowband = string.Equals(match.Groups[1].Value, "NB", StringComparison.OrdinalIgnoreCase);
            if (narrowband)
            {
                state.NbIot = true;
            }
            else
            {
                state.LteM = true;
            }

            if (match.Groups[2].Success)
            {
                var band = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                (narrowband ? state.NbIotBands : state.LteMBands).Add(band);
            }
        }
    }

    /// <summary>
    /// Reads a free-text token word by word. Every word that names a technology counts; a word that
    /// is understood and says nothing (another radio, filler) is passed over. Returns false when any
    /// word is not understood, so the token is kept as an ambiguity - with whatever it did say applied.
    /// </summary>
    private static bool ReadWords(State state, string token)
    {
        if (string.Equals(token, "2.4-2.5 GHz", StringComparison.OrdinalIgnoreCase))
        {
            return true; // the ISM band: Wi-Fi and Bluetooth, not a cellular radio
        }

        var understood = true;
        foreach (var raw in token.Split([' ', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.TrimEnd(':');

            if (TechnologyWords.TryGetValue(word, out var technology))
            {
                switch (technology)
                {
                    case Technology.Gsm:
                        state.Gsm = true;
                        state.Technologies++;
                        break;
                    case Technology.Umts:
                        state.Umts = true;
                        state.Technologies++;
                        break;
                    case Technology.Lte:
                        state.LteBroadband = true;
                        state.Technologies++;
                        break;
                    case Technology.NonGsmLegacy:
                        state.Cdma = true;
                        state.Technologies++;
                        break;
                    case Technology.Satellite:
                        state.Satellite = true;
                        break;
                }
            }
            else if (Number(BareLteBand, word) is { } band)
            {
                state.LteBroadband = true;
                state.LteBands.Add(band);
                state.Technologies++;
            }
            else if (!NeutralWords.Contains(word) && !NonCellularWord.IsMatch(word))
            {
                understood = false;
            }
        }

        return understood;
    }

    private enum Technology
    {
        Gsm,
        Umts,
        Lte,
        NonGsmLegacy,
        Satellite,
    }

    private static int? Number(Regex pattern, string token)
    {
        var match = pattern.Match(token);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static List<int> Sorted(HashSet<int> values) => [.. values.Order()];

    /// <summary>What the sections have said so far.</summary>
    private sealed class State
    {
        public int Technologies { get; set; }

        public bool Gsm { get; set; }

        public bool Umts { get; set; }

        public bool Cdma { get; set; }

        public bool LteBroadband { get; set; }

        public bool LteM { get; set; }

        public bool NbIot { get; set; }

        public bool EcGsmIot { get; set; }

        public bool Nr { get; set; }

        public bool NrRedCap { get; set; }

        public bool DualConnectivity { get; set; }

        public bool Satellite { get; set; }

        public string OptionsText { get; set; } = string.Empty;

        public HashSet<int> Options { get; } = [];

        public HashSet<int> LteBands { get; } = [];

        public HashSet<int> NrBands { get; } = [];

        public HashSet<int> LteMBands { get; } = [];

        public HashSet<int> NbIotBands { get; } = [];

        public HashSet<int> UmtsBands { get; } = [];

        public HashSet<int> GsmBands { get; } = [];

        public HashSet<string> NtnBands { get; } = new(StringComparer.Ordinal);

        public List<string> Unparsed { get; } = [];
    }
}
