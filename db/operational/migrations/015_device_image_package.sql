-- 015_device_image_package.sql
--
-- Candidates staged from a curated image package (the product owner's TAC_Catalog_Images
-- deliveries), and what a reviewer needs to get through a couple of thousand of them.
--
-- WHY NEW COLUMNS. A package maps TACs to images; the catalogue keys images by model (brand,
-- marketing name). So a candidate from a package carries evidence a web-sourced one does not: the
-- product name the package gave it, how it matched, and how many of the model's TACs and active
-- bindings that mapping covers - "Galaxy A14" has 200 TACs and the package maps one of them, with
-- no bindings, to a "Galaxy A14 5G" image. That evidence, and the warnings drawn from it and from
-- the pixels, are stored with the candidate rather than recomputed later against whatever the rules
-- have become.
--
-- WARNINGS, NOT REJECTIONS. ADR-011's pixel rules reject web-sourced candidates outright. On the
-- package they rejected 310 of 1,279 images, almost all wrongly (renders trimmed tight to the device
-- read as cropped). The product owner decided on 2026-10-06 that nothing from a package is
-- discarded: every image reaches review, each measurement shown as a warning, and a person decides.
--
-- bindings is the model's active bindings when the candidate was staged - a snapshot for ordering
-- the queue (most-used models first), not a live figure.

ALTER TABLE catalog.device_image_candidate
    ADD COLUMN IF NOT EXISTS bindings bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS warnings jsonb  NOT NULL DEFAULT '[]'::jsonb,
    ADD COLUMN IF NOT EXISTS evidence jsonb  NOT NULL DEFAULT '{}'::jsonb,
    ADD COLUMN IF NOT EXISTS package  text;

ALTER TABLE catalog.device_image_candidate
    ADD CONSTRAINT ck_candidate_bindings CHECK (bindings >= 0),
    ADD CONSTRAINT ck_candidate_warnings CHECK (jsonb_typeof(warnings) = 'array'),
    ADD CONSTRAINT ck_candidate_evidence CHECK (jsonb_typeof(evidence) = 'object');

-- The queue's default order: the models people actually carry first.
CREATE INDEX IF NOT EXISTS ix_candidate_bindings
    ON catalog.device_image_candidate (status, bindings DESC, quality_score DESC, id);
