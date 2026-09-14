-- 017_mart_ready.sql
--
-- A register of which deliveries have COMPLETE marts, so the dashboard never reads a partial one.
--
-- THE PROBLEM THIS FIXES, found the hard way.
--
-- Every dashboard query selects its delivery with `WHERE seq = (SELECT max(seq) FROM <mart>)`.
-- That is correct only if the newest sequence present is also finished, and the mart refresh
-- writes 15 INSERTs across 6 marts - three measures into the device-class mart, three into the
-- capability mart, six dimensions into the dimension mart. A run where a third of them fail
-- still leaves every mart holding rows for the new sequence.
--
-- So max(seq) picks the half-built delivery, and the widgets whose slice failed return nothing.
-- Not an error, not a fallback to the previous good delivery - an empty chart, on a screen that
-- was working a minute earlier. Observed exactly this: agg_device_class_daily with only the
-- subscribers measure, agg_capability_daily with only bindings, and the dimension mart missing
-- `vendor`, which is the one Top Vendors reads.
--
-- THE FIX. Completion becomes a fact the refresh records, rather than something inferred from
-- the presence of rows. A sequence is removed from this table when its rebuild starts and added
-- back only when every statement has succeeded, so at any instant the dashboard is reading the
-- newest delivery that is actually whole. During a rebuild it serves the previous one - a day
-- older, internally consistent, and the freshness card says how old it is.
--
-- This is the analytics-store twin of what imports.import_job.is_effective does for the
-- operational side: the newest thing that exists is not necessarily the thing to serve.

CREATE TABLE IF NOT EXISTS sqm.mart_ready
(
    seq          UInt16,
    completed_at DateTime64(3, 'UTC'),
    statements   UInt16
)
ENGINE = ReplacingMergeTree(completed_at)
ORDER BY seq;

-- Seed with the initial snapshot, which is complete and has been serving the dashboard all
-- along. Anything newer has to earn its place by finishing a refresh.
INSERT INTO sqm.mart_ready (seq, completed_at, statements)
SELECT 0, now64(3), 0
WHERE (SELECT count() FROM sqm.mart_ready WHERE seq = 0) = 0;
