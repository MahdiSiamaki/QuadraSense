-- PostgreSQL benchmark: SQM device-SIM binding workload
-- Same data, same source CSVs, same column types as the ClickHouse run.

DROP SCHEMA IF EXISTS sqm CASCADE;
CREATE SCHEMA sqm;

-- ---------------------------------------------------------------- TAC dimension
CREATE TABLE sqm.tac (
    tac             text PRIMARY KEY,
    manufacturer    text,
    "modelName"     text,
    "marketingName" text,
    "brandName"     text,
    "allocationDate"  text,
    "lastUpdatedDate" text,
    "organisationId"  text,
    "deviceType"    text,
    bluetooth       text,
    nfc             text,
    wlan            text,
    auth_ims        text,
    unauth_ims      text,
    ims_no_uicc     text,
    removable_uicc  text,
    removable_euicc text,
    nonrem_uicc     text,
    nonrem_euicc    text,
    net_specific_id text,
    ntn             text,
    sim_slot        text,
    imei_quantity   text,
    "operatingSystem" text,
    oem             text,
    "bandDetails"   text
);

-- ---------------------------------------------------- Current state of bindings
CREATE TABLE sqm.binding_current (
    msisdn  bigint  NOT NULL,
    imsi    bigint  NOT NULL,
    imei    text    NOT NULL,
    tac     text GENERATED ALWAYS AS
              (CASE WHEN length(imei) = 14 THEN substr(imei, 1, 8) END) STORED,
    active  smallint NOT NULL DEFAULT 1
);

-- ------------------------------------------------------- Full event history
CREATE TABLE sqm.binding_event (
    day_seq smallint NOT NULL,
    msisdn  bigint   NOT NULL,
    imsi    bigint   NOT NULL,
    imei    text     NOT NULL,
    label   smallint NOT NULL    -- 1 = add, 2 = remove
);

-- Staging table for the raw CSV (column order matches the source file)
CREATE UNLOGGED TABLE sqm.stage_base (
    imei   text,
    imsi   text,
    msisdn text
);

CREATE UNLOGGED TABLE sqm.stage_delta (
    msisdn text,
    imsi   text,
    imei   text,
    label  text
);
