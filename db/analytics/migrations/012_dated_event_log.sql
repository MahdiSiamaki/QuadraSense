-- 012_dated_event_log.sql
--
-- Real dates, now that the source supplies them (see docs/discovery/03-dated-daily-files.md).
--
-- Two changes:
--
-- 1. binding_event gains `data_date` and is partitioned by month rather than by a bucket of
--    the sequence number. Monthly partitions match how the data actually arrives and how it
--    will be retained or reprocessed: re-importing a month becomes DROP PARTITION plus a
--    reload, instead of a delete by predicate.
--
-- 2. binding_current gains an explicit version column. It was created during benchmarking as a
--    plain ReplacingMergeTree with NO version, which keeps an arbitrary row per key on merge.
--    That is fine for a table loaded once, and silently wrong for a fold: applying 133 days of
--    events depends entirely on the latest event winning. `last_change_seq` makes that explicit
--    and deterministic.
--
-- Both are derived data, rebuildable from the source files, so recreating them costs only the
-- reload.

DROP TABLE IF EXISTS sqm.binding_event;
CREATE TABLE sqm.binding_event
(
    seq        UInt16,
    data_date  Date,
    msisdn     UInt64,
    imsi       UInt64,
    imei       String,
    tac        String MATERIALIZED if(length(imei) = 14, substring(imei, 1, 8), ''),
    label      Enum8('add' = 1, 'remove' = 2)
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(data_date)
-- Ordered by subscriber first so per-MSISDN history is a primary-index seek, not a scan.
ORDER BY (msisdn, imsi, imei, seq);

DROP TABLE IF EXISTS sqm.binding_current;
CREATE TABLE sqm.binding_current
(
    msisdn          UInt64,
    imsi            UInt64,
    imei            String,
    tac             String MATERIALIZED if(length(imei) = 14, substring(imei, 1, 8), ''),
    active          UInt8,
    -- Sequence of the event that last set `active`. 0 means the initial dump.
    last_change_seq UInt16,
    -- Date of that event. NULL for rows that came from the initial dump, which covers a
    -- month-long window rather than a single day.
    last_change_date Nullable(Date)
)
-- The version argument is what makes the fold correct: on merge, the row with the highest
-- last_change_seq wins, so the most recent event always determines the state.
ENGINE = ReplacingMergeTree(last_change_seq)
ORDER BY (msisdn, imsi, imei);

-- Daily change counts, now dated. Feeds every time-series widget.
DROP TABLE IF EXISTS sqm.agg_change_daily;
CREATE TABLE sqm.agg_change_daily
(
    seq       UInt16,
    data_date Date,
    tac       String,
    label     Enum8('add' = 1, 'remove' = 2),
    n         UInt64
)
ENGINE = SummingMergeTree(n)
PARTITION BY toYYYYMM(data_date)
ORDER BY (data_date, tac, label);

-- Per-day totals, so the headline time series does not have to aggregate the TAC-level table.
CREATE TABLE IF NOT EXISTS sqm.agg_change_summary_daily
(
    seq                UInt16,
    data_date          Date,
    added              UInt64,
    removed            UInt64,
    -- Events that changed nothing. Baselines measured in Phase 0: redundant adds 19.18%,
    -- orphan removes 1.77%. Tracked per day so a drift from those baselines is visible.
    redundant_adds     UInt64,
    orphan_removes     UInt64,
    -- Bindings whose IMEI is the 000000 sentinel.
    unknown_device_rows UInt64,
    rows_total         UInt64
)
ENGINE = ReplacingMergeTree
PARTITION BY toYYYYMM(data_date)
ORDER BY data_date;
