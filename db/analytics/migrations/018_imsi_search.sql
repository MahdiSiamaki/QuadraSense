-- 018_imsi_search.sql
--
-- Makes IMSI a searchable key. Structure only: the 295-million-row backfill is a worker command
-- (`--backfill-imsi`), for the same reason the mart backfill is - it needs to pause merges, run in
-- chunks, and reconcile afterwards, none of which a migration runner can do.
--
-- ===========================================================================================
-- THE PROBLEM, MEASURED
--
-- binding_current is ORDER BY (msisdn, imsi, imei). A filter on the SECOND key column prunes
-- nothing: ClickHouse falls back to "generic exclusion search", which can only exclude a range
-- between two marks when the leading column is unchanged across it - and msisdn is nearly unique,
-- so it almost never is.
--
-- Five real IMSIs, exact lookup on binding_current, before this migration:
--
--   432113900974194   1385 ms   295,013,916 rows   36,013/36,013 marks   2.20 GiB
--   432113933569397   1284 ms   295,013,916 rows   36,013/36,013 marks   2.20 GiB
--   432113950963412   1419 ms   295,013,916 rows   36,013/36,013 marks   2.20 GiB
--   432113984058502   1273 ms   295,013,916 rows   36,013/36,013 marks   2.20 GiB
--   432113991761332     23 ms        16,384 rows            2/36,013     410 KiB
--
-- The fifth is not a fifth data point. It is one value whose position happens to let the
-- exclusion search bound it, and taking it as representative is exactly the mistake that made the
-- first version of this work look unnecessary. Four of five read the entire table.
--
-- ===========================================================================================
-- WHY A TABLE AND NOT AN INDEX
--
-- A bloom_filter skip index on binding_current.imsi was built and measured first, because it is
-- twenty times cheaper in storage:
--
--   bloom_filter(0.01)    109 MiB    73-190 ms   ~2.8M rows (330 of 36,013 marks)
--   bloom_filter(0.001)   164 MiB    58-140 ms   ~280K rows ( 34 of 36,013 marks)
--
-- Both work, and the false-positive count matches the theory exactly: 36,013 granules times the
-- configured rate. But a bloom filter answers set membership and nothing else, so PREFIX search -
-- which is a numeric range - gets no help at all: it still read all 295M rows in 2,518 ms.
--
-- A projection ORDER BY imsi would serve both, and was rejected: ClickHouse does not use
-- projections for queries with FINAL, and every read of this table needs FINAL to resolve the
-- ReplacingMergeTree. A second table has no such restriction.
--
-- Measured after this migration and its backfill, same five IMSIs, 9 parts:
--
--   exact          20-50 ms    69,143 rows   9 marks
--   prefix 13d        16 ms    69,143 rows   9 marks
--   prefix 12d        21 ms    69,143 rows   9 marks
--   prefix 10d        39 ms   183,831 rows  23 marks
--
-- ~60x faster on exact, ~120x on prefix, and 4,270x fewer rows read. The floor is one granule per
-- part, which is why the backfill compacts when it finishes.
--
-- Cost: 6.45 GiB, against 916 GiB free. See docs/adr/ADR-008-imsi-search.md.
-- ===========================================================================================

CREATE TABLE IF NOT EXISTS sqm.binding_by_imsi
(
    imsi             UInt64,
    msisdn           UInt64,
    imei             String,
    tac              String MATERIALIZED if(length(imei) = 14, substring(imei, 1, 8), ''),
    active           UInt8,
    last_change_seq  UInt16,
    last_change_date Nullable(Date)
)
ENGINE = ReplacingMergeTree(last_change_seq)
ORDER BY (imsi, msisdn, imei)
SETTINGS index_granularity = 8192;

-- ---------------------------------------------------------------------------
-- Kept in step by a materialized view, not by the worker writing twice.
--
-- The daily fold is `INSERT INTO binding_current SELECT ... GROUP BY msisdn, imsi, imei`, so an MV
-- on that table mirrors exactly the rows the fold decided on, with the same version column. The
-- two tables therefore converge on the same state without the ingestion code knowing this table
-- exists - and a future change to the fold cannot forget to update it.
--
-- THE ONE WAY THIS BREAKS, stated so it is not discovered: a materialized view fires on INSERT
-- and on nothing else. Rebuilding binding_current by EXCHANGE TABLES - which migration 015 did to
-- binding_event - bypasses it completely and leaves this table describing the old data. Any such
-- rebuild must be followed by `Sqm.Ingestion --backfill-imsi`, and the backfill's reconciliation
-- step is what proves it was.
-- ---------------------------------------------------------------------------
CREATE MATERIALIZED VIEW IF NOT EXISTS sqm.mv_binding_by_imsi TO sqm.binding_by_imsi AS
SELECT imsi, msisdn, imei, active, last_change_seq, last_change_date
FROM sqm.binding_current;

-- ---------------------------------------------------------------------------
-- History: a skip index on the event log rather than a second copy of it.
--
-- binding_event is 1.05 billion rows and 25.28 GiB. A second copy ordered by IMSI would cost the
-- same again, and would buy prefix search over history - which nothing asks for: you resolve a
-- prefix against current state, then open one SIM.
--
-- So the event log gets a bloom filter, which serves the query that IS asked - one IMSI, over a
-- date range - and costs a fraction of a copy. The date range prunes partitions before the index
-- is consulted at all, since the table is PARTITION BY data_date.
--
-- Measured before, three IMSIs, no date filter:
--   10,571 ms / 12,866 ms / 20,792 ms, each reading all 1,049,379,693 rows (128,418 marks, 7.82 GiB)
--
-- 0.001 rather than 0.01 because this table has 128,418 granules: at 1% the false positives alone
-- would be 1,284 granules, ~10.5M rows. At 0.1% they are ~128 granules, ~1M rows.
-- ---------------------------------------------------------------------------
ALTER TABLE sqm.binding_event
    ADD INDEX IF NOT EXISTS idx_imsi imsi TYPE bloom_filter(0.001) GRANULARITY 1;

-- ---------------------------------------------------------------------------
-- The bloom filter trialled on binding_current is dropped.
--
-- It works - the numbers are above - but binding_by_imsi answers the same query faster and
-- answers prefix search too, so keeping both would be paying 164 MiB for an index nothing reaches.
-- The measurement is kept in this file and in ADR-008; the index is not.
-- ---------------------------------------------------------------------------
ALTER TABLE sqm.binding_current DROP INDEX IF EXISTS idx_imsi_bloom;
ALTER TABLE sqm.binding_current DROP INDEX IF EXISTS idx_imsi_bloom_tight;
