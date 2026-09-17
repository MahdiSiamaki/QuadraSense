-- ---------------------------------------------------------------------------
-- A device image gets a lifecycle, and a sourced image stops pretending to be
-- a verified one.
--
-- catalog.device_model_image had no status: a picture either existed or it did
-- not. So an image pulled off Wikipedia by a script and an image an
-- administrator chose and uploaded were the same thing to every reader of this
-- table, and 84 of the 87 rows in it are the former, none ever reviewed.
--
-- This adds two things and destroys nothing:
--
--   1. Status and provenance on the live table. Existing rows are classified
--      from the source note they already carry, so the 84 auto-sourced images
--      surface as needing review rather than being silently blessed.
--
--   2. catalog.device_image_candidate - a staging area. Sourcing writes here
--      and NEVER to the live table. A candidate becomes the live image only
--      when a reviewer approves it, which is the safety property the whole
--      exercise turns on: the current image stays active until a human says
--      otherwise.
--
-- Migrations 005 and 008 are untouched.
-- ---------------------------------------------------------------------------

-- --------------------------------------------------------------- live table

ALTER TABLE catalog.device_model_image
    ADD COLUMN IF NOT EXISTS status text NOT NULL DEFAULT 'verified',
    ADD COLUMN IF NOT EXISTS source_type text NOT NULL DEFAULT 'manual',
    ADD COLUMN IF NOT EXISTS source_domain text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS source_url text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS original_width int,
    ADD COLUMN IF NOT EXISTS original_height int,
    ADD COLUMN IF NOT EXISTS quality_score int,
    ADD COLUMN IF NOT EXISTS verified_by bigint REFERENCES auth.user_account (id),
    ADD COLUMN IF NOT EXISTS verified_at timestamptz;

DO $$
BEGIN
    -- Two states only for a LIVE image, because only two are meaningful once a
    -- picture is being served: a human vouched for it, or nobody has yet.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_model_image_status')
    THEN
        ALTER TABLE catalog.device_model_image
            ADD CONSTRAINT ck_model_image_status
            CHECK (status IN ('verified', 'needs_review'));
    END IF;
END
$$;

-- Classify what is already there from its own provenance note. An image whose
-- note names Wikimedia was written by the sourcing script and has never been
-- looked at; anything else was uploaded by a person through the API.
UPDATE catalog.device_model_image
   SET status = 'needs_review',
       source_type = 'wikimedia',
       source_domain = 'commons.wikimedia.org'
 WHERE source_note LIKE 'Wikimedia Commons:%'
   AND status = 'verified';

COMMENT ON COLUMN catalog.device_model_image.status IS
    'verified = a reviewer approved this image. needs_review = it was sourced automatically and nobody has confirmed it.';

-- ---------------------------------------------------------------- candidates

CREATE TABLE IF NOT EXISTS catalog.device_image_candidate (
    id              bigserial   PRIMARY KEY,

    -- Same key as the live table: an image belongs to a model, not a TAC.
    model_key       text        NOT NULL
                    CONSTRAINT ck_candidate_key
                    CHECK (model_key = lower(model_key) AND model_key LIKE '%|%'),

    brand           text        NOT NULL,
    marketing_name  text        NOT NULL,

    status          text        NOT NULL DEFAULT 'needs_review'
                    CONSTRAINT ck_candidate_status
                    CHECK (status IN ('needs_review', 'approved', 'rejected', 'failed')),

    content_type    text        NOT NULL
                    CONSTRAINT ck_candidate_type
                    CHECK (content_type IN ('image/png', 'image/jpeg', 'image/webp')),

    bytes           bytea       NOT NULL
                    CONSTRAINT ck_candidate_size
                    CHECK (octet_length(bytes) > 0 AND octet_length(bytes) <= 524288),

    sha256          bytea       NOT NULL
                    CONSTRAINT ck_candidate_sha CHECK (octet_length(sha256) = 32),

    -- Provenance, in the pieces a reviewer actually needs to judge it. The
    -- domain is stored separately from the URL so "which sources are we
    -- accepting" is a GROUP BY rather than a string search.
    source_type     text        NOT NULL,
    source_domain   text        NOT NULL,
    source_url      text        NOT NULL,

    original_width  int         NOT NULL,
    original_height int         NOT NULL,
    original_bytes  int         NOT NULL,

    -- The score and every term that produced it. Stored rather than recomputed
    -- so a reviewer sees the reasoning that existed when the candidate was
    -- made, not the reasoning of whatever the rules have since become.
    quality_score   int         NOT NULL,
    score_breakdown jsonb       NOT NULL DEFAULT '[]'::jsonb,

    rejection_reason text,

    created_at      timestamptz NOT NULL DEFAULT now(),
    last_checked_at timestamptz NOT NULL DEFAULT now(),
    reviewed_by     bigint      REFERENCES auth.user_account (id),
    reviewed_at     timestamptz
);

COMMENT ON TABLE catalog.device_image_candidate IS
    'Proposed device images awaiting review. Sourcing writes here and never to device_model_image; a candidate reaches the live table only when a reviewer approves it.';

-- One proposal per model per distinct image. Re-running the sourcing tool must
-- be idempotent: proposing the same bytes again is not a new candidate, and a
-- candidate a reviewer already REJECTED must not come back on the next run
-- unless the image itself is different.
CREATE UNIQUE INDEX IF NOT EXISTS ux_candidate_model_sha
    ON catalog.device_image_candidate (model_key, sha256);

-- The review queue, which is the only listing this table has.
CREATE INDEX IF NOT EXISTS ix_candidate_queue
    ON catalog.device_image_candidate (status, quality_score DESC, created_at);
