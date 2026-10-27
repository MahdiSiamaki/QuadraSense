using Sqm.Domain.Devices;

namespace Sqm.Domain.Tests;

/// <summary>
/// Tests for reading a device model's radios out of its GSMA record.
/// </summary>
/// <remarks>
/// Every <c>bandDetails</c> here is a real record from the GSMA catalogue of October 2026, by TAC,
/// shortened only where a band-combination section runs to thousands of characters. The cases are the
/// shapes the catalogue measured, each one a place a text search went wrong or could: a feature phone
/// that lists a 5G architecture option and no NR band, a module whose only LTE is NB-IoT, a phone
/// whose record predates the eUICC fields.
/// </remarks>
public class RadioProfileParserTests
{
    /// <summary>Galaxy A54 5G, 35004012.</summary>
    private const string GalaxyA54 =
        "5G NR: n1, n3, n5, n7, n8, n28, n38, n40, n41, n66, n77, n78, n79 | 5G NR 2B CA FR1: CA_n8-n78A | "
        + "5G NR CA FR1: CA_n78 | 5G DC BC EN-DC FR1 2B: DC_1A_n3A, DC_1A_n77A, DC_1A_n78A, DC_2A_n78A, "
        + "DC_3A_n77A, DC_3A_n78A, DC_5A_n78A, DC_7A_n3A | 5G DC BC EN-DC FR1 4B: DC_1A-3A-5A_n78A | "
        + "5G NA Options: 5G-5Series | 4G CA: CA_1, CA_2, CA_3, CA_5, CA_7, CA_8, CA_12, CA_38, CA_40, CA_41, CA_66 | "
        + "4G IB CA 2B: CA_1A-3A, CA_1A-3C, CA_1A-7A | 4G NC CA 2B: CA_1A-1A, CA_2A-2A | "
        + "4G: LTE FDD BAND 1, LTE FDD BAND 2, LTE FDD BAND 3, LTE FDD BAND 4, LTE FDD BAND 5, LTE FDD BAND 7, "
        + "LTE FDD BAND 8, LTE FDD BAND 12, LTE FDD BAND 13, LTE FDD BAND 17, LTE FDD BAND 20, LTE FDD BAND 25, "
        + "LTE FDD BAND 26, LTE FDD BAND 28, LTE FDD Band 32, LTE FDD Band 66, LTE TDD BAND 38, LTE TDD BAND 39, "
        + "LTE TDD BAND 40, LTE TDD BAND 41 | 4G Cat: LTE 12 | 2G/3G: GSM 1800,GSM 1900,GSM850 (GSM800),GSM 900,"
        + "WCDMA FDD Band 1,WCDMA FDD Band 2,WCDMA FDD Band 4,WCDMA FDD Band 5,WCDMA FDD Band 8 | Radio Interface: NONE";

    /// <summary>A feature phone, 01453000: an architecture option and no NR band.</summary>
    private const string FeaturePhoneWithOption =
        "5G NA Options: 5G-5Series | 4G: LTE FDD BAND 2, LTE FDD BAND 4, LTE FDD BAND 5, LTE FDD BAND 7, "
        + "LTE FDD BAND 17 | 4G Cat: LTE 4 | 2G/3G: GSM850 (GSM800),GSM 900,GSM 1800,GSM 1900,WCDMA FDD Band 1,"
        + "WCDMA FDD Band 2,WCDMA FDD Band 5 | Radio Interface: NONE";

    /// <summary>A modem, 01567300, whose only LTE is NB-IoT and LTE-M.</summary>
    private const string LpwanModem =
        "LPWAN: Cat-NB1,Cat-NB2,Cat-NB-LTEFDD LTE FDD BAND 1,Cat-NB-LTEFDD LTE FDD BAND 3,"
        + "Cat-NB-LTEFDD LTE FDD BAND 20,Cat-NB-LTEFDD LTE FDD Band 66,Cat-NB-LTEFDD LTE FDD BAND 85,Cat-M1,"
        + "Cat-M-LTEFDD LTE FDD BAND 1,Cat-M-LTEFDD LTE FDD BAND 3,Cat-M-LTEFDD LTE FDD BAND 25 | Radio Interface: NONE";

    /// <summary>A smartphone, 35405595, with 3GPP NTN; band combinations shortened.</summary>
    private const string NtnPhone =
        "5G NR: n1, n2, n3, n5, n7, n8, n12, n13, n14, n20, n25, n28, n29, n30, n38, n41, n48, n66, n71, n77, n78, n257 | "
        + "5G DC BC EN-DC FR1 2B: DC_1A_n3A, DC_2A_n41(2A), DC_66A-(n)71AA | 5G NA Options: 5G-2Series,5G-3Series | "
        + "4G CA Config: CA_2C, CA_7C | 4G: LTE FDD BAND 1, LTE FDD BAND 3, LTE TDD BAND 41 | "
        + "4G Cat: LTE 12,LTE-DL 20,LTE-UL 18 | 2G/3G: GSM 1800,GSM 900,WCDMA FDD Band 1 | Radio Interface: NONE | "
        + "NTN: E-UTRA B256";

    private static TacRecord Record(
        string bands,
        string deviceType = "Smartphone",
        string removableEuicc = "0",
        string nonremovableEuicc = "0",
        string ntn = "Not Known",
        string allocated = "14-Oct-2020",
        string ims = "Not Known") =>
        new("00000000", deviceType, bands, removableEuicc, nonremovableEuicc, ntn, allocated, ims);

    [Fact]
    public void A_5g_phone_reads_its_bands_and_every_token_is_understood()
    {
        var profile = RadioProfileParser.Parse(Record(GalaxyA54));

        Assert.True(profile.Nr);
        Assert.Equal([1, 3, 5, 7, 8, 28, 38, 40, 41, 66, 77, 78, 79], profile.NrBands);
        Assert.Contains(3, profile.LteBands);
        Assert.Contains(40, profile.LteBands);
        Assert.Contains(32, profile.LteBands); // "Band" in mixed case is the same band
        Assert.Equal([850, 900, 1800, 1900], profile.GsmBands);
        Assert.Equal([1, 2, 4, 5, 8], profile.UmtsBands);
        Assert.Equal(SunsetClass.Lte, profile.Sunset);
        Assert.Equal(Capability.Supported, profile.FiveG);
        Assert.Empty(profile.Unparsed);
    }

    [Fact]
    public void Non_standalone_is_read_from_en_dc_combinations_when_the_options_do_not_say()
    {
        // The A54's record lists only "5G-5Series" (LTE on the 5G core) as its architecture option,
        // and dozens of EN-DC combinations - which are non-standalone 5G by definition.
        var profile = RadioProfileParser.Parse(Record(GalaxyA54));

        Assert.Equal(Capability.Supported, profile.Nsa);
        Assert.Equal(Capability.NotStated, profile.Sa);
        Assert.Equal("5G-5Series", profile.ArchitectureOptions);
    }

    [Fact]
    public void Standalone_is_only_what_the_record_states()
    {
        var stated = RadioProfileParser.Parse(Record(NtnPhone));
        Assert.Equal(Capability.Supported, stated.Sa);
        Assert.Equal(Capability.Supported, stated.Nsa);

        // NR bands alone say nothing about the core network.
        var bandsOnly = RadioProfileParser.Parse(Record("5G NR: n78 | 4G: LTE FDD BAND 3"));
        Assert.True(bandsOnly.Nr);
        Assert.Equal(Capability.NotStated, bandsOnly.Sa);
        Assert.Equal(Capability.NotStated, bandsOnly.Nsa);
    }

    [Fact]
    public void An_architecture_option_without_an_nr_band_is_not_5g()
    {
        // 01453000 is a feature phone. The dashboard's old test - "5G NA" anywhere in the text -
        // counted it, and about a thousand records like it, as 5G-capable.
        var profile = RadioProfileParser.Parse(Record(FeaturePhoneWithOption, "Mobile Phone/Feature phone"));

        Assert.False(profile.Nr);
        Assert.Equal(Capability.Unsupported, profile.FiveG);
        Assert.Equal(Capability.Supported, profile.Lte);
        Assert.Equal("5G-5Series", profile.ArchitectureOptions);
    }

    [Theory]
    [InlineData("2G/3G: GSM 900,GSM 1800 | Radio Interface: NONE")]
    [InlineData("2G/3G: GSM850 (GSM800),GSM 900,GSM 1800,GSM 1900,WCDMA FDD Band 1,WCDMA FDD Band 2,WCDMA FDD Band 5 | Radio Interface: NONE")]
    [InlineData("LPWAN: EC-GSM-IoT | 2G/3G: GSM850 (GSM800),GSM 900,GSM 1800,GSM 1900 | Radio Interface: NONE")]
    [InlineData("Radio Interface: EDGE,GPRS")]
    public void A_device_with_only_2g_and_3g_is_stranded_by_a_sunset(string bands)
    {
        var profile = RadioProfileParser.Parse(Record(bands, "Handheld"));

        Assert.Equal(SunsetClass.LegacyOnly, profile.Sunset);
        Assert.Equal(Capability.Unsupported, profile.Lte);
        Assert.Empty(profile.Unparsed);
    }

    [Fact]
    public void Ec_gsm_iot_is_a_gsm_technology()
    {
        var profile = RadioProfileParser.Parse(Record(
            "LPWAN: EC-GSM-IoT | 2G/3G: GSM 900 | Radio Interface: NONE", "IoT Device"));

        Assert.True(profile.EcGsmIot);
        Assert.True(profile.Gsm);
        Assert.False(profile.NbIot);
        Assert.False(profile.LteM);
        Assert.Equal(SunsetClass.LegacyOnly, profile.Sunset);
    }

    [Fact]
    public void A_module_whose_only_lte_is_nb_iot_or_lte_m_is_its_own_class()
    {
        // Still served after a 2G/3G shutdown - it runs on LTE - and reported on its own row.
        var profile = RadioProfileParser.Parse(Record(LpwanModem, "Modem"));

        Assert.Equal(SunsetClass.LpwanOnly, profile.Sunset);
        Assert.Equal(Capability.Supported, profile.Lte);
        Assert.False(profile.LteBroadband);
        Assert.True(profile.NbIot);
        Assert.True(profile.LteM);
        Assert.Equal([1, 3, 20, 66, 85], profile.NbIotBands);
        Assert.Equal([1, 3, 25], profile.LteMBands);
        Assert.Empty(profile.LteBands);
        Assert.Empty(profile.Unparsed);
    }

    [Fact]
    public void Lte_m_beside_broadband_lte_is_still_broadband()
    {
        var profile = RadioProfileParser.Parse(Record(
            "4G: LTE FDD BAND 2, LTE FDD BAND 4, LTE FDD BAND 12 | 4G Cat: LTE 1 | LPWAN: Cat-NB1,Cat-M1,"
            + "Cat-M-LTEFDD LTE FDD BAND 2 | 2G/3G: WCDMA FDD Band 2,WCDMA FDD Band 4,WCDMA FDD Band 5 | Radio Interface: NONE",
            "Modem"));

        Assert.Equal(SunsetClass.Lte, profile.Sunset);
        Assert.Equal([2, 4, 12], profile.LteBands);
        Assert.Equal([2], profile.LteMBands);
        Assert.Empty(profile.Unparsed);
    }

    [Fact]
    public void Redcap_is_5g_nr()
    {
        var profile = RadioProfileParser.Parse(Record(
            "5G REDCAP: n1, n3, n78 | 4G: LTE FDD BAND 1, LTE TDD BAND 42 | 4G Cat: LTE 4,LTE-DL 13,LTE-UL 16 | Radio Interface: NONE",
            "Modem"));

        Assert.True(profile.Nr);
        Assert.True(profile.NrRedCap);
        Assert.Equal([1, 3, 78], profile.NrBands);
        Assert.Equal(Capability.Supported, profile.FiveG);
        Assert.Equal("IoT / M2M", profile.DeviceClass);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Radio Interface: NONE")]
    public void A_record_that_lists_no_band_says_nothing_rather_than_no(string bands)
    {
        // 13 of 271,830 TACs. Read as "no LTE" they would join the sunset list on no evidence.
        var profile = RadioProfileParser.Parse(Record(bands));

        Assert.False(profile.BandsListed);
        Assert.Equal(SunsetClass.Unknown, profile.Sunset);
        Assert.Equal(Capability.NotStated, profile.Lte);
        Assert.Equal(Capability.NotStated, profile.FiveG);
    }

    [Fact]
    public void Cdma_and_td_scdma_are_2g_3g_and_a_satellite_radio_is_kept_apart_from_ntn()
    {
        var profile = RadioProfileParser.Parse(Record(
            "4G: LTE FDD BAND 1, LTE TDD BAND 38 | 2G/3G: GSM 900,WCDMA FDD Band 1,TD-SCDMA,CDMA2000 | Radio Interface: SATELLITE",
            ntn: "No"));

        Assert.True(profile.Umts);
        Assert.True(profile.Cdma);
        Assert.True(profile.SatelliteListed);
        Assert.Equal(Capability.Unsupported, profile.Ntn);
        Assert.Empty(profile.Unparsed);
    }

    [Fact]
    public void Ntn_reads_its_mode_orbit_and_bands()
    {
        var profile = RadioProfileParser.Parse(Record(NtnPhone, ntn: "3GPP NTN & TN(GSO)", nonremovableEuicc: "1"));

        Assert.Equal(Capability.Supported, profile.Ntn);
        Assert.Equal("NTN and TN", profile.NtnMode);
        Assert.Equal("GSO", profile.NtnOrbits);
        Assert.Equal(["B256"], profile.NtnBands);
        Assert.Empty(profile.Unparsed);
    }

    [Theory]
    [InlineData("Not Known", Capability.NotStated, null, null)]
    [InlineData("No", Capability.Unsupported, null, null)]
    [InlineData("3GPP NTN Only(GSO,NGSO)", Capability.Supported, "NTN only", "GSO,NGSO")]
    [InlineData("3GPP NTN & TN(NGSO)", Capability.Supported, "NTN and TN", "NGSO")]
    [InlineData("", Capability.NotStated, null, null)]
    public void Ntn_connectivity_has_three_states(string value, Capability expected, string? mode, string? orbits)
    {
        var profile = RadioProfileParser.Parse(Record("4G: LTE FDD BAND 3", ntn: value));

        Assert.Equal(expected, profile.Ntn);
        Assert.Equal(mode, profile.NtnMode);
        Assert.Equal(orbits, profile.NtnOrbits);
    }

    [Theory]
    [InlineData("00", "00", Capability.NotStated)]
    [InlineData("0", "0", Capability.Unsupported)]
    [InlineData("0", "00", Capability.NotStated)]
    [InlineData("00", "0", Capability.NotStated)]
    [InlineData("0", "1", Capability.Supported)]
    [InlineData("1", "00", Capability.Supported)]
    [InlineData("2", "0", Capability.Supported)]
    [InlineData("", "", Capability.NotStated)]
    [InlineData("Not Known", "0", Capability.NotStated)]
    public void Esim_counts_00_as_not_stated_and_only_two_zeros_as_no(string removable, string embedded, Capability expected)
    {
        // "00" is the form a record carries when it predates the field: 99,298 TACs have it in all
        // four count fields at once. Until 2026-10-27 the dashboard read it as zero.
        Assert.Equal(expected, RadioProfileParser.ReadEsim(removable, embedded));
    }

    [Fact]
    public void A_token_no_rule_recognises_is_kept_and_not_guessed()
    {
        var profile = RadioProfileParser.Parse(Record("4G: LTE FDD BAND 3, LTE BAND X | Future: thing"));

        Assert.Equal([3], profile.LteBands);
        Assert.Contains("4G: LTE BAND X", profile.Unparsed);
        Assert.Contains("Future: (section)", profile.Unparsed);
    }

    [Theory]
    [InlineData("2G/3G: GSM 900,WCDMA TDD Band A,HSDPA,HSUPA | Radio Interface: NONE", true, true)]
    [InlineData("2G/3G: GSM-R Railway Appl. | Radio Interface: NONE", true, false)]
    [InlineData("Radio Interface: GSM/GPRS/EDGE/UMTS/WCDMA", true, true)]
    [InlineData("Radio Interface: EDGE GPRS GPS HSUPA HSDPA", true, true)]
    [InlineData("2G/3G: GSM 900,TD-SCDMA,CDMA2000 | Radio Interface: Bluetooth", true, true)]
    public void Free_text_radio_words_are_read_and_other_radios_passed_over(string bands, bool gsm, bool umts)
    {
        // The Radio Interface field is a manufacturer's own words, in 419 forms on the 2026-10
        // catalogue; the commonest strays in 2G/3G are lettered UTRA TDD bands and HSPA.
        var profile = RadioProfileParser.Parse(Record(bands, "Handheld"));

        Assert.Equal(gsm, profile.Gsm);
        Assert.Equal(umts, profile.Umts);
        Assert.Equal(SunsetClass.LegacyOnly, profile.Sunset);
        Assert.Empty(profile.Unparsed);
    }

    [Fact]
    public void An_lte_band_in_the_wrong_section_or_free_text_is_still_an_lte_band()
    {
        var profile = RadioProfileParser.Parse(Record("2G/3G: GSM 900,LTE TDD BAND 62 | Radio Interface: LTE FDD: B29"));

        Assert.True(profile.LteBroadband);
        Assert.Equal([29, 62], profile.LteBands);
        Assert.Equal(SunsetClass.Lte, profile.Sunset);
        Assert.Empty(profile.Unparsed);
    }

    [Theory]
    [InlineData("2G/3G: GSM 900,698-2690MHz", "2G/3G: 698-2690MHz")]
    [InlineData("2G/3G: GSM 900,AXGP TDD 41", "2G/3G: AXGP TDD 41")]
    [InlineData("Radio Interface: If you select the others specify the details here", "Radio Interface: If you select the others specify the details here")]
    public void A_frequency_range_or_a_foreign_system_stays_an_ambiguity(string bands, string kept)
    {
        // A range of frequencies names no technology, and AXGP is not a 3GPP system; neither is guessed.
        Assert.Contains(kept, RadioProfileParser.Parse(Record(bands)).Unparsed);
    }

    [Theory]
    [InlineData("Smartphone", "Smartphone")]
    [InlineData("Mobile Phone/Feature phone", "Feature phone")]
    [InlineData("e-Book", "Tablet")]
    [InlineData("Module", "IoT / M2M")]
    [InlineData("Vehicle TCU", "IoT / M2M")]
    [InlineData("WLAN Router", "IoT / M2M")]
    [InlineData("Wearable", "Wearable")]
    [InlineData("Handheld", "Other")]
    [InlineData("Device for the Automatic Processing of Data (APD)", "Other")]
    [InlineData(null, "Other")]
    public void Device_classes_are_the_dashboards(string? deviceType, string expected)
    {
        // The mapping agg_device_class_daily has used since migration 007; it now lives here alone.
        Assert.Equal(expected, RadioProfileParser.DeviceClassOf(deviceType));
    }

    [Fact]
    public void Allocation_date_is_read_and_a_missing_one_is_null()
    {
        Assert.Equal(new DateOnly(2020, 10, 14), RadioProfileParser.Parse(Record("4G: LTE FDD BAND 3")).AllocationDate);
        Assert.Null(RadioProfileParser.Parse(Record("4G: LTE FDD BAND 3", allocated: "Not Known")).AllocationDate);
    }

    [Theory]
    [InlineData("Y", Capability.Supported)]
    [InlineData("No", Capability.Unsupported)]
    [InlineData("Not Known", Capability.NotStated)]
    public void Ims_emergency_keeps_its_three_states(string value, Capability expected)
    {
        Assert.Equal(expected, RadioProfileParser.Parse(Record("4G: LTE FDD BAND 3", ims: value)).ImsEmergency);
    }
}
