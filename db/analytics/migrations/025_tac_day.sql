-- 025_tac_day.sql
--
-- Phase 5 (new models, network age): per day and per model (TAC), the handsets and SIMs a daily file
-- named.
--
-- WHY. "When did this model first appear?" had no honest answer. agg_device_model.first_seen is the
-- earliest *last change* among a model's current bindings: a model whose early bindings were all
-- replaced looks newer than it is. The first day a daily file named any handset of the model is the
-- answer, and the event log holds it - at a 1.8-billion-row scan (198 s, measured 2026-10-05). This
-- table keeps it at about 40,000 rows a day (one per model), written by the import after the day
-- (TacDayStep, 22-27 s at one thread, measured), so the question becomes a GROUP BY over a few
-- million rows.
--
-- NOT KEPT: which of a day's handsets the feed had never seen. That needs each IMEI looked up in the
-- binding history - 450 million rows read per eighth of a day, measured - and growth over time is
-- already on the device page from agg_device_model, delivery by delivery.
--
-- THE INITIAL DUMP is day 1970-01-01: one row per model the dump listed. The dump is a month of
-- observations without dates (2025-12-27..2026-01-25), so a model in it is "seen since before
-- 2026-01-26", never "new".
--
-- COUNTABLE IMEIs ONLY - 14 digits, not fourteen zeros, not the shifted shape (the feed-quality
-- test, unchanged). Without it September alone showed 85,290 "new" TACs, 84,738 of them the
-- shifted IMEIs' fake prefixes.
CREATE TABLE IF NOT EXISTS sqm.tac_day
(
    data_date    Date,      -- 1970-01-01 for the initial dump
    tac          String,
    imeis        UInt32,    -- distinct countable IMEIs of this model with an event that day
    sims         UInt32,    -- distinct SIMs bound to those IMEIs by the day's events
    adds         UInt32,    -- add events
    computed_at  DateTime64(3)
)
ENGINE = MergeTree
PARTITION BY data_date          -- a corrected day is replaced by dropping its partition, as elsewhere
ORDER BY (data_date, tac);
