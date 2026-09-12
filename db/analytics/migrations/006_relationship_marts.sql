-- 006_relationship_marts.sql
--
-- Marts for the subscriber/device relationship widgets.
--
-- These answer questions the TAC rollup cannot, because they are about how many
-- distinct things relate to each other rather than about device attributes:
--
--   * How many devices does a subscriber use?      (multi-device behaviour)
--   * How many subscribers share a device?         (dual-SIM and resale)
--   * Which operator prefixes are in the base?     (subscriber segmentation)
--
-- Computing these live is the single most expensive shape in the whole workload:
-- grouping 126M rows into ~79M distinct MSISDNs measured at 52 seconds. That is
-- 100x over the dashboard budget, so they are pre-computed once per delivery into
-- tables of a few dozen rows.

-- Devices per subscriber. Buckets 1..9 exactly, then 10+.
-- Measured on the initial snapshot: 55.5M subscribers on one device, 14.6M on two.
CREATE TABLE IF NOT EXISTS sqm.agg_devices_per_subscriber
(
    seq        UInt16,
    data_date  Nullable(Date),
    bucket     UInt8,      -- 1..9, or 10 meaning "10 or more"
    n_msisdn   UInt64
)
ENGINE = SummingMergeTree(n_msisdn)
PARTITION BY seq
ORDER BY (seq, bucket);

-- Subscribers per device. The dual-SIM and device-resale signal.
-- Measured: 73.2M IMEIs used by one number, 15.3M by two or more.
CREATE TABLE IF NOT EXISTS sqm.agg_subscribers_per_device
(
    seq        UInt16,
    data_date  Nullable(Date),
    bucket     UInt8,
    n_imei     UInt64
)
ENGINE = SummingMergeTree(n_imei)
PARTITION BY seq
ORDER BY (seq, bucket);

-- Subscriber numbers by operator prefix. 342 distinct 4-digit prefixes were observed;
-- the head is standard Iranian MCI (9149, 9148, 9144, …) and the tail contains
-- non-conforming ranges worth seeing rather than silently averaging away.
CREATE TABLE IF NOT EXISTS sqm.agg_msisdn_prefix
(
    seq        UInt16,
    data_date  Nullable(Date),
    prefix4    String,
    n          UInt64
)
ENGINE = SummingMergeTree(n)
PARTITION BY seq
ORDER BY (seq, prefix4);
