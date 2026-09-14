-- 001_import_platform.sql
--
-- The import platform's operational schema: job queue, file registry, versioning, quarantine
-- and audit. PostgreSQL, per ADR-002.
--
-- This schema IS the queue. Claiming a job and recording that it was claimed are one
-- transaction, so the queue and the audit trail cannot disagree - which is the reason a
-- broker was rejected (see docs/architecture/09-import-platform.md §3).

CREATE SCHEMA IF NOT EXISTS imports;

-- ---------------------------------------------------------------------------
-- Data sources. A row per kind of file the platform accepts.
-- Extensible by insert, not by deployment.
-- ---------------------------------------------------------------------------
CREATE TABLE imports.data_source (
    code                text PRIMARY KEY,           -- 'TAC', 'SQM'
    display_name        text        NOT NULL,
    description         text,
    -- How a business date is obtained. Neither source carries one in its rows.
    business_date_source text       NOT NULL
        CHECK (business_date_source IN ('filename', 'content', 'none')),
    -- Regex capturing the business date from the filename.
    filename_date_regex text,
    -- What a second file for the same business date means.
    revision_strategy   text        NOT NULL
        CHECK (revision_strategy IN ('replace_by_date', 'snapshot_version', 'append')),
    max_file_bytes      bigint      NOT NULL DEFAULT 10737418240,
    is_enabled          boolean     NOT NULL DEFAULT true
);

COMMENT ON COLUMN imports.data_source.revision_strategy IS
    'replace_by_date: each file is a complete statement of one day (SQM). '
    'snapshot_version: each file is a full dataset superseding the last (TAC). '
    'Chosen from the data''s semantics, not by preference - see design doc §6.';

INSERT INTO imports.data_source
    (code, display_name, description, business_date_source, filename_date_regex, revision_strategy)
VALUES
    ('SQM', 'Subscriber device/SIM daily changes',
     'One day of add/remove changes to device-SIM bindings.',
     'filename', '(\d{4}-\d{2}-\d{2})', 'replace_by_date'),
    ('TAC', 'GSMA TAC device database',
     'Full snapshot of the GSMA type allocation code database.',
     'filename', '(\d{1,2}[A-Za-z]{3}\d{4})', 'snapshot_version')
ON CONFLICT (code) DO NOTHING;

-- ---------------------------------------------------------------------------
-- Schema versions. The column contract a file must match.
-- ---------------------------------------------------------------------------
CREATE TABLE imports.schema_version (
    id              serial PRIMARY KEY,
    source_code     text        NOT NULL REFERENCES imports.data_source(code),
    version         text        NOT NULL,               -- 'v1'
    -- Ordered column names. Order matters: a reorder with a correct header would
    -- otherwise pass unnoticed.
    columns         text[]      NOT NULL,
    -- Hash of the normalised header, for O(1) matching on upload.
    header_hash     text        NOT NULL,
    is_current      boolean     NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (source_code, version)
);

CREATE UNIQUE INDEX ux_schema_version_header
    ON imports.schema_version (source_code, header_hash);

-- ---------------------------------------------------------------------------
-- Uploaded files. Immutable once written.
--
-- The file is identified by the SHA-256 of its CONTENT, never its name. A rename is not a
-- new file, and the same name with different content is not the same file.
-- ---------------------------------------------------------------------------
CREATE TABLE imports.import_file (
    id                  bigserial PRIMARY KEY,
    source_code         text        NOT NULL REFERENCES imports.data_source(code),
    original_file_name  text        NOT NULL,
    -- Generated name under the storage root. The client-supplied name is never used as a
    -- path component, which removes path traversal as a category.
    stored_path         text        NOT NULL UNIQUE,
    file_bytes          bigint      NOT NULL CHECK (file_bytes > 0),
    sha256              char(64)    NOT NULL,
    uploaded_by         text        NOT NULL,
    uploaded_at         timestamptz NOT NULL DEFAULT now(),
    -- Retained after the blob is deleted, so history stays explainable.
    is_blob_present     boolean     NOT NULL DEFAULT true,
    blob_deleted_at     timestamptz,
    blob_deleted_by     text
);

-- Content identity. This single constraint is what makes duplicate detection a database
-- guarantee rather than an application check that a race can defeat.
CREATE UNIQUE INDEX ux_import_file_sha ON imports.import_file (source_code, sha256);
CREATE INDEX ix_import_file_uploaded ON imports.import_file (uploaded_at DESC);

-- ---------------------------------------------------------------------------
-- Import jobs. One row per attempt, and also the queue.
-- ---------------------------------------------------------------------------
CREATE TYPE imports.job_status AS ENUM (
    'UPLOADED', 'QUEUED', 'VALIDATING', 'PARSING', 'NORMALIZING', 'DEDUPLICATING',
    'ENRICHING', 'IMPORTING', 'AGGREGATING', 'FINALIZING',
    'COMPLETED', 'PARTIALLY_COMPLETED', 'DUPLICATE',
    'FAILED', 'QUARANTINED', 'CANCELLED', 'RETRYING'
);

COMMENT ON TYPE imports.job_status IS
    'One enum, not a scatter of booleans: is_running + is_failed + is_complete admits '
    'states like "running and failed" that mean nothing. DUPLICATE is a terminal '
    'success-ish state - nothing was wrong, the work was already done.';

CREATE TABLE imports.import_job (
    id                  bigserial PRIMARY KEY,
    source_code         text        NOT NULL REFERENCES imports.data_source(code),
    file_id             bigint      NOT NULL REFERENCES imports.import_file(id),
    schema_version_id   integer     REFERENCES imports.schema_version(id),

    status              imports.job_status NOT NULL DEFAULT 'UPLOADED',
    -- Business date parsed from the filename. Null until validation reads it.
    business_date       date,
    -- 1 for the first accepted file for a business date, 2 for a correction, and so on.
    revision            integer     NOT NULL DEFAULT 1,
    -- False once a later revision for the same business date supersedes this one.
    is_effective        boolean     NOT NULL DEFAULT false,

    -- Queue mechanics -------------------------------------------------------
    priority            smallint    NOT NULL DEFAULT 0,
    attempt             smallint    NOT NULL DEFAULT 0,
    max_attempts        smallint    NOT NULL DEFAULT 3,
    run_after           timestamptz,
    worker_id           text,
    -- A running job holds a lease it must renew. If the worker dies the lease expires and a
    -- sweeper requeues the job - which is why nothing is held only in worker memory.
    lease_expires_at    timestamptz,

    -- Resumability ----------------------------------------------------------
    current_stage       text,
    stage_started_at    timestamptz,
    -- Rows already durably applied. A resumed job restarts from here, not from zero.
    rows_committed      bigint      NOT NULL DEFAULT 0,

    -- Counters --------------------------------------------------------------
    rows_input          bigint      NOT NULL DEFAULT 0,
    rows_valid          bigint      NOT NULL DEFAULT 0,
    rows_invalid        bigint      NOT NULL DEFAULT 0,
    rows_inserted       bigint      NOT NULL DEFAULT 0,
    rows_updated        bigint      NOT NULL DEFAULT 0,
    rows_duplicate      bigint      NOT NULL DEFAULT 0,
    rows_rejected       bigint      NOT NULL DEFAULT 0,
    warning_count       integer     NOT NULL DEFAULT 0,
    error_count         integer     NOT NULL DEFAULT 0,

    -- Timing ----------------------------------------------------------------
    created_at          timestamptz NOT NULL DEFAULT now(),
    started_at          timestamptz,
    finished_at         timestamptz,
    duration_ms         bigint GENERATED ALWAYS AS (
        CASE WHEN finished_at IS NOT NULL AND started_at IS NOT NULL
             THEN EXTRACT(EPOCH FROM (finished_at - started_at))::bigint * 1000 END
    ) STORED,

    -- Lineage and context ---------------------------------------------------
    reprocess_of_job_id bigint      REFERENCES imports.import_job(id),
    supersedes_job_id   bigint      REFERENCES imports.import_job(id),
    created_by          text        NOT NULL,
    cancel_requested    boolean     NOT NULL DEFAULT false,
    error_summary       text,
    metadata            jsonb       NOT NULL DEFAULT '{}'::jsonb
);

-- The queue read path: find claimable work cheaply.
CREATE INDEX ix_import_job_claimable ON imports.import_job (priority DESC, created_at)
    WHERE status IN ('QUEUED', 'RETRYING');

-- The sweeper's read path: find jobs whose worker died.
CREATE INDEX ix_import_job_lease ON imports.import_job (lease_expires_at)
    WHERE lease_expires_at IS NOT NULL;

CREATE INDEX ix_import_job_history ON imports.import_job (created_at DESC);
CREATE INDEX ix_import_job_business_date ON imports.import_job (source_code, business_date);

-- At most one effective revision per business date per source. A database guarantee, not an
-- application convention: two workers racing to apply corrections for the same day cannot
-- both win.
CREATE UNIQUE INDEX ux_import_job_effective
    ON imports.import_job (source_code, business_date)
    WHERE is_effective;

-- ---------------------------------------------------------------------------
-- Job events. The timeline the UI shows, generated by the worker as it works.
-- ---------------------------------------------------------------------------
CREATE TABLE imports.import_event (
    id          bigserial PRIMARY KEY,
    job_id      bigint      NOT NULL REFERENCES imports.import_job(id) ON DELETE CASCADE,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    severity    text        NOT NULL CHECK (severity IN ('info', 'warning', 'error')),
    stage       text,
    message     text        NOT NULL,
    detail      jsonb
);

CREATE INDEX ix_import_event_job ON imports.import_event (job_id, occurred_at);

-- ---------------------------------------------------------------------------
-- Progress. Written separately from import_job and updated far more often.
--
-- Kept out of import_job on purpose: progress ticks many times a minute during a large file,
-- and updating a wide row that often would churn it needlessly. This table is narrow, so an
-- update touches little.
-- ---------------------------------------------------------------------------
CREATE TABLE imports.import_progress (
    job_id          bigint      PRIMARY KEY REFERENCES imports.import_job(id) ON DELETE CASCADE,
    stage           text        NOT NULL,
    rows_processed  bigint      NOT NULL DEFAULT 0,
    rows_expected   bigint,
    percent         numeric(5,2),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- Quarantine, aggregated.
--
-- A wholly malformed 8M-row file would otherwise write 8M error rows and make error handling
-- the storage problem. Errors are grouped by (rule, column) with a capped sample of raw rows.
-- ---------------------------------------------------------------------------
CREATE TABLE imports.quarantine_rule_summary (
    id              bigserial PRIMARY KEY,
    job_id          bigint      NOT NULL REFERENCES imports.import_job(id) ON DELETE CASCADE,
    rule_code       text        NOT NULL,
    column_name     text,
    severity        text        NOT NULL,
    occurrence_count bigint     NOT NULL,
    first_row_number bigint,
    message         text        NOT NULL,
    UNIQUE (job_id, rule_code, column_name)
);

CREATE TABLE imports.quarantine_sample (
    id              bigserial PRIMARY KEY,
    summary_id      bigint      NOT NULL REFERENCES imports.quarantine_rule_summary(id) ON DELETE CASCADE,
    row_number      bigint      NOT NULL,
    raw_line        text        NOT NULL,
    offending_value text
);

CREATE INDEX ix_quarantine_sample_summary ON imports.quarantine_sample (summary_id);

-- ---------------------------------------------------------------------------
-- TAC dataset versions, with the activation lifecycle.
--
-- Uploading does not change what the dashboard shows. Activation is a separate, audited act,
-- because TAC determines the manufacturer and model on every screen (decision D1).
-- ---------------------------------------------------------------------------
CREATE TYPE imports.tac_version_status AS ENUM
    ('DRAFT', 'PROCESSING', 'READY', 'ACTIVE', 'SUPERSEDED', 'FAILED');

CREATE TABLE imports.tac_version (
    id                  bigserial PRIMARY KEY,
    job_id              bigint      NOT NULL REFERENCES imports.import_job(id),
    -- Human label taken from the filename, e.g. 'v2026.09.01'.
    version_label       text        NOT NULL UNIQUE,
    -- The dataset's own date, corroborated against max(lastUpdatedDate) in the file.
    dataset_date        date,
    status              imports.tac_version_status NOT NULL DEFAULT 'DRAFT',
    row_count           integer,

    -- Diff against the previous version. Expected magnitude ~1,000-1,650 rows/month; an
    -- order of magnitude more is itself a signal worth stopping on.
    diff_against_id     bigint      REFERENCES imports.tac_version(id),
    tacs_added          integer,
    tacs_updated        integer,
    tacs_removed        integer,
    tacs_unchanged      integer,

    created_at          timestamptz NOT NULL DEFAULT now(),
    activated_at        timestamptz,
    activated_by        text,
    superseded_at       timestamptz
);

-- Exactly one active version, enforced by the database.
CREATE UNIQUE INDEX ux_tac_version_active ON imports.tac_version ((status = 'ACTIVE'))
    WHERE status = 'ACTIVE';

-- ---------------------------------------------------------------------------
-- Audit. Append-only by grant, not by convention: the writing role has INSERT and nothing
-- else, so no application path can rewrite history.
-- ---------------------------------------------------------------------------
CREATE TABLE imports.import_audit (
    id          bigserial PRIMARY KEY,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    actor       text        NOT NULL,
    action      text        NOT NULL,
    job_id      bigint,
    file_id     bigint,
    tac_version_id bigint,
    source_ip   inet,
    correlation_id text,
    detail      jsonb
);

CREATE INDEX ix_import_audit_time ON imports.import_audit (occurred_at DESC);
CREATE INDEX ix_import_audit_job ON imports.import_audit (job_id) WHERE job_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- Expected business dates, for missing-day detection.
--
-- A calendar of what should have arrived, so a gap is detectable without inferring intent
-- from absence. Seeded from the observed range; extended as deliveries continue.
-- ---------------------------------------------------------------------------
CREATE TABLE imports.expected_business_date (
    source_code   text NOT NULL REFERENCES imports.data_source(code),
    business_date date NOT NULL,
    note          text,
    PRIMARY KEY (source_code, business_date)
);
