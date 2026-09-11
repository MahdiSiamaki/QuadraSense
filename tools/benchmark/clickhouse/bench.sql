-- ClickHouse benchmark: SQM device-SIM binding workload
-- Loaded from the original source CSVs, same files PostgreSQL gets.

DROP DATABASE IF EXISTS sqm;
CREATE DATABASE sqm;

-- ---------------------------------------------------------------- TAC dimension
CREATE TABLE sqm.tac
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
    authIMS         String,
    unauthIMS       String,
    imsNoUICC       String,
    removableUICC   String,
    removableEUICC  String,
    nonRemUICC      String,
    nonRemEUICC     String,
    netSpecificId   String,
    ntnConnectivity String,
    simSlot         String,
    imeiQuantity    String,
    operatingSystem String,
    oem             String,
    bandDetails     String
)
ENGINE = MergeTree ORDER BY tac;

-- ---------------------------------------------------- Current state of bindings
-- Grain: one row per (msisdn, imsi, imei) binding.
CREATE TABLE sqm.binding_current
(
    msisdn  UInt64,
    imsi    UInt64,
    imei    String,
    tac     String MATERIALIZED if(length(imei) = 14, substring(imei, 1, 8), ''),
    active  UInt8 DEFAULT 1
)
ENGINE = ReplacingMergeTree
ORDER BY (msisdn, imsi, imei);

-- ------------------------------------------------------- Full event history
CREATE TABLE sqm.binding_event
(
    day_seq UInt16,
    msisdn  UInt64,
    imsi    UInt64,
    imei    String,
    tac     String MATERIALIZED if(length(imei) = 14, substring(imei, 1, 8), ''),
    label   Enum8('add' = 1, 'remove' = 2)
)
ENGINE = MergeTree
PARTITION BY intDiv(day_seq, 32)
ORDER BY (msisdn, imsi, imei, day_seq);
