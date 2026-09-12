-- 009_dimension_counts_mart.sql
--
-- Lets every breakdown be counted three ways: bindings, subscribers, or handsets.
--
-- WHY THIS TABLE HAS TO EXIST. The existing agg_device_daily is keyed by TAC and stores a
-- binding count, which sums freely. Distinct counts do not behave the same way:
--
--   * Handsets (IMEI) DO sum across TACs -- an IMEI has exactly one TAC, since the TAC is
--     literally its first 8 digits. No handset is counted under two vendors.
--
--   * Subscribers (MSISDN) DO NOT. One subscriber can hold a Samsung and an Apple handset,
--     so adding distinct-MSISDN-per-TAC would count them once per vendor and overstate the
--     total. Measured: Samsung alone has 40,070,800 subscribers out of 79.5M — the vendor
--     columns must overlap heavily.
--
-- So the distinct counts are computed per dimension value, once per delivery, rather than
-- derived at query time.
--
-- Counts use uniq() (HyperLogLog, ~0.5% error). On figures displayed as "39.3M" that is
-- invisible, and uniqExact over 126M rows exhausted the server's memory when tried.
--
-- Filtered views cannot use this table -- it is pre-aggregated on a single dimension with no
-- filter applied -- and fall back to the raw table, exactly as they already do for bindings.

CREATE TABLE IF NOT EXISTS sqm.agg_dimension_daily
(
    seq         UInt16,
    data_date   Nullable(Date),
    -- 'vendor' | 'manufacturer' | 'model' | 'deviceType' | 'os' | 'tac'
    dimension   LowCardinality(String),
    dim_value   String,
    bindings    UInt64,
    subscribers UInt64,
    handsets    UInt64
)
-- Replacing, not Summing. These are distinct counts: adding two rows for the same key
-- would be meaningless rather than merely duplicated.
ENGINE = ReplacingMergeTree
PARTITION BY seq
ORDER BY (seq, dimension, dim_value);
