-- 007_progress_is_bytes.sql
--
-- Renames two columns that have been lying since they were created.
--
-- ===========================================================================================
-- WHAT WAS WRONG.
--
-- imports.import_progress called its counters rows_processed and rows_expected. Both import
-- processors fill them from the file position - SqmDailyProcessor passes `bytesSeen` against
-- `job.FileBytes`, TacSnapshotProcessor passes `bytesRead` against `total`. They have always
-- been bytes.
--
-- Nothing computed from them was wrong: `percent` is bytes over bytes, which is a correct
-- fraction of the file. What was wrong was the two absolute numbers, which the Import Center
-- printed unlabelled beside the progress bar:
--
--     274,726,912 of 335,100,378
--
-- for a file the very same page reports as having 7,066,140 rows. A reader who knows the row
-- count sees a number forty times too large and has no way to tell whether the import is
-- confused or the display is.
--
-- Measuring by bytes is the right choice, and that is worth keeping: the row count is not known
-- until the file has been read, so a byte-based bar can start at zero and finish at a hundred
-- without ever revising its own estimate. The names simply never caught up with the decision.
--
-- The rename is safe: this table holds at most one row per job and is written on a fixed
-- interval, so the only thing a rename can disturb is a query written against the old names, and
-- both are changed in the same commit.
-- ===========================================================================================

ALTER TABLE imports.import_progress RENAME COLUMN rows_processed TO bytes_processed;
ALTER TABLE imports.import_progress RENAME COLUMN rows_expected  TO bytes_expected;

COMMENT ON COLUMN imports.import_progress.bytes_processed IS
    'Bytes of the source file consumed so far. Bytes and not rows: the row count is unknown until the file has been read, so a byte-based bar never has to revise its own estimate.';

COMMENT ON COLUMN imports.import_progress.bytes_expected IS
    'Size of the source file, which is what percent is computed against.';
