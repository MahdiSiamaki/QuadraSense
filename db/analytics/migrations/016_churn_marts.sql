-- 016_churn_marts.sql
--
-- SIM changes and handset changes per day.
--
-- WHY THEY ARE THEIR OWN TABLES. Both answer "how many subscribers moved" for one day, and
-- computing either needs a GROUP BY (data_date, msisdn) with array aggregates. Across the whole
-- event log that is hundreds of millions of groups - it does not fit in memory on any machine
-- this project has, and it never needs to: the answer for a day depends only on that day.
--
-- So these are built one day at a time, by the same code path the daily import uses. The tables
-- are partitioned by day so a rebuild of one day is a partition drop rather than a delete by
-- predicate.
--
-- THE DEFINITIONS ARE SAME-DAY, DELIBERATELY.
--
-- A SIM change is a number that on one day has a remove carrying one IMSI and an add carrying
-- another. A swap that spans midnight is missed, and one where the old SIM is removed days later
-- is counted on neither day. Detecting those needs per-subscriber state across days, which is a
-- different and much more expensive computation. This is the cheap, defensible version, and the
-- dashboard says which definition it is showing rather than letting the reader assume the
-- expensive one.

CREATE TABLE IF NOT EXISTS sqm.agg_sim_change_daily
(
    data_date      Date,
    msisdn_changed UInt64
)
ENGINE = ReplacingMergeTree
PARTITION BY data_date
ORDER BY data_date;

CREATE TABLE IF NOT EXISTS sqm.agg_device_change_daily
(
    data_date      Date,
    msisdn_changed UInt64
)
ENGINE = ReplacingMergeTree
PARTITION BY data_date
ORDER BY data_date;
