-- 032_planning_figures_and_model_index.sql
--
-- Two things the planning views could not do fast enough, each measured first.
-- docs/architecture/19-network-planning.md sections 3 and 5.
--
-- 1. THE BAND VIEW, WRITTEN AT PUBLICATION. Unnarrowed, the band view merged every LTE and NR
--    model's uniq() states on each request: 33 s on the laptop and about 45 s on the department VM
--    (its LTE bands alone 24-29 s and 434 MiB of the read path's 476 MiB cap, 2026-10-27). None of
--    it depends on a filter or a setting, so the worker writes it once, when the run publishes,
--    under its own larger limits; the page reads a few hundred rows. The match against the
--    operator's bands depends on a setting the worker does not own, and stays live.
--
-- 2. WHO HOLDS WHICH MODEL, PER NUMBER. A list of ready or eSIM numbers narrowed by vendor, model or
--    device type was refused: the run kept each number's models only for the exposed, LTE-M /
--    NB-IoT-only and not-assessable classes. One row per (number, model), ordered by number and
--    carrying the number's classes, lets a narrowed page, its count and its campaign file stream in
--    number order - holding one number at a time, whatever the class's size.

-- Per-model figures of a published run that no filter or setting changes: one row per item of a
-- view. Kept with the run's summary, for every run; a few hundred rows each.
CREATE TABLE IF NOT EXISTS sqm.planning_model_figure
(
    run_id          UInt64,
    view            LowCardinality(String),   -- 'bands'
    item            String,                   -- 'lte:3', 'nr:78', 'arch-both', 'ambiguous'
    numbers         UInt64,                   -- numbers holding such a model, merged uniq() - estimated
    bindings        UInt64,
    handsets        UInt64,                   -- estimated
    written_at      DateTime64(3)
)
ENGINE = ReplacingMergeTree(written_at)
ORDER BY (run_id, view, item);

-- Every model a number holds in a run, with the number's classes and counts: written by each
-- number chunk from its stage, dropped with the run's other rows.
CREATE TABLE IF NOT EXISTS sqm.planning_number_model
(
    run_id          UInt64,
    chunk           UInt16,
    msisdn          UInt64 CODEC(Delta, ZSTD(1)),   -- in order within a chunk: the deltas are small
    tac             LowCardinality(String),   -- '' for an unknown device
    sunset          UInt8,
    reason          UInt8,
    esim            UInt8,
    handsets        UInt16,
    bindings        UInt16
)
ENGINE = MergeTree
PARTITION BY (run_id, chunk)
ORDER BY (msisdn, tac);
