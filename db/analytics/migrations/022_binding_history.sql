-- 022_binding_history.sql
--
-- Every binding's history - when the feed added and removed it - kept so that one number, SIM or
-- handset's whole timeline is a key read. Approved by the product owner, 2026-10-01.
--
-- THE PROBLEM, measured on the real log (1,800,586,041 events, 233 daily partitions). A
-- timeline needs each binding's dated events. The log is ordered by MSISDN within each day, so
-- one binding's events are scattered over every day it was touched, and reading them costs about
-- one granule per number per day whatever the index:
--
--   a typical IMEI, 5 bindings           2,121,728 rows read    1.5 s
--   an IMEI with 243 bindings           84,578,811 rows read   29.7 s
--   the most-shared IMEI, 8,578 bindings  240,695,941 rows read   64.8 s   (over the 100M budget)
--
-- The handsets that matter most - shared ones - were exactly the ones that could not be shown.
--
-- THE ANSWER: one row per binding per month, holding that month's dated events, in three sort
-- orders (number, SIM, IMEI) so each kind of entity is a contiguous key range. A prototype over
-- one block of a million numbers (13,048,419 events, 3,810,910 bindings) measured:
--
--   an IMEI's timeline, 218 bindings, 579 events      81,920 rows read    34 ms
--   the stored events against the log, every binding   0 mismatches
--   size per sort order                                ~180 MiB for 0.72% of the log
--                                                      -> ~23 GiB each, ~70 GiB for three
--
-- WHY BY MONTH. The rows are aggregates - min, max, sum, concatenation - which merge correctly
-- whatever order days arrive in, so a late day is simply added. A day that is REPLACED is the
-- problem: its old events would stay in the aggregates. Partitioning by month makes the repair
-- exact and cheap: drop the month, rebuild it from the log. One binding is then 1.45 rows on
-- average (measured on the same block), not one per day.
--
-- WHY THE EVENTS ARE STORED, not only first and last dates: a timeline draws every add and
-- remove, and the second trip to the log for them is the cost being removed. They are 40 of the
-- 180 MiB above.
--
-- THE INITIAL DUMP is partition 0. It is not a snapshot but everything observed between
-- 2025-12-27 and 2026-01-25 (docs/discovery/03-dated-daily-files.md), so its bindings have no start
-- date. Their first_date is 2025-12-27, the earliest it could be - the product owner's decision,
-- 2026-10-01: shown as the window, sorted as its first day - and in_dump says so.
--
-- NOTHING IS LOADED HERE. The tables start empty; `Sqm.Ingestion --backfill-history` fills them
-- month by month, pausing merges and reconciling each month against the log, and the daily
-- import keeps them current from then on. Measured cost of the backfill: about two to two and a
-- half hours, once.

-- ===========================================================================================
-- 1. The history, ordered by number. The import writes here; the copies follow it.
-- ===========================================================================================
CREATE TABLE IF NOT EXISTS sqm.binding_history
(
    -- YYYYMM of the events in this row; 0 for the initial dump.
    month       UInt32,
    msisdn      UInt64,
    imsi        UInt64,
    imei        String,

    -- 1 when the initial dump listed this binding.
    in_dump     SimpleAggregateFunction(max, UInt8),

    -- Earliest and latest event date. Dump rows carry 2025-12-27 and 1970-01-01, so that min()
    -- and max() across a binding's rows give its first-seen date and its last change.
    first_date  SimpleAggregateFunction(min, Date),
    last_date   SimpleAggregateFunction(max, Date),

    adds        SimpleAggregateFunction(sum, UInt64),
    removes     SimpleAggregateFunction(sum, UInt64),

    -- (date, seq, 1 = add | 2 = remove). Unordered: concatenation is how rows merge, so a reader
    -- sorts by date and then seq - seq alone is wrong, because a late day takes the next one.
    events      SimpleAggregateFunction(groupArrayArray, Array(Tuple(Date, UInt16, UInt8)))
)
ENGINE = AggregatingMergeTree
PARTITION BY month
ORDER BY (msisdn, imsi, imei);

-- ===========================================================================================
-- 2. The same rows ordered by SIM and by IMEI. Filled by the inserts into the table above, the
-- way binding_by_imsi and binding_by_imei follow binding_current. A DROP PARTITION does not
-- propagate through a view, so the month rebuild drops all three itself.
-- ===========================================================================================
CREATE TABLE IF NOT EXISTS sqm.binding_history_by_imsi AS sqm.binding_history
ENGINE = AggregatingMergeTree
PARTITION BY month
ORDER BY (imsi, msisdn, imei);

CREATE TABLE IF NOT EXISTS sqm.binding_history_by_imei AS sqm.binding_history
ENGINE = AggregatingMergeTree
PARTITION BY month
ORDER BY (imei, msisdn, imsi);

CREATE MATERIALIZED VIEW IF NOT EXISTS sqm.mv_binding_history_by_imsi TO sqm.binding_history_by_imsi AS
SELECT month, msisdn, imsi, imei, in_dump, first_date, last_date, adds, removes, events
FROM sqm.binding_history;

CREATE MATERIALIZED VIEW IF NOT EXISTS sqm.mv_binding_history_by_imei TO sqm.binding_history_by_imei AS
SELECT month, msisdn, imsi, imei, in_dump, first_date, last_date, adds, removes, events
FROM sqm.binding_history;

-- ===========================================================================================
-- 3. Which days the history holds, and whether their write finished.
--
-- Written 'pending' BEFORE a day's events go in and 'done' after. A day found here at all is one
-- the history has seen before - a retry, or a corrected file replacing it - and adding it again
-- would count it twice, so its month is rebuilt from the log instead. The only cheap path is the
-- one that cannot double anything: a day the history has never seen.
-- ===========================================================================================
CREATE TABLE IF NOT EXISTS sqm.binding_history_day
(
    data_date   Date,
    state       Enum8('pending' = 1, 'done' = 2),
    -- Events the day contributed, for the reconciliation against the log.
    events      UInt64,
    updated_at  DateTime64(3)
)
ENGINE = ReplacingMergeTree(updated_at)
ORDER BY data_date;
