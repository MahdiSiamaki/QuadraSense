-- 015_daily_partitioning.sql
--
-- Repartitions the event log and the change marts by day instead of by month.
--
-- WHY. Re-importing a corrected day has to remove that day first, or the import doubles it.
-- With a monthly partition key the only way to do that is `ALTER TABLE ... DELETE WHERE
-- data_date = X`, which in ClickHouse is a mutation: it rewrites every part of the month it
-- touches, roughly 4 GiB and 250 million rows to remove 8 million.
--
-- This is measured, not feared. An early version of the bulk loader issued that mutation once
-- per file unconditionally, and the load went quadratic - the first days took 11 s each and the
-- rate kept falling as the table grew. The loader was fixed by checking whether the day was
-- present before mutating, which avoided the cost but did not remove it; a corrected file still
-- pays it.
--
-- With the day as the partition key, removing a day becomes `DROP PARTITION`: a metadata
-- operation that unlinks the day's parts. Instant, atomic, and it cannot touch another day.
--
-- COST OF THE CHANGE. A partition key cannot be altered in place, so the table is rebuilt:
-- ~1.05 billion rows and ~17 GiB read and written once. The rebuild streams, so it is bounded
-- by disk rather than memory.
--
-- COST OF THE RESULT. 365 partitions a year instead of 12. ClickHouse is comfortable into the
-- low thousands, so this is fine for years; the number to watch is total active parts, and the
-- daily files are ~8 million rows each, which is a healthy part size rather than a small one.

-- ---------------------------------------------------------------------------
-- 1. The event log.
--
-- Built as a new table and swapped, rather than dropped and refilled. If anything fails
-- partway, the original is still there and still correct.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sqm.binding_event_daily
(
    seq       UInt16,
    data_date Date,
    msisdn    UInt64,
    imsi      UInt64,
    imei      String,
    tac       String MATERIALIZED if(length(imei) = 14, substring(imei, 1, 8), ''),
    label     Enum8('add' = 1, 'remove' = 2)
)
ENGINE = MergeTree
PARTITION BY data_date
ORDER BY (msisdn, imsi, imei, seq);

-- The guard makes the rebuild re-runnable.
--
-- ClickHouse has no DDL transactions, so a migration can fail partway; this one reads a billion
-- rows and takes tens of minutes, which is long enough for a client to give up on a statement
-- the server is still happily running. Without the guard, a second run would append a second
-- copy of every event - and a SummingMergeTree downstream would sum the duplicates without
-- complaint, exactly as happened once before in this project.
INSERT INTO sqm.binding_event_daily (seq, data_date, msisdn, imsi, imei, label)
SELECT seq, data_date, msisdn, imsi, imei, label
FROM sqm.binding_event
WHERE (SELECT count() FROM sqm.binding_event_daily) = 0;

-- EXCHANGE, not two renames. It is atomic: no window exists in which the name refers to
-- nothing, so a query running at the moment of the swap sees one table or the other.
EXCHANGE TABLES sqm.binding_event AND sqm.binding_event_daily;

DROP TABLE sqm.binding_event_daily;

-- ---------------------------------------------------------------------------
-- 2. The change marts.
--
-- Dropped and recreated rather than rebuilt: they are derived, and refresh_change_marts.sql
-- repopulates them from the event log in one pass. Rewriting them here would do the same work
-- twice.
-- ---------------------------------------------------------------------------
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
PARTITION BY data_date
ORDER BY (data_date, tac, label);

DROP TABLE IF EXISTS sqm.agg_change_summary_daily;

CREATE TABLE sqm.agg_change_summary_daily
(
    seq                 UInt16,
    data_date           Date,
    added               UInt64,
    removed             UInt64,
    redundant_adds      UInt64,
    orphan_removes      UInt64,
    unknown_device_rows UInt64,
    rows_total          UInt64
)
ENGINE = ReplacingMergeTree
PARTITION BY data_date
ORDER BY data_date;
