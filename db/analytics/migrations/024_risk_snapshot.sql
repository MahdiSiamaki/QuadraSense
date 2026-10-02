-- 024_risk_snapshot.sql
--
-- Phase 4's windowed and all-time measures, per SIM and per IMEI, as of the latest data day:
-- what the Shared IMEI, High Device Count, Rapid Device Change and IMEI Randomisation lists read.
--
-- WHY PRECOMPUTED. One 30-day pass over the history's SIM copy took 190-255 s at 2 threads
-- (measured 2026-10-02, eight key-range chunks of ~46M rows), against the Explorer's 30 s budget.
-- So the worker builds these when it is idle, and the API only filters, sorts and judges.
--
-- MEASURES, NOT VERDICTS. Counts are stored; thresholds are applied when read (Sqm.Domain.Risk.
-- RiskRules), so a threshold change needs no recompute. Only rows whose largest measure reaches a
-- storage floor are kept; a threshold below the floor is refused rather than answered wrongly.
--
-- CLEAN AND RAW. "Countable" IMEIs only - 14 digits, not fourteen zeros, not the shifted shape
-- (the feed-quality test, unchanged) - and adds on a (day, SIM) the feed-quality monitor listed as
-- multi-number are set aside. The raw counts sit beside the clean ones, so the set-aside is visible.
--
-- RUNS. A run is built chunk by chunk, one chunk per idle moment of the worker, each chunk its own
-- (run_id, chunk) partition written DROP-then-INSERT. A run is published only when every planned
-- chunk is written, no key appears twice, and the inputs it was built from have not changed;
-- readers read the newest published run. A late or corrected day, a month rebuild or a GSMA
-- activation changes the inputs and so makes the published run stale - labelled, never silently
-- wrong - until the next one is published.

CREATE TABLE IF NOT EXISTS sqm.risk_run
(
    run_id          UInt64,          -- unix milliseconds when the run was planned
    as_of           Date,            -- the data-through day the windows end on
    fingerprint     UInt64,          -- the inputs the run was planned from
    tac_version_id  UInt32,
    state           Enum8('running' = 1, 'published' = 2, 'abandoned' = 3, 'failed' = 4),
    chunks          UInt16,          -- per table
    sim_cuts        Array(UInt64),   -- key-range cut points, fixed for the run
    imei_cuts       Array(String),
    done            Array(String),   -- '<table>:<chunk>' written so far
    note            String,
    updated_at      DateTime64(3)
)
ENGINE = ReplacingMergeTree(updated_at)
ORDER BY run_id;

-- Per SIM: distinct countable IMEIs it was added to, in the 30, 20 and 7 days to as_of.
CREATE TABLE IF NOT EXISTS sqm.risk_sim_window
(
    run_id                UInt64,
    chunk                 UInt16,
    imsi                  UInt64,
    imeis_30              UInt32,
    imeis_30_raw          UInt32,
    imeis_20              UInt32,
    tacs_20               UInt32,
    imeis_7               UInt32,
    imeis_7_raw           UInt32,
    adds_30               UInt32,   -- every add in the 30 days, before the screens
    adds_set_aside_30     UInt32,   -- of those, set aside as feed defects
    add_days_30           UInt16,   -- days with a clean add
    max_imeis_one_day_30  UInt32,
    max_day_30            Date,
    top_tacs_20           Array(String)
)
ENGINE = MergeTree
PARTITION BY (run_id, chunk)
ORDER BY imsi;

-- Per IMEI, windowed: distinct SIMs and numbers added to it in the 30 and 7 days to as_of.
CREATE TABLE IF NOT EXISTS sqm.risk_imei_window
(
    run_id                UInt64,
    chunk                 UInt16,
    imei                  String,
    sims_30               UInt32,
    sims_30_raw           UInt32,
    sims_7                UInt32,
    numbers_30            UInt32,
    adds_30               UInt32,
    adds_set_aside_30     UInt32,
    add_days_30           UInt16,
    max_sims_one_day_30   UInt32,
    max_day_30            Date
)
ENGINE = MergeTree
PARTITION BY (run_id, chunk)
ORDER BY imei;

-- Per IMEI, all time, from current state: SIMs and numbers ever, and SIMs the feed has not removed -
-- dated, or known only from the initial dump. "Not removed" is not presence (spec section 21).
CREATE TABLE IF NOT EXISTS sqm.risk_imei_lifetime
(
    run_id                  UInt64,
    chunk                   UInt16,
    imei                    String,
    sims_ever               UInt32,
    numbers_ever            UInt32,
    bindings                UInt32,
    sims_not_removed_dated  UInt32,
    sims_not_removed_dump   UInt32
)
ENGINE = MergeTree
PARTITION BY (run_id, chunk)
ORDER BY imei;
