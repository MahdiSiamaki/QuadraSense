-- ---------------------------------------------------------------------------
-- The dashboard snapshot is rebuilt once per run of queued files, not once per file.
--
-- Every daily import ends by rebuilding the dashboard snapshot for the delivery holding the
-- latest date: 14 full scans of binding_current, ~31 minutes (system.query_log, 31 August).
-- After a late or corrected day that is the SAME delivery every time, and each rebuild is made
-- stale by the next file in the queue. For the ~27 files expected from the operator that is about
-- 14 hours of rebuilds whose results nobody would see, with the dashboard falling back to the
-- previous delivery during each one.
--
-- So an import with more files of its source queued behind it skips the rebuild and records here
-- that the snapshot is owed. The last file of the run rebuilds it. And when the worker has
-- nothing it can claim - the queue is empty, or blocked behind a failed day - it settles any debt
-- left, so a run that stops part way can never leave the dashboard behind its data.
--
-- One row per source, present only while a rebuild is owed. owed_since moves forward every time
-- another file defers, and a rebuild clears only the debt it saw before it began: a file that
-- deferred while the rebuild was running keeps its row, and is rebuilt for.
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS imports.dashboard_refresh_owed (
    -- A debt for a source that no longer exists means nothing, hence the cascade.
    source_code  text        PRIMARY KEY
                             REFERENCES imports.data_source (code) ON DELETE CASCADE,

    owed_since   timestamptz NOT NULL DEFAULT now(),

    -- The job that last deferred, for the Import Center to point at. Not a foreign key: jobs are
    -- history and may be removed, and a debt must not be removed with them.
    last_job_id  bigint,

    reason       text        NOT NULL
);
