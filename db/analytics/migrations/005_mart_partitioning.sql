-- 005_mart_partitioning.sql
--
-- Makes the mart refresh genuinely idempotent.
--
-- The bug this fixes: `refresh_marts.sql` only ever INSERTed. Running it twice for the same
-- delivery doubled every count in `agg_device_daily` — a SummingMergeTree happily sums the
-- duplicates — and the dashboard reported 251,879,046 bindings against a real 125,939,523.
-- Nothing errored. The numbers were simply wrong, which is the worst way for this to fail.
--
-- ADR-004 requires the pipeline to be idempotent, and the job's own comment claimed it was.
-- A comment is not a mechanism.
--
-- The fix is structural rather than procedural: partition each mart by `seq`, so a refresh can
-- DROP PARTITION before inserting. That is atomic and instant in ClickHouse, and it makes
-- re-running a delivery produce the same result by construction rather than by remembering to
-- clear the table first.
--
-- These tables are derived data, fully rebuildable from binding_current, so recreating them
-- costs nothing but the eight seconds of the rebuild.

DROP TABLE IF EXISTS sqm.agg_device_daily;
CREATE TABLE sqm.agg_device_daily
(
    seq        UInt16,
    data_date  Nullable(Date),
    tac        String,
    active     UInt8,
    n          UInt64
)
ENGINE = SummingMergeTree(n)
PARTITION BY seq
ORDER BY (seq, tac, active);

DROP TABLE IF EXISTS sqm.agg_change_daily;
CREATE TABLE sqm.agg_change_daily
(
    seq        UInt16,
    data_date  Nullable(Date),
    tac        String,
    label      Enum8('add' = 1, 'remove' = 2),
    n          UInt64
)
ENGINE = SummingMergeTree(n)
PARTITION BY seq
ORDER BY (seq, tac, label);

DROP TABLE IF EXISTS sqm.agg_device_class_daily;
CREATE TABLE sqm.agg_device_class_daily
(
    seq          UInt16,
    data_date    Nullable(Date),
    device_class String,
    n            UInt64
)
ENGINE = SummingMergeTree(n)
PARTITION BY seq
ORDER BY (seq, device_class);

DROP TABLE IF EXISTS sqm.agg_quality_daily;
CREATE TABLE sqm.agg_quality_daily
(
    seq        UInt16,
    data_date  Nullable(Date),
    rule       String,
    n          UInt64
)
ENGINE = SummingMergeTree(n)
PARTITION BY seq
ORDER BY (seq, rule);

-- agg_kpi_daily is a ReplacingMergeTree keyed on seq, so a re-insert replaces rather than
-- accumulates. It was never affected by this bug — but it is partitioned too, so every mart
-- is cleared the same way and the refresh job has no special cases to remember.
DROP TABLE IF EXISTS sqm.agg_kpi_daily;
CREATE TABLE sqm.agg_kpi_daily
(
    seq                     UInt16,
    data_date               Nullable(Date),
    active_bindings         UInt64,
    distinct_subscribers    UInt64,
    distinct_devices        UInt64,
    unknown_device_bindings UInt64,
    malformed_imei_bindings UInt64,
    tac_matched_bindings    UInt64,
    computed_at             DateTime64(3, 'UTC') DEFAULT now64(3)
)
ENGINE = ReplacingMergeTree(computed_at)
PARTITION BY seq
ORDER BY seq;
