-- 006_worker_heartbeat.sql
--
-- Lets the UI say "no worker is running" instead of showing a job queued forever.
--
-- ===========================================================================================
-- WHY THIS EXISTS.
--
-- A 319.6 MB daily file was uploaded successfully. The Import Center showed it as Queued, with
-- rows read 0, attempt 0, and a progress panel that never appeared. Nothing was broken: the file
-- was stored, the hash was computed, the job row was written. There was simply no worker process
-- running to claim it, and NOTHING ANYWHERE SAID SO.
--
-- That is the worst shape a failure can take - everything reports success and nothing happens -
-- and it is not a development-only accident. A worker can crash, be killed by the OOM killer, or
-- be stopped by a deploy, and today the only symptom is a queue that stops moving.
--
-- The job table cannot answer this. It records worker_id and lease_expires_at, but only for jobs
-- that were CLAIMED. A job nobody ever claimed has no worker to ask about, which is exactly the
-- case that needs answering.
-- ===========================================================================================

CREATE TABLE IF NOT EXISTS imports.worker_heartbeat (
    -- Stable per worker: hostname plus process id by default, so restarting a worker on the same
    -- machine replaces its row rather than accumulating dead ones.
    worker_id        text        PRIMARY KEY,

    hostname         text        NOT NULL,

    -- How many jobs this worker will run at once. Shown so an operator can tell a saturated
    -- queue from an absent worker: three queued jobs behind one worker is patience, three queued
    -- behind zero workers is a problem.
    max_concurrent   smallint    NOT NULL,

    -- Whether it is sharing a process with the API, which changes what a restart costs and is
    -- worth seeing on the page rather than inferring from how the system was launched.
    hosted_in_api    boolean     NOT NULL DEFAULT false,

    started_at       timestamptz NOT NULL DEFAULT now(),
    last_seen_at     timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE imports.worker_heartbeat IS
    'One row per import worker, refreshed on every poll. Its absence is what tells the UI that a queued job has nobody to run it.';

-- Answering "is any worker alive" is a scan of a table with one or two rows, so this index is not
-- for speed. It is for the cleanup that removes long-dead workers, which orders by this column.
CREATE INDEX IF NOT EXISTS ix_worker_heartbeat_seen ON imports.worker_heartbeat (last_seen_at DESC);

-- No grants file needed: 003_least_privilege.sql set ALTER DEFAULT PRIVILEGES on schema imports,
-- so a table created here by the owner is already reachable by sqm_app. That is the difference
-- between this and the catalog schema in migration 005, which was a NEW schema and therefore had
-- no default privileges to inherit - and returned "permission denied for schema catalog" on the
-- first request because of it.
