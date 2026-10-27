-- 019_progress_within_step.sql
--
-- Progress within one step of a stage: "12 of 31 statements".
--
-- AGGREGATING reports its seven steps (018). The last, rebuilding the dashboard figures, is most of
-- the stage when it runs - 31 statements, ~31 of ~34 minutes of an import on the laptop - and as one
-- step its bar stood still for all of it. The mart refresh counts its statements anyway; these carry
-- the count to the page, with the unit it is counted in.
ALTER TABLE imports.import_progress
    ADD COLUMN IF NOT EXISTS step_done  integer,
    ADD COLUMN IF NOT EXISTS step_total integer,
    ADD COLUMN IF NOT EXISTS step_unit  text;

COMMENT ON COLUMN imports.import_progress.step_done IS
    'Within the running step: how much is done, in step_unit.';
COMMENT ON COLUMN imports.import_progress.step_total IS
    'Within the running step: how much there is.';
COMMENT ON COLUMN imports.import_progress.step_unit IS
    'What the step counts - "statements" for the dashboard rebuild.';
