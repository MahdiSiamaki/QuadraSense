-- 001_initial_schema.sql
-- Analytics store (ClickHouse). Forward-only; never edited after it has been applied anywhere.
--
-- Column types follow the discovery findings exactly:
--   msisdn / imsi  -> UInt64  (always numeric, no significant leading zeros)
--   imei / tac     -> String  (MUST be text: '000000' and '00100100' carry significant leading zeros)

CREATE DATABASE IF NOT EXISTS sqm;

-- ---------------------------------------------------------------------------
-- TAC dimension, versioned.
-- 270,166 rows per version, zero duplicate TACs. Small enough that keeping every
-- version costs almost nothing and makes an old report reproducible.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sqm.tac_version
(
    version_id      UInt32,
    file_name       String,
    file_sha256     FixedString(64),
    file_bytes      UInt64,
    row_count       UInt32,
    uploaded_at     DateTime64(3, 'UTC'),
    uploaded_by     String,
    tacs_added      UInt32,
    tacs_changed    UInt32,
    tacs_removed    UInt32,
    is_current      UInt8 DEFAULT 0
)
ENGINE = ReplacingMergeTree(uploaded_at)
ORDER BY version_id;

CREATE TABLE IF NOT EXISTS sqm.tac
(
    tac             String,
    manufacturer    String,
    modelName       String,
    marketingName   String,
    brandName       String,
    allocationDate  String,
    lastUpdatedDate String,
    organisationId  String,
    deviceType      String,
    bluetooth       String,
    nfc             String,
    wlan            String,
    c13             String,
    c14             String,
    c15             String,
    c16             String,
    c17             String,
    c18             String,
    c19             String,
    c20             String,
    c21             String,
    c22             String,
    c23             String,
    operatingSystem String,
    oem             String,
    bandDetails     String
)
ENGINE = MergeTree
ORDER BY tac;

-- ---------------------------------------------------------------------------
-- Curated vendor normalisation.
-- Data, not code: the raw GSMA manufacturer field holds 10,529 distinct values
-- including 6 spellings of Samsung and 7 of Motorola. Hardcoding a mapping would
-- mean a deployment every time a new string appears, and with a tail that long
-- that would be constant.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sqm.tac_vendor_map
(
    raw_manufacturer String,
    vendor_canonical String,
    vendor_group     String,
    updated_at       DateTime64(3, 'UTC') DEFAULT now64(3),
    updated_by       String DEFAULT ''
)
ENGINE = ReplacingMergeTree(updated_at)
ORDER BY raw_manufacturer;

-- ---------------------------------------------------------------------------
-- Immutable event log — the system of record.
-- Everything else is rebuildable from this plus the source files, which is what
-- makes reprocessing safe.
-- ~803.5M rows today, growing ~3.0B/year.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sqm.binding_event
(
    seq       UInt16,
    batch_id  UUID,
    msisdn    UInt64,
    imsi      UInt64,
    imei      String,
    tac       String MATERIALIZED if(length(imei) = 14, substring(imei, 1, 8), ''),
    label     Enum8('add' = 1, 'remove' = 2)
)
ENGINE = MergeTree
PARTITION BY intDiv(seq, 32)
-- Ordered by subscriber first so per-MSISDN history is a primary-index seek,
-- not a scan. Measured equivalent on binding_current: 12 ms reading 278K of 126M rows.
ORDER BY (msisdn, imsi, imei, seq);

-- ---------------------------------------------------------------------------
-- Current state — derived, never hand-edited.
-- ~108M active of ~227M total distinct bindings. Inactive rows are retained
-- because churn analysis is a confirmed requirement.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sqm.binding_current
(
    msisdn          UInt64,
    imsi            UInt64,
    imei            String,
    tac             String MATERIALIZED if(length(imei) = 14, substring(imei, 1, 8), ''),
    active          UInt8 DEFAULT 1,
    first_seen_seq  UInt16 DEFAULT 0,
    last_change_seq UInt16 DEFAULT 0,
    version         UInt32 DEFAULT 0
)
ENGINE = ReplacingMergeTree(version)
ORDER BY (msisdn, imsi, imei);

-- ---------------------------------------------------------------------------
-- Marts. Dashboards read these, never the tables above.
--
-- The benchmark showed that even ClickHouse needs 3.5 s for the top-manufacturer
-- aggregate and 52 s for the churn distribution over raw data, against a 500 ms
-- budget. Pre-aggregation is therefore load-bearing, not an optimisation.
-- ---------------------------------------------------------------------------

-- ~95,000 rows/day: one per (sequence, TAC, active flag).
CREATE TABLE IF NOT EXISTS sqm.agg_device_daily
(
    seq        UInt16,
    data_date  Nullable(Date),
    tac        String,
    active     UInt8,
    n          UInt64
)
ENGINE = SummingMergeTree(n)
ORDER BY (seq, tac, active);

-- Change counts per delivery. Feeds every growth / churn / net-change widget.
CREATE TABLE IF NOT EXISTS sqm.agg_change_daily
(
    seq        UInt16,
    data_date  Nullable(Date),
    tac        String,
    label      Enum8('add' = 1, 'remove' = 2),
    n          UInt64
)
ENGINE = SummingMergeTree(n)
ORDER BY (seq, tac, label);

-- Data-quality counters per delivery. Small, and the basis of every baseline alert.
CREATE TABLE IF NOT EXISTS sqm.agg_quality_daily
(
    seq        UInt16,
    data_date  Nullable(Date),
    rule       String,
    n          UInt64
)
ENGINE = SummingMergeTree(n)
ORDER BY (seq, rule);
