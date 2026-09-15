-- 019_device_module.sql
--
-- The Devices module: a table ordered by IMEI, and a per-model rollup mart.
--
-- ===========================================================================================
-- WHAT A DEVICE IS HERE, because the whole design follows from it.
--
--   Device  = TAC.  An 8-digit Type Allocation Code. This is what the GSMA record describes -
--             manufacturer, brand, model, marketing name, device type, OS, OEM, bands - and it
--             is a MODEL, not a thing you can hold.
--   Handset = IMEI. One physical unit. Its first 8 digits ARE its TAC.
--   Binding = (msisdn, imsi, imei). A handset reaches SIMs and numbers only through these.
--
-- So Device -> Handset is not a join, it is a PREFIX. Measured over all 295,013,916 rows of
-- binding_current:
--
--   rows                     295,013,916
--   14-digit IMEIs           284,341,927
--   malformed IMEIs           10,671,989   (3.6%, tac = '')
--   substring(imei,1,8) != tac         0   <- exact, on every row
--   distinct IMEIs           120,355,763
--   distinct TACs               168,198
--
-- That zero is the licence for everything below: a table ordered by IMEI puts every handset of a
-- device model in one contiguous range, so "this model's identifiers" is a primary-index seek
-- rather than a scan.
-- ===========================================================================================

-- ===========================================================================================
-- 1. sqm.binding_by_imei - current state, ordered by IMEI.
--
-- THE PROBLEM. binding_current is ORDER BY (msisdn, imsi, imei). A filter on imei - the third
-- key column - prunes nothing, and the same is true of tac, which is derived from it. Measured,
-- warm, on 25.8:
--
--   exact IMEI, 4 samples     1,861 / 1,966 / 1,999 / 2,085 ms   295,013,916 rows read, each
--   one TAC,    3 samples     6,488 / 6,803 / 8,906 ms           295,013,916 rows read, each
--
-- Seven of seven read the entire table. ADR-008 records how a single lucky sample nearly ended
-- that investigation early; this one was measured with enough samples to be sure.
--
-- WHY NOT THE CHEAPER OPTIONS. A bloom_filter skip index on tac was rejected without building
-- it, and the arithmetic is why: the table is ordered by MSISDN, so a TAC's rows are scattered
-- uniformly. The most populous TAC holds 296,686 of 295M rows - 0.1% - and at 8,192 rows per
-- granule the chance a given granule contains none of them is 0.999^8192 = 0.03%. The filter
-- would select essentially every granule and cost 100+ MiB to do it. A projection was rejected
-- for the reason ADR-008 gives: ClickHouse does not use projections with FINAL, and every read
-- of current state needs FINAL.
--
-- ORDER BY (imei, msisdn, imsi) rather than (tac, imei, ...): tac is a prefix of imei, so one
-- key serves both - an exact IMEI is a point seek, a TAC is the range ['<tac>000000',
-- '<tac>999999'], and an IMEI prefix is a narrower range inside it. A leading tac column would
-- add eight bytes per row to say something the next column already says.
--
-- Cost: ~6.5 GiB, against 905 GiB free.
-- ===========================================================================================
CREATE TABLE IF NOT EXISTS sqm.binding_by_imei
(
    imei             String,
    msisdn           UInt64,
    imsi             UInt64,
    tac              String MATERIALIZED if(length(imei) = 14, substring(imei, 1, 8), ''),
    active           UInt8,
    last_change_seq  UInt16,
    last_change_date Nullable(Date)
)
ENGINE = ReplacingMergeTree(last_change_seq)
ORDER BY (imei, msisdn, imsi)
SETTINGS index_granularity = 8192;

-- ---------------------------------------------------------------------------
-- Kept in step by a materialized view, exactly as binding_by_imsi is.
--
-- THE ONE WAY THIS BREAKS, stated so it is not discovered: a materialized view fires on INSERT
-- and on nothing else. Rebuilding binding_current by EXCHANGE TABLES bypasses it completely and
-- leaves this table describing the old data. Any such rebuild must be followed by
-- `Sqm.Ingestion --backfill-imei`, and the backfill's reconciliation is what proves it was.
-- ---------------------------------------------------------------------------
CREATE MATERIALIZED VIEW IF NOT EXISTS sqm.mv_binding_by_imei TO sqm.binding_by_imei AS
SELECT imei, msisdn, imsi, active, last_change_seq, last_change_date
FROM sqm.binding_current;

-- ===========================================================================================
-- 2. sqm.agg_device_model - one row per device model per delivery.
--
-- The Devices list sorts, filters and pages over ~98,000 device models. Doing that from
-- binding_current would be a 2.35 s aggregate per keystroke; doing it from here is a scan of a
-- table small enough to fit in memory several times over.
--
-- Measured: the whole rollup over 295,013,916 rows with FINAL costs 2.35 s and produces 97,903
-- models totalling 114,237,785 active bindings - which is the network total the dashboard shows,
-- to the row. That is cheap enough to rebuild with the other marts on every delivery, so it is
-- built there rather than kept fresh by a second mechanism.
--
-- WHY THE DISTINCT COUNTS ARE uniq() AND NOT uniqExact. Three distinct counts across ~98,000
-- groups over 295M rows is the expensive shape, and uniqExact measured at ~52 s for a single one
-- of them elsewhere in this script. uniq() is HyperLogLog at ~0.5% error, which is invisible on
-- "1,247,392 handsets".
--
-- It is uniq() specifically, and not some better estimator, because THIS TABLE FEEDS THE
-- DASHBOARD'S TAC BREAKDOWN. refresh_marts.sql section 6 now derives agg_dimension_daily's 'tac'
-- slice from here rather than aggregating the 295M rows a second time - so a different estimator
-- here would silently move numbers that are already on screen. Two estimates that disagree about
-- one population are worse than either of them.
--
-- PARTITION BY seq, and the refresh drops the partition before inserting, so a re-run replaces a
-- delivery instead of doubling it. That rule is not decoration: an insert-only mart refresh once
-- doubled agg_device_daily to 251,879,046 against a real 125,939,523, silently.
-- ===========================================================================================
CREATE TABLE IF NOT EXISTS sqm.agg_device_model
(
    seq          UInt16,
    data_date    Nullable(Date),
    tac          String,
    -- Active bindings: number + SIM + handset triples currently in force for this model.
    bindings     UInt64,
    -- Distinct physical handsets. Lower than bindings whenever a handset carries more than one
    -- SIM, which is common: the most populous model shows 296,686 bindings over 208,895 handsets.
    handsets     UInt64,
    sims         UInt64,
    subscribers  UInt64,
    -- Earliest and latest day a daily file said anything about a binding of this model. NULL
    -- means no daily file ever has - the model is present only because the initial dump listed
    -- it, which is true of a large share of the tail.
    first_seen   Nullable(Date),
    last_seen    Nullable(Date)
)
ENGINE = ReplacingMergeTree
PARTITION BY seq
ORDER BY (seq, tac);
