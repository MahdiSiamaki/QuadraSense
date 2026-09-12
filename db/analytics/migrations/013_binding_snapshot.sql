-- 013_binding_snapshot.sql
--
-- The initial dump, kept as its own table.
--
-- It was previously loaded straight into binding_current, which conflated two different
-- things: the baseline the operator delivered, and the derived state produced by folding
-- events onto it. Separating them means the fold can be re-run at any time without
-- re-reading the 4.86 GiB source CSV, and it makes binding_current honestly derived - it can
-- be truncated and rebuilt from binding_snapshot plus binding_event whenever needed.
--
-- Note what this table is NOT. It is not a point-in-time snapshot despite the name: the
-- source file covers 2025-12-27 to 2026-01-25 and averages 1.59 bindings per MSISDN, which a
-- single instant cannot produce. It is the set of bindings observed during that window. That
-- distinction is Q1, still unanswered, and it is the reason 19.18% of the first deltas' adds
-- refer to bindings the dump already contains.

CREATE TABLE IF NOT EXISTS sqm.binding_snapshot
(
    msisdn UInt64,
    imsi   UInt64,
    imei   String,
    tac    String MATERIALIZED if(length(imei) = 14, substring(imei, 1, 8), '')
)
ENGINE = MergeTree
ORDER BY (msisdn, imsi, imei);
