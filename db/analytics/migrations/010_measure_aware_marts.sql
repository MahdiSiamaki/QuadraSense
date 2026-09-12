-- 010_measure_aware_marts.sql
--
-- Lets the device-class mix and the capability breakdown follow the same
-- bindings/subscribers/handsets selector as every other widget.
--
-- SHAPE. A `measure` column rather than nine parallel count columns
-- (supported_bindings, supported_subscribers, supported_handsets, ...). The wide form
-- would have to grow by three columns every time a measure is added, and every query
-- would need to pick a column name by string. A row per measure keeps the tables narrow
-- and the queries uniform.
--
-- Volumes are trivial: 8 classes x 3 measures = 24 rows; 4 capabilities x 3 = 12 rows.
--
-- A CAVEAT THAT ONLY APPLIES TO SUBSCRIBERS. Under bindings and handsets, a row belongs to
-- exactly one class and one capability answer. Under subscribers it does not: someone who
-- owns a 5G phone and a feature phone is counted in both 'Smartphone' and 'Feature phone',
-- and in both 5G-supported and 5G-unsupported. The percentages are still correct answers to
-- "what share of subscribers have at least one such device" - they simply do not partition
-- the population, and the UI says so.

DROP TABLE IF EXISTS sqm.agg_device_class_daily;
CREATE TABLE sqm.agg_device_class_daily
(
    seq          UInt16,
    data_date    Nullable(Date),
    measure      LowCardinality(String),   -- 'bindings' | 'subscribers' | 'handsets'
    device_class String,
    n            UInt64
)
ENGINE = ReplacingMergeTree
PARTITION BY seq
ORDER BY (seq, measure, device_class);

DROP TABLE IF EXISTS sqm.agg_capability_daily;
CREATE TABLE sqm.agg_capability_daily
(
    seq         UInt16,
    data_date   Nullable(Date),
    measure     LowCardinality(String),
    capability  String,
    supported   UInt64,
    unsupported UInt64,
    unknown     UInt64
)
ENGINE = ReplacingMergeTree
PARTITION BY seq
ORDER BY (seq, measure, capability);
