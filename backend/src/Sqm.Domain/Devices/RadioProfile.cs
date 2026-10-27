namespace Sqm.Domain.Devices;

/// <summary>What the GSMA record says about one capability: yes, no, or nothing.</summary>
/// <remarks>
/// <para>
/// Three states, never two. The stored codes are the ones <c>sqm.tac_capability.ims_emergency</c>
/// has used since migration 011 - 1 supported, 0 not, 2 not stated - so every capability reads the
/// same way in SQL.
/// </para>
/// <para>
/// "Not stated" is not "no". The GSMA count fields carry <c>00</c> where the record predates the
/// field: 99,298 of 271,830 TACs have <c>00</c> in all four of eUICC, SIM slots and IMEI count, and
/// up to 2018 nearly every allocation does, from 2019 almost none. Read as zero, every older handset
/// would be "known to have no eSIM" on the strength of a field nobody filled in.
/// </para>
/// </remarks>
public enum Capability : byte
{
    /// <summary>The record says the device does not have it.</summary>
    Unsupported = 0,

    /// <summary>The record says the device has it.</summary>
    Supported = 1,

    /// <summary>The record does not say.</summary>
    NotStated = 2,
}

/// <summary>What a device can still use once 2G and 3G are switched off.</summary>
public enum SunsetClass : byte
{
    /// <summary>The record lists no radio bands at all, so nothing can be said.</summary>
    Unknown = 0,

    /// <summary>Broadband LTE (a <c>4G</c> band or an LTE category) or 5G NR.</summary>
    Lte = 1,

    /// <summary>
    /// LTE-M or NB-IoT, and no broadband LTE or NR. Still served after a 2G/3G shutdown - they run on
    /// LTE - but a different population, so reported on its own row (owner's decision, 2026-10-27).
    /// </summary>
    LpwanOnly = 2,

    /// <summary>
    /// Bands are listed and every one is 2G or 3G: GSM, WCDMA, TD-SCDMA, CDMA, or EC-GSM-IoT (which is
    /// GSM). This is the device a sunset strands.
    /// </summary>
    LegacyOnly = 3,
}

/// <summary>A device model's radios, bands and SIM capability, parsed from its GSMA record.</summary>
/// <param name="BandsListed">
/// False when <c>bandDetails</c> names no radio technology - empty, or only
/// <c>Radio Interface: NONE</c> (13 of 271,830 TACs). Every radio answer is then "not stated".
/// </param>
/// <param name="Gsm">GSM (2G), including GPRS/EDGE interfaces and EC-GSM-IoT.</param>
/// <param name="Umts">3G: WCDMA or TD-SCDMA.</param>
/// <param name="Cdma">CDMA2000 / 3GPP2 - not a technology of this network, but a 2G/3G one.</param>
/// <param name="LteBroadband">A <c>4G</c> band or LTE category: ordinary LTE.</param>
/// <param name="LteM">LTE-M (Cat-M1).</param>
/// <param name="NbIot">NB-IoT (Cat-NB).</param>
/// <param name="EcGsmIot">EC-GSM-IoT - a GSM technology, so a 2G one.</param>
/// <param name="Nr">5G NR: an NR band, including RedCap.</param>
/// <param name="NrRedCap">5G RedCap, the reduced-capability NR of IoT devices.</param>
/// <param name="LteBands">E-UTRA band numbers from the <c>4G</c> section, e.g. 3 for "LTE FDD BAND 3".</param>
/// <param name="NrBands">NR band numbers from <c>5G NR</c> and <c>5G REDCAP</c>, e.g. 78 for "n78".</param>
/// <param name="LteMBands">E-UTRA bands named for LTE-M in the <c>LPWAN</c> section.</param>
/// <param name="NbIotBands">E-UTRA bands named for NB-IoT in the <c>LPWAN</c> section.</param>
/// <param name="UmtsBands">UTRA band numbers, e.g. 1 for "WCDMA FDD Band 1".</param>
/// <param name="GsmBands">GSM bands in MHz, e.g. 900 for "GSM 900" and 850 for "GSM850 (GSM800)".</param>
/// <param name="Sa">
/// 5G standalone (3GPP option 2), only as the record states it: "5G-2Series" among the
/// <c>5G NA Options</c>. Not stated otherwise - NR bands alone say nothing about the core.
/// </param>
/// <param name="Nsa">
/// 5G non-standalone, as the record states it: option 3, 4 or 7 among the <c>5G NA Options</c>, or an
/// EN-DC / NE-DC band combination, which is non-standalone by definition. Galaxy A54 (35004012) lists
/// only "5G-5Series" as its option but dozens of EN-DC combinations, so both sources are read.
/// </param>
/// <param name="ArchitectureOptions">The <c>5G NA Options</c> exactly as listed, or empty.</param>
/// <param name="Esim">An embedded or removable eUICC. From the two eUICC counts; see <see cref="Capability"/>.</param>
/// <param name="Ntn">3GPP non-terrestrial (satellite) connectivity, from <c>ntnConnectivity</c>.</param>
/// <param name="NtnMode">"NTN only" or "NTN and TN" when stated, otherwise null.</param>
/// <param name="NtnOrbits">GSO, NGSO or both, as stated in <c>ntnConnectivity</c>.</param>
/// <param name="NtnBands">The <c>NTN</c> section's bands, normalised: "B255", "n256".</param>
/// <param name="SatelliteListed">
/// A <c>SATELLITE</c> band section or radio interface - a satellite radio, which is not the same claim
/// as 3GPP NTN, and is kept apart from it.
/// </param>
/// <param name="ImsEmergency">Authenticated IMS emergency calling - not VoLTE, which the catalogue does not carry.</param>
/// <param name="AllocationDate">When the GSMA allocated the TAC - not a release or manufacture date.</param>
/// <param name="DeviceClass">The coarse class the dashboard's device mix uses.</param>
/// <param name="Unparsed">Band tokens no rule recognised, as "section: token" - an ambiguity, not a guess.</param>
public sealed record RadioProfile(
    bool BandsListed,
    bool Gsm,
    bool Umts,
    bool Cdma,
    bool LteBroadband,
    bool LteM,
    bool NbIot,
    bool EcGsmIot,
    bool Nr,
    bool NrRedCap,
    IReadOnlyList<int> LteBands,
    IReadOnlyList<int> NrBands,
    IReadOnlyList<int> LteMBands,
    IReadOnlyList<int> NbIotBands,
    IReadOnlyList<int> UmtsBands,
    IReadOnlyList<int> GsmBands,
    Capability Sa,
    Capability Nsa,
    string ArchitectureOptions,
    Capability Esim,
    Capability Ntn,
    string? NtnMode,
    string? NtnOrbits,
    IReadOnlyList<string> NtnBands,
    bool SatelliteListed,
    Capability ImsEmergency,
    DateOnly? AllocationDate,
    string DeviceClass,
    IReadOnlyList<string> Unparsed)
{
    /// <summary>Any LTE: broadband, LTE-M or NB-IoT. The dashboard's "LTE" capability.</summary>
    public Capability Lte => !BandsListed
        ? Capability.NotStated
        : LteBroadband || LteM || NbIot ? Capability.Supported : Capability.Unsupported;

    /// <summary>5G NR. The dashboard's "5G" capability: an NR band, not just a listed option.</summary>
    public Capability FiveG => !BandsListed
        ? Capability.NotStated
        : Nr ? Capability.Supported : Capability.Unsupported;

    /// <summary>What the device can use once 2G and 3G are switched off.</summary>
    public SunsetClass Sunset
    {
        get
        {
            if (!BandsListed)
            {
                return SunsetClass.Unknown;
            }

            if (LteBroadband || Nr)
            {
                return SunsetClass.Lte;
            }

            if (LteM || NbIot)
            {
                return SunsetClass.LpwanOnly;
            }

            // Bands are listed but none is terrestrial 2G/3G either - a satellite or NTN radio alone.
            // Nothing a terrestrial sunset decides, and nothing to call legacy.
            return Gsm || Umts || Cdma || EcGsmIot ? SunsetClass.LegacyOnly : SunsetClass.Unknown;
        }
    }
}
