-- ---------------------------------------------------------------------------
-- A device photograph belongs to a MODEL, not to a TAC.
--
-- catalog.device_image was keyed by TAC, and that key was wrong in a way that
-- only shows up against real data. A marketing name maps to many TACs:
--
--     Redmi Note 12S    18 TACs
--     Redmi Note 13     38 TACs
--     Galaxy A12       184 TACs
--
-- so an administrator who uploaded a picture of a Redmi Note 12S saw it on one
-- device page and the drawn placeholder on the other seventeen - the same phone,
-- the same screen, no picture. Covering one model meant 18 uploads of identical
-- bytes, and replacing it later meant finding all 18 again or leaving the rest
-- silently stale. Keyed by model it is one row, one upload, one replacement.
--
-- The key is (brand, marketing name), normalised. Brand rather than manufacturer
-- because the GSMA record carries both and they differ in the way that matters
-- here: manufacturer is "Xiaomi Communications Co Ltd" while brand is "Redmi",
-- and it is the brand a person recognises on the handset.
--
-- The TAC -> model mapping is NOT resolvable here. That dimension lives in
-- ClickHouse and this database cannot see it, so the two rows that exist are
-- carried over by their resolved keys, written out rather than computed. Both
-- were checked against sqm.tac at the time of writing:
--
--     86033006 -> Redmi   | Redmi Note 12S   (a real Xiaomi product photograph)
--     35004012 -> Samsung | Galaxy A54 5g    (a 179-byte synthetic test upload)
--
-- Anything else in the table would be lost, so the migration refuses to run if
-- it finds a row it was not written to carry.
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS catalog.device_model_image (
    -- Normalised identity: lower(brand) || '|' || lower(marketing name), both
    -- trimmed and internally whitespace-collapsed. Computed by the application,
    -- which is the only side that can see the TAC dimension, and constrained
    -- here to the shape that implies.
    model_key       text        PRIMARY KEY
                    CONSTRAINT ck_model_image_key
                    CHECK (model_key = lower(model_key) AND model_key LIKE '%|%'),

    -- Kept alongside the key so this table is readable on its own. A key is for
    -- joining; a person reading a backup needs the words.
    brand           text        NOT NULL,
    marketing_name  text        NOT NULL,

    content_type    text        NOT NULL
                    CONSTRAINT ck_model_image_type
                    CHECK (content_type IN ('image/png', 'image/jpeg', 'image/webp')),

    bytes           bytea       NOT NULL
                    CONSTRAINT ck_model_image_size
                    CHECK (octet_length(bytes) > 0 AND octet_length(bytes) <= 524288),

    sha256          bytea       NOT NULL
                    CONSTRAINT ck_model_image_sha CHECK (octet_length(sha256) = 32),

    -- Where the picture came from. Not decoration: images are now sourced in
    -- bulk as well as uploaded by hand, and an image whose origin and licence
    -- nobody recorded is one nobody can defend later.
    source_note     text        NOT NULL DEFAULT '',

    uploaded_by     bigint      NOT NULL REFERENCES auth.user_account (id),
    uploaded_at     timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE catalog.device_model_image IS
    'One curated photograph per device model, keyed by brand and marketing name. Covers every TAC of that model.';

CREATE INDEX IF NOT EXISTS ix_model_image_updated
    ON catalog.device_model_image (updated_at DESC);

-- --------------------------------------------------------------------- carry over

DO $$
DECLARE
    stray text;
BEGIN
    IF to_regclass('catalog.device_image') IS NULL THEN
        RETURN;
    END IF;

    SELECT string_agg(tac, ', ' ORDER BY tac) INTO stray
    FROM catalog.device_image
    WHERE tac NOT IN ('86033006', '35004012');

    IF stray IS NOT NULL THEN
        RAISE EXCEPTION
            'catalog.device_image holds rows this migration was not written to carry: %. '
            'Resolve each TAC to its (brand, marketing name) against sqm.tac and add it above '
            'rather than losing the image.', stray;
    END IF;

    INSERT INTO catalog.device_model_image
        (model_key, brand, marketing_name, content_type, bytes, sha256,
         source_note, uploaded_by, uploaded_at, updated_at)
    SELECT
        CASE i.tac
            WHEN '86033006' THEN 'redmi|redmi note 12s'
            WHEN '35004012' THEN 'samsung|galaxy a54 5g'
        END,
        CASE i.tac WHEN '86033006' THEN 'Redmi'          ELSE 'Samsung'       END,
        CASE i.tac WHEN '86033006' THEN 'Redmi Note 12S' ELSE 'Galaxy A54 5g' END,
        i.content_type, i.bytes, i.sha256,
        CASE WHEN i.source_note = ''
             THEN 'carried over from the TAC-keyed table; original provenance not recorded'
             ELSE i.source_note END,
        i.uploaded_by, i.uploaded_at, i.updated_at
    FROM catalog.device_image AS i
    ON CONFLICT (model_key) DO NOTHING;

    DROP TABLE catalog.device_image;
END
$$;
