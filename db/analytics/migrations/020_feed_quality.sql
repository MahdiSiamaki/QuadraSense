-- 020_feed_quality.sql
--
-- Feed quality, one day at a time: what each day's file looks like, measured, so a day that looks
-- unlike the days before it is flagged when it arrives rather than found weeks later in a chart.
--
-- WHY. Two defects in the operator's own files went unnoticed for weeks, found only because a
-- chart looked wrong (measured 2026-09-30, and confirmed in the raw files, not only in the store):
--
--   * From 2026-09-15, IMEIs with their first digit dropped and a 0 added at the end:
--     '35004012xxxxxx' arrives as '5004012xxxxxx0'. About 7,000 rows a day before, 261,000 on
--     15 September, 2.4 to 3.2 million a day from the 17th - a quarter of every file. 99.6% of
--     them become a real GSMA TAC again with one digit put back in front, against about 2.7% by
--     chance.
--   * From 2026-07-27, SIMs carrying more than one phone number on the same day: 500 to 900 a
--     day before, up to 116,000 in late August, 260,000 to 383,000 a day from 16 September.
--
-- Together they inflated SIM changes about 15-fold and handset changes about 5-fold. Nothing in
-- the import could have said so: every row was well-formed. What is wrong is the distribution,
-- and only a comparison with other days shows that.
--
-- THIS IS DATA QUALITY, NOT RISK. These tables describe the feed. A SIM with two numbers on a
-- day is a fact about a row here; whether it means anything about a subscriber is a different
-- question, answered elsewhere and never from this table alone.

-- ---------------------------------------------------------------------------
-- One row per day. Recomputed whenever the day is imported or re-imported; the newest row wins.
--
-- Counts, not rates: the rates are derived where they are read, so a change to how a rate is
-- judged never needs the days recomputed.
--
-- unknown_tac_rows and shifted_imei_rows are judged against the GSMA version active when the row
-- was computed, and tac_version_id says which. Activating a new version does not recompute them;
-- `Sqm.Ingestion --refresh-quality --force` does.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sqm.dq_daily
(
    data_date              Date,
    rows_total             UInt64,
    -- Distinct IMSIs in the day's rows: the denominator for multi_number_sims.
    sims_total             UInt64,
    -- The '000000' sentinel: the network did not know the device.
    unknown_device_rows    UInt64,
    -- Neither 14 digits nor the sentinel. 25 and 26 August carried ~10% of these, IMEIs cut to
    -- eight digits.
    malformed_imei_rows    UInt64,
    -- 14 digits, TAC not in the active GSMA version.
    unknown_tac_rows       UInt64,
    -- Of those, ending in 0 and made a GSMA TAC again by one leading digit: the 15 September shape.
    shifted_imei_rows      UInt64,
    -- SIMs with two or more phone numbers on this day, and the rows they carry.
    multi_number_sims      UInt64,
    multi_number_sim_rows  UInt64,
    tac_version_id         UInt32,
    computed_at            DateTime64(3, 'UTC')
)
ENGINE = ReplacingMergeTree(computed_at)
ORDER BY data_date;

-- ---------------------------------------------------------------------------
-- The SIMs behind multi_number_sims, per day - so a rule elsewhere can set those rows aside or
-- mark them, instead of learning only that "some" rows on the day were affected.
--
-- Partitioned by day like the other day-level tables, so recomputing a day is a partition drop
-- and an insert. Small: 500-900 rows on an ordinary day, up to ~383,000 on the worst.
--
-- The shifted IMEIs need no such table. They are recognisable from the IMEI itself, wherever it
-- appears, by the same test that counts them above.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sqm.dq_multi_number_sim_day
(
    data_date  Date,
    imsi       UInt64,
    numbers    UInt32,
    rows       UInt32
)
ENGINE = MergeTree
PARTITION BY data_date
ORDER BY (data_date, imsi);
