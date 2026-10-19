-- 027_tac_population.sql
--
-- The active population per model (TAC), per delivery, with its distinct counts kept as mergeable
-- uniq() states rather than finished numbers - so a filtered dashboard view is a merge over these
-- rows instead of a scan of binding_current.
--
-- WHY. Every dashboard filter (vendor, device type, OS, TAC, unknown devices) is a property of the
-- TAC. Migration 009 sent filtered views to binding_current FINAL because the marts held finished
-- distinct counts, which cannot be added up across models. That read measured ~6 s then. On
-- 2026-10-19 it measured 100 s alone and 183 s on the dashboard - past the API's 180 s client
-- timeout, so the KPI row failed with 500, was retried, and the abandoned query kept running.
-- The cause is the table, not the query: binding_current held 817,326,809 row versions for
-- 120,354,456 live bindings in 18 parts, because merges are capped at 2 GiB on this node
-- (infra/clickhouse/memory.xml) and FINAL has to collapse every version on every read.
-- `count() ... FINAL` alone took 74 s against 5 s without FINAL.
--
-- A uniq() state merged across models is the same estimator the raw path used (uniq over the
-- union), so a filtered figure keeps the meaning and the ~0.5% error it had, and a subscriber who
-- holds two models is still counted once.
--
-- BUILT in refresh_marts.sql section 1b, in the pass that already grouped binding_current by TAC
-- for agg_device_model; agg_device_model is now finalised from these states, so the refresh reads
-- binding_current once for both. Measured on the 2026-10-19 data: 158,002 TACs, 120,354,456
-- bindings, 336 s at two threads, 149 MiB peak.
--
-- PARTITION BY seq, and the refresh drops the partition before inserting, as for every mart: an
-- insert-only refresh once doubled agg_device_daily silently.
CREATE TABLE IF NOT EXISTS sqm.agg_tac_population
(
    seq             UInt16,
    -- '' for bindings whose IMEI is not 14 digits: the 000000 sentinel and malformed IMEIs.
    tac             String,
    bindings        UInt64,
    -- Split of the '' row, which the KPI row reports separately. Zero for every real TAC.
    unknown_device  UInt64,     -- imei = '000000'
    malformed_imei  UInt64,     -- not 14 digits and not 000000
    subscribers     AggregateFunction(uniq, UInt64),
    sims            AggregateFunction(uniq, UInt64),
    handsets        AggregateFunction(uniq, String),   -- 14-digit IMEIs only
    -- Earliest and latest day a daily file named a binding of this model; NULL when only the
    -- initial dump ever did (agg_device_model's first_seen / last_seen).
    first_seen      Nullable(Date),
    last_seen       Nullable(Date)
)
ENGINE = MergeTree
PARTITION BY seq
ORDER BY (seq, tac);
