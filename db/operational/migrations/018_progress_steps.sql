-- 018_progress_steps.sql
--
-- Progress inside a stage that reads no file: which of its steps is running.
--
-- import_progress counted bytes of the file (007), which is right for validating and loading and
-- says nothing about AGGREGATING - folding the day into current state, the change marts, the
-- binding history, feed quality, risk, the model counts and the dashboard - which is most of a daily
-- import: on the department VM 25 of its 30 minutes. The page showed an indeterminate sweep for all
-- of it. A step label and its place in the list let it say what is happening and how far along.
--
-- Separate columns rather than reusing the byte counters for steps: a number whose unit depends on
-- another column is a number somebody reads in the wrong unit.
ALTER TABLE imports.import_progress
    ADD COLUMN IF NOT EXISTS step       text,
    ADD COLUMN IF NOT EXISTS step_index smallint,
    ADD COLUMN IF NOT EXISTS step_count smallint;

COMMENT ON COLUMN imports.import_progress.step IS
    'The step of the stage now running, in words, for a stage that reads no file (AGGREGATING).';
COMMENT ON COLUMN imports.import_progress.step_index IS
    'Its place in the stage''s steps, from 1.';
COMMENT ON COLUMN imports.import_progress.step_count IS
    'How many steps the stage has.';
