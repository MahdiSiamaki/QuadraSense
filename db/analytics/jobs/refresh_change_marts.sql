-- refresh_change_marts.sql
--
-- Time-series marts, built from the dated event log.
--
-- These are what makes a real calendar axis possible. Until the source supplied dates, every
-- chart here would have had to be labelled "delivery sequence"; now each row carries the day
-- the change actually happened.
--
-- Idempotent: each statement drops the month partitions it is about to write. Monthly
-- partitions mean re-importing a month is a drop plus a reload rather than a delete by
-- predicate over a billion rows.

-- --------------------------------------------------------------------------
-- 1. Change counts per day and TAC.
--
--    ~95,000 TACs x 2 labels x 133 days, but sparse in practice - only TACs that actually
--    changed on a day appear. Feeds vendor- and model-level growth.
-- --------------------------------------------------------------------------
TRUNCATE TABLE sqm.agg_change_daily;

INSERT INTO sqm.agg_change_daily (seq, data_date, tac, label, n)
SELECT
    any(seq)  AS seq,
    data_date,
    tac,
    label,
    count()   AS n
FROM sqm.binding_event
GROUP BY data_date, tac, label;

-- --------------------------------------------------------------------------
-- 2. Per-day totals.
--
--    Kept separate from the TAC-level table so the headline series does not have to
--    aggregate ~12M rows to draw 133 points.
--
--    redundant_adds and orphan_removes are NOT computed here. Deriving them needs each
--    binding's previous state, which is a window function over ~1.1B rows - a different
--    order of cost from everything else in this file. They are measured in Phase 0
--    (19.18% and 1.77%) and belong in the ingest pipeline, which already knows the prior
--    state as it folds. Left at zero rather than guessed at.
-- --------------------------------------------------------------------------
TRUNCATE TABLE sqm.agg_change_summary_daily;

INSERT INTO sqm.agg_change_summary_daily
    (seq, data_date, added, removed, redundant_adds, orphan_removes,
     unknown_device_rows, rows_total)
SELECT
    any(seq)                        AS seq,
    data_date,
    countIf(label = 'add')          AS added,
    countIf(label = 'remove')       AS removed,
    0                               AS redundant_adds,
    0                               AS orphan_removes,
    countIf(imei = '000000')        AS unknown_device_rows,
    count()                         AS rows_total
FROM sqm.binding_event
GROUP BY data_date;

-- --------------------------------------------------------------------------
-- 3. SIM changes per day.
--
--    A SIM change is a subscriber whose number moved to a different IMSI: on the same day,
--    the number has a remove carrying one IMSI and an add carrying another.
--
--    This is deliberately a same-day definition. A swap that spans midnight will be missed,
--    and one where the old SIM is removed days later will not be counted on either day.
--    Detecting those needs per-subscriber state across days; this is the cheap, defensible
--    version, and the UI says which definition it uses.
-- --------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sqm.agg_sim_change_daily
(
    data_date       Date,
    msisdn_changed  UInt64
)
ENGINE = ReplacingMergeTree
PARTITION BY toYYYYMM(data_date)
ORDER BY data_date;

TRUNCATE TABLE sqm.agg_sim_change_daily;

INSERT INTO sqm.agg_sim_change_daily (data_date, msisdn_changed)
SELECT data_date, count() AS msisdn_changed
FROM (
    SELECT
        data_date,
        msisdn,
        groupUniqArrayIf(imsi, label = 'add')    AS added_sims,
        groupUniqArrayIf(imsi, label = 'remove') AS removed_sims
    FROM sqm.binding_event
    GROUP BY data_date, msisdn
    -- Both directions present, and at least one added SIM that was not among those removed.
    HAVING length(added_sims) > 0
       AND length(removed_sims) > 0
       AND length(arrayFilter(x -> NOT has(removed_sims, x), added_sims)) > 0
)
GROUP BY data_date;

-- --------------------------------------------------------------------------
-- 4. Device changes per day.
--
--    Same shape, but for handsets: the number stayed on its SIM and moved to a different
--    device. Measured over the whole window in Phase 0 at 51.3% of subscribers, so this is
--    the single largest churn signal in the dataset.
-- --------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sqm.agg_device_change_daily
(
    data_date       Date,
    msisdn_changed  UInt64
)
ENGINE = ReplacingMergeTree
PARTITION BY toYYYYMM(data_date)
ORDER BY data_date;

TRUNCATE TABLE sqm.agg_device_change_daily;

INSERT INTO sqm.agg_device_change_daily (data_date, msisdn_changed)
SELECT data_date, count() AS msisdn_changed
FROM (
    SELECT
        data_date,
        msisdn,
        groupUniqArrayIf(imei, label = 'add')    AS added_devices,
        groupUniqArrayIf(imei, label = 'remove') AS removed_devices
    FROM sqm.binding_event
    GROUP BY data_date, msisdn
    HAVING length(added_devices) > 0
       AND length(removed_devices) > 0
       AND length(arrayFilter(x -> NOT has(removed_devices, x), added_devices)) > 0
)
GROUP BY data_date;
