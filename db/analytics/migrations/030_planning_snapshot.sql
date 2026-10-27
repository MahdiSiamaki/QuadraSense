-- 030_planning_snapshot.sql
--
-- Network planning's per-number and per-device measures, as of one delivery: what the 2G/3G sunset
-- (N1), eSIM (N3) and IoT fleet (N5) views read. docs/architecture/19-network-planning.md.
--
-- WHY PRECOMPUTED. "Numbers every one of whose active handsets lacks LTE" is a question about each
-- number's whole set of handsets, so it cannot be answered from the per-model marts: a number with a
-- feature phone and a smartphone is in neither model's figures as "exposed". It needs every active
-- binding grouped by number - binding_current FINAL, ~120M bindings, which took 100 s and more to
-- read on 2026-10-19 - so the worker builds it when it is idle, one range of numbers at a time, and
-- the API only reads.
--
-- AS OF A DELIVERY. A run is built from current state while it is still the state of the latest
-- delivery, and abandoned if another delivery is folded before it is published - the rule
-- refresh_marts.sql follows, for the reason it gives: the state of an earlier delivery cannot be
-- recovered afterwards. So per-number trends begin with the first run (the product owner's
-- decision, 2026-10-27), and each published run leaves its finished figures in planning_summary.
--
-- COUNTED EXACTLY where the unit allows. A number is in exactly one chunk, so numbers and bindings
-- per class are exact counts. Handsets are distinct IMEIs across numbers and chunks - an IMEI can sit
-- on several numbers - so they are uniq() states, ~0.5% error, labelled estimated, as everywhere.

CREATE TABLE IF NOT EXISTS sqm.planning_run
(
    run_id          UInt64,          -- unix milliseconds when the run was planned
    as_of_seq       UInt16,          -- the delivery whose state it describes
    as_of_date      Nullable(Date),  -- that delivery's business date; NULL for the initial dump
    fingerprint     UInt64,          -- the inputs the run was planned from
    tac_version_id  UInt32,
    parser_version  UInt16,          -- RadioProfileParser.Version the profiles were parsed by
    state           Enum8('running' = 1, 'published' = 2, 'abandoned' = 3, 'failed' = 4),
    chunks          UInt16,          -- per table
    msisdn_cuts     Array(UInt64),   -- key-range cut points, fixed for the run
    imei_cuts       Array(String),
    done            Array(String),   -- '<table>:<chunk>' written so far
    note            String,
    updated_at      DateTime64(3)
)
ENGINE = ReplacingMergeTree(updated_at)
ORDER BY run_id;

-- One row per number with at least one active binding.
--
--   sunset   1 LTE or 5G on some handset - ready
--            2 no LTE, but LTE-M or NB-IoT on some handset - ready, reported apart
--            3 every handset 2G/3G only, every one known - exposed to a sunset
--            4 no LTE known, and at least one handset that cannot be assessed
--   reason   why a number is 4, the first that applies: 1 a shifted IMEI (the feed defect of
--            2026-09-15, docs/discovery/05-…), 2 an unknown device (000000 or not 14 digits),
--            3 a TAC the catalogue does not hold, 4 a model whose record lists no band. 0 otherwise.
--   esim     1 some handset is eSIM-capable; 0 every handset is known not to be; 2 otherwise
--
-- ORDER BY (sunset, msisdn): a list of one class is a range, paged by number.
CREATE TABLE IF NOT EXISTS sqm.planning_number
(
    run_id          UInt64,
    chunk           UInt16,
    msisdn          UInt64,
    sunset          UInt8,
    reason          UInt8,
    esim            UInt8,
    iot             UInt8,           -- 1 when some handset is an IoT / M2M device
    gsm_only        UInt8,           -- 1 for a class-3 number whose every handset is GSM only (no 3G)
    handsets        UInt16,          -- distinct IMEIs, unknown devices included
    bindings        UInt16,
    paired          UInt8,           -- handsets whose class was taken from their dual-SIM partner
    tacs            Array(String)    -- the models it holds; empty for class 1
)
ENGINE = MergeTree
PARTITION BY (run_id, chunk)
ORDER BY (sunset, msisdn);

-- Per number class and model: who holds it. A number with two models is counted under each - the
-- figure is "numbers holding this model", and a vendor's figure is a merge of its models' states.
CREATE TABLE IF NOT EXISTS sqm.planning_breakdown
(
    run_id          UInt64,
    chunk           UInt16,
    sunset          UInt8,
    reason          UInt8,
    esim            UInt8,
    tac             String,          -- '' for an unknown device
    numbers         AggregateFunction(uniq, UInt64),
    bindings        UInt64,
    handsets        AggregateFunction(uniq, String)
)
ENGINE = MergeTree
PARTITION BY (run_id, chunk)
ORDER BY (sunset, reason, esim, tac);

-- Per IoT / M2M device (the dashboard's device class), all time, from the binding history: how many
-- SIMs and numbers it has carried - the rotation of SIMs through a fleet.
CREATE TABLE IF NOT EXISTS sqm.planning_iot_device
(
    run_id          UInt64,
    chunk           UInt16,
    imei            String,
    tac             String,
    sims_ever       UInt32,
    numbers_ever    UInt32,
    sims_active     UInt32,
    numbers_active  UInt32,
    sims_90         UInt32,          -- SIMs whose binding on it began in the 90 days to the delivery
    first_date      Date,            -- 2025-12-27 for a device the initial dump already held
    in_dump         UInt8
)
ENGINE = MergeTree
PARTITION BY (run_id, chunk)
ORDER BY (tac, imei);

-- Finished figures of every published run, kept when the run's rows are dropped: the trend.
CREATE TABLE IF NOT EXISTS sqm.planning_summary
(
    run_id          UInt64,
    as_of_seq       UInt16,
    as_of_date      Nullable(Date),
    measure         LowCardinality(String),   -- 'sunset' | 'esim'
    class           UInt8,
    reason          UInt8,
    numbers         UInt64,
    bindings        UInt64,
    handsets        UInt64                     -- estimated
)
ENGINE = ReplacingMergeTree
ORDER BY (run_id, measure, class, reason);
