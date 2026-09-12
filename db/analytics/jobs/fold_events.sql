-- fold_events.sql
--
-- Rebuilds binding_current from the initial dump plus every event in binding_event.
--
-- HOW THE FOLD WORKS. binding_current is a ReplacingMergeTree keyed on
-- (msisdn, imsi, imei) and versioned by last_change_seq, so on merge the row with the
-- highest sequence wins. That means the fold does not have to replay events in order at all:
-- it only has to write, for each binding, the state implied by its LAST event.
--
-- That is correct because Phase 0 proved the feed has set semantics - `add` and `remove`
-- toggle a binding, and the final state depends only on the last event, not the path taken.
-- Verified over 8,062,257 transitions with zero double-adds.
--
-- Two sources are written:
--   seq 0  - the initial dump, every binding active. Loses to any later event.
--   seq N  - one row per binding that has at least one event, carrying its last state.
--
-- A binding present only in the dump keeps its seq-0 row. A binding with events takes the
-- event row, because its sequence is higher.

-- --------------------------------------------------------------------------
-- 1. Baseline: the initial dump, as it was delivered.
-- --------------------------------------------------------------------------
TRUNCATE TABLE sqm.binding_current;

INSERT INTO sqm.binding_current (msisdn, imsi, imei, active, last_change_seq, last_change_date)
SELECT msisdn, imsi, imei, 1, 0, NULL
FROM sqm.binding_snapshot;

-- --------------------------------------------------------------------------
-- 2. Fold: the last event per binding decides its state.
--
--    argMax over ~1.1B rows grouping into ~227M bindings. This is the single heaviest
--    statement in the system; run_refresh.py's per-query ceiling lets it spill rather than
--    entering the server's overcommit arbitration.
-- --------------------------------------------------------------------------
INSERT INTO sqm.binding_current (msisdn, imsi, imei, active, last_change_seq, last_change_date)
SELECT
    msisdn,
    imsi,
    imei,
    argMax(label, seq) = 'add' AS active,
    max(seq)                   AS last_change_seq,
    argMax(data_date, seq)     AS last_change_date
FROM sqm.binding_event
GROUP BY msisdn, imsi, imei;

-- --------------------------------------------------------------------------
-- 3. Collapse. Until this runs, binding_current holds both the baseline row and the event
--    row for any binding that changed, and a plain SELECT would double-count.
-- --------------------------------------------------------------------------
OPTIMIZE TABLE sqm.binding_current FINAL;
