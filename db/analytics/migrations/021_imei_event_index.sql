-- 021_imei_event_index.sql
--
-- A bloom-filter skip index on binding_event.imei: one handset's dated history without reading
-- the whole event log. Approved by the product owner, 2026-09-30, to run while nothing imports.
--
-- THE PROBLEM, measured on the real log (1,800,586,041 rows, 46.4 GiB, 233 daily partitions):
-- `WHERE imei = ...` read every row - 36.7 GiB, 27 s - because the log is ordered by MSISDN and
-- the IMEI is the third key column, which prunes nothing. The same question for an IMSI, which has
-- had this index since 018, read 1.9 million rows.
--
-- THE SAME ANSWER AS FOR THE IMSI, for the same reasons (ADR-008): a second copy of the log
-- ordered by IMEI would cost another ~46 GiB and a full re-ingest to buy the same query; a
-- projection would copy it too. A bloom filter costs a fraction and serves the query that is
-- actually asked - one handset, over a date range, where the range prunes partitions first.
--
-- 0.001 for the same reason as the IMSI index: at 220,529 granules, 1% false positives would read
-- ~2,200 granules (~18M rows) for nothing; 0.1% reads ~220.
--
-- MEASURED BEFORE APPROVAL, on a copy of ten real days (65,055,609 rows):
--   before   65,055,609 rows read, 0.44 s
--   after       106,496 rows read, 0.06 s   (about 600 times fewer)
--   built in 3 s, 0.113 GiB, peak mutation memory 3 MiB
-- Scaled to the whole log (x27.7): ~3.1 GiB of index - the IMSI index is 2.87 GiB - and minutes of
-- background work.
--
-- HOW IT RUNS. ADD INDEX is a metadata change: every part written from now on carries the index.
-- MATERIALIZE INDEX builds it for the parts that exist, as a mutation - it returns at once and
-- works in the background, reading the imei column and writing only the index files. Follow it in
-- system.mutations (table = 'binding_event', is_done). Until it finishes, a query uses the index
-- on the parts that have it and reads the others in full, so answers are right throughout; only
-- their cost changes.
ALTER TABLE sqm.binding_event
    ADD INDEX IF NOT EXISTS idx_imei imei TYPE bloom_filter(0.001) GRANULARITY 1;

ALTER TABLE sqm.binding_event MATERIALIZE INDEX idx_imei;
