-- 017_cancel_until_loading_ends.sql
--
-- An import can be cancelled until the point where stopping it would leave something half-done,
-- and not after (docs/architecture/09-import-platform.md, D12).
--
-- Until 2026-10-26 a running job was checked for a cancel at one place only - a daily file just
-- after validation, a TAC file never - and a cancel that came later was accepted with 202 and then
-- ignored: the job completed and the screen had said it was stopping.
--
-- WHERE THAT POINT IS BELONGS TO THE PROCESSOR, not to a list of stage names here. A daily file
-- passes it on entering AGGREGATING, where the day is folded into the history and the marts and
-- stopping halfway would leave them half-updated. A TAC file passes it only on FINALIZING, where
-- the version is registered: until then it is a partition nothing reads, and dropping it is safe.
-- So the processor crosses the point with one statement that also checks for a cancel, and the
-- cancel endpoint reads this column - which makes the two exclusive: a cancel either lands before
-- the point and the job stops, or is refused after it, never accepted and ignored.
--
-- True for every attempt until its processor crosses the point; a retry starts from the beginning
-- and is cancellable again.
ALTER TABLE imports.import_job
    ADD COLUMN IF NOT EXISTS cancellable boolean NOT NULL DEFAULT true;

COMMENT ON COLUMN imports.import_job.cancellable IS
    'False once the running attempt has passed the point where stopping would leave work half-done '
    '(AGGREGATING for a daily file, FINALIZING for a TAC file). Reset at each claim.';
