-- 026_quality_snapshot.sql
--
-- Phase 5 (data-quality signals): per run of the measures snapshot (migration 024), how many
-- bindings fall in each data-quality category - from current state, and from each binding's
-- events in the binding history - and how long the feed held a binding before removing it.
--
-- WHY PRECOMPUTED. The current-state categories are one scan of binding_current FINAL (273 s at two
-- threads, measured 2026-10-05); the history categories are one pass over the binding history (260 s
-- for a 24th of the numbers at one thread). The worker builds them while idle, in the same runs and
-- under the same publication rule as the risk measures, chunked by number; readers sum the chunks.
--
-- DATA QUALITY, NOT RISK. Separate tables, read by separate endpoints and shown on their own page:
-- nothing here is evidence about a subscriber.
--
-- COUNTS. bindings and numbers add up exactly across chunks (a chunk is a range of numbers); SIMs
-- and IMEIs are uniq() states, merged when read, so they are estimates (~0.5%) and labelled so.
-- For the lifetime categories, periods counts the add-to-remove periods and bindings the bindings
-- that had at least one.

ALTER TABLE sqm.risk_run ADD COLUMN IF NOT EXISTS msisdn_cuts Array(UInt64) DEFAULT [] AFTER imei_cuts;

CREATE TABLE IF NOT EXISTS sqm.quality_chunk
(
    run_id    UInt64,
    chunk     UInt16,
    category  LowCardinality(String),
    bindings  UInt64,
    numbers   UInt64,
    sims      AggregateFunction(uniq, UInt64),
    imeis     AggregateFunction(uniq, String),
    periods   UInt64
)
ENGINE = MergeTree
PARTITION BY (run_id, chunk)
ORDER BY category;
