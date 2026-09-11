-- 003_kpi_mart.sql
-- Headline counters, pre-aggregated per delivery.
--
-- Why this table exists separately from agg_device_daily:
--
-- Counts of bindings are additive, so they can be summed out of a TAC-level rollup.
-- Distinct counts of subscribers and devices are NOT - you cannot add up distinct
-- MSISDNs per TAC and get distinct MSISDNs overall, because one subscriber can hold
-- bindings across several TACs (measured: 30% of MSISDNs have 2+ bindings).
--
-- So the distinct counts are computed once, at ingest, over the whole population,
-- and stored. One row per delivery - about 365 rows a year.

CREATE TABLE IF NOT EXISTS sqm.agg_kpi_daily
(
    seq                     UInt16,
    data_date               Nullable(Date),
    active_bindings         UInt64,
    distinct_subscribers    UInt64,
    distinct_devices        UInt64,
    unknown_device_bindings UInt64,
    tac_matched_bindings    UInt64,
    computed_at             DateTime64(3, 'UTC') DEFAULT now64(3)
)
ENGINE = ReplacingMergeTree(computed_at)
ORDER BY seq;

-- Device-class rollup: the smartphone / feature phone / IoT mix.
--
-- GSMA deviceType has 19 values with a long tail. Charting all 19 is unreadable, and
-- picking an arbitrary top 5 hides the IoT segment (Modem + Dongle + IoT Device +
-- Module + WLAN Router together are ~8% of bindings and are a real business segment,
-- not noise). Grouping them into classes keeps that visible.
CREATE TABLE IF NOT EXISTS sqm.agg_device_class_daily
(
    seq          UInt16,
    data_date    Nullable(Date),
    device_class String,
    n            UInt64
)
ENGINE = SummingMergeTree(n)
ORDER BY (seq, device_class);
