-- 029_tac_radio.sql
--
-- A device model's radios, bands and SIM capability, parsed once per GSMA version.
--
-- WHY. The capability flags (migration 011) were three searches of the raw text: "LTE" anywhere,
-- "5G NR" or "5G NA" anywhere, and the eUICC counts read with toUInt8OrZero. Network planning needs
-- what they could not give - which bands, which radio generations, standalone or not - and the
-- searches were wrong in two places that matter, measured on the 2026-10 catalogue (271,830 TACs):
--
--   * eSIM. The GSMA count fields carry '00' where a record predates the field: 99,298 TACs have
--     it in eUICC, SIM slots and IMEI count at once, and up to 2018 nearly every allocation does.
--     toUInt8OrZero('00') is 0, so every older handset was "known to have no eSIM".
--   * 5G. "5G NA Options" lists 3GPP architecture options, not bands. About a thousand records list
--     an option and no NR band - feature phones among them - and were counted as 5G.
--
-- The product owner chose (2026-10-27) to correct both everywhere: '00' is "not stated", and 5G
-- needs an NR band.
--
-- WHERE THE PARSING HAPPENS. In C#, Sqm.Domain.Devices.RadioProfileParser, not in SQL: the field
-- has a grammar (sections separated by " | ", each "label: values") that is far clearer as code,
-- and it is unit-tested there against real records. The parse runs once per version - when a TAC
-- file is loaded, and before a dashboard rebuild if the active version has not been parsed by the
-- current parser - and is 271,830 rows, seconds.
--
-- Capability columns are 1 supported, 0 not, 2 not stated - the encoding tac_capability.ims_emergency
-- has used since migration 011.

CREATE TABLE IF NOT EXISTS sqm.tac_radio
(
    version_id            UInt32,
    tac                   String,
    parser_version        UInt16,
    device_class          LowCardinality(String),   -- the dashboard's device mix classes

    bands_listed          UInt8,      -- 0: bandDetails names no radio at all - every answer below is "not stated"
    gsm                   UInt8,      -- includes GPRS/EDGE interfaces and EC-GSM-IoT
    umts                  UInt8,      -- WCDMA, TD-SCDMA
    cdma                  UInt8,      -- CDMA2000 / 3GPP2
    lte_broadband         UInt8,      -- a 4G band or an LTE category
    lte_m                 UInt8,
    nb_iot                UInt8,
    ec_gsm_iot            UInt8,
    nr                    UInt8,      -- an NR band, RedCap included
    nr_redcap             UInt8,

    lte                   UInt8,      -- capability: any LTE, LTE-M and NB-IoT included
    five_g                UInt8,      -- capability: an NR band
    -- What survives a 2G/3G shutdown: 0 unknown (no bands listed), 1 LTE or NR, 2 LTE-M / NB-IoT
    -- only, 3 only 2G/3G. Sqm.Domain.Devices.SunsetClass.
    sunset                UInt8,

    -- Standalone / non-standalone 5G, only as the record states it: 1 stated, 2 not stated.
    sa                    UInt8,
    nsa                   UInt8,
    architecture_options  String,     -- the "5G NA Options" exactly as listed

    lte_bands             Array(UInt16),
    nr_bands              Array(UInt16),
    lte_m_bands           Array(UInt16),
    nb_iot_bands          Array(UInt16),
    umts_bands            Array(UInt16),
    gsm_bands             Array(UInt16),   -- MHz

    esim                  UInt8,      -- capability, from the two eUICC counts
    ntn                   UInt8,      -- capability, from ntnConnectivity
    ntn_mode              LowCardinality(String),   -- 'NTN only' | 'NTN and TN' | ''
    ntn_orbits            LowCardinality(String),   -- 'GSO' | 'NGSO' | 'GSO,NGSO' | ''
    ntn_bands             Array(String),            -- 'B255', 'n256'
    satellite_listed      UInt8,      -- a SATELLITE band section or interface: not the same claim as 3GPP NTN
    ims_emergency         UInt8,      -- capability; emergency calls over IMS, which is not VoLTE

    allocation_date       Nullable(Date),   -- the GSMA allocation, not a release or manufacture date
    unparsed              Array(String)     -- tokens no rule recognised, "section: token"
)
ENGINE = MergeTree
PARTITION BY version_id
ORDER BY (version_id, tac);

-- The parse writes here first and the version's partition is then swapped in whole
-- (REPLACE PARTITION), so a reader sees the previous parse or the new one, never a half.
CREATE TABLE IF NOT EXISTS sqm.tac_radio_staging AS sqm.tac_radio;

-- Which versions are parsed, by which parser. A version is current when its newest row names the
-- parser version the code carries (RadioProfileParser.Version).
CREATE TABLE IF NOT EXISTS sqm.tac_radio_parsed
(
    version_id      UInt32,
    parser_version  UInt16,
    rows            UInt64,
    unparsed_tacs   UInt64,     -- TACs with at least one token no rule recognised
    parsed_at       DateTime64(3, 'UTC')
)
ENGINE = ReplacingMergeTree(parsed_at)
ORDER BY version_id;

-- The active version's profiles, as sqm.tac is the active version's records.
CREATE VIEW IF NOT EXISTS sqm.tac_radio_active AS
SELECT *
FROM sqm.tac_radio
WHERE version_id = (SELECT version_id FROM sqm.tac_active FINAL ORDER BY activated_at DESC LIMIT 1);
