-- 005_device_catalog.sql
--
-- The application role's grants on the `catalog` schema, added by migration 005.
--
-- A SEPARATE FILE, for the reason 003_least_privilege.sql gives: migrations describe the SHAPE of
-- the database and are checksummed and immutable once applied; grants describe WHO MAY DO WHAT,
-- which legitimately changes. A new schema needs a new grants file rather than an edit to an
-- applied migration.
--
-- Run once per environment, AFTER migration 005, as the schema owner:
--
--   psql -U sqm -d sqm -f db/operational/grants/005_device_catalog.sql
--
-- Re-running is safe.
--
-- ---------------------------------------------------------------------------------------------
-- WHY THIS FILE EXISTS AT ALL, which is the interesting part.
--
-- Migration 005 created `catalog.device_image` and the Devices module was tested and working
-- against it - as the schema owner. The first request through the running API returned
-- HTTP 500 and the log said:
--
--     42501: permission denied for schema catalog
--
-- That is the least-privilege model working exactly as designed. The application does not connect
-- as the owner; it connects as sqm_app, which holds USAGE on imports, auth and audit and on
-- nothing else. A new schema is invisible to it until somebody says otherwise, and "somebody says
-- otherwise" is this file.
--
-- It is worth recording because the failure mode is the good one: a schema nobody granted is a
-- schema nobody can read, and that was discovered by a 500 on the first request rather than by an
-- audit six months later finding the application had been running as owner all along.
-- ---------------------------------------------------------------------------------------------

\set ON_ERROR_STOP on

GRANT USAGE ON SCHEMA catalog TO sqm_app;

-- Full CRUD, unlike the audit tables. A device photograph is reference data that administrators
-- curate through the application: they add one, replace it when a better picture turns up, and
-- delete one uploaded by mistake. There is no append-only claim to protect here, and pretending
-- otherwise would mean an administrator could add a wrong image and never remove it.
--
-- The uploads are audited instead: every write goes through the API, which records who, which
-- device model, the media type and the byte count in audit.event - where UPDATE and DELETE really
-- are refused.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA catalog TO sqm_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA catalog TO sqm_app;

-- So a future table in this schema is reachable without anybody remembering to come back here.
-- FOR ROLE <owner> because default privileges apply to objects created BY a specific role, and
-- the owner is whoever runs the migrations - detected rather than assumed, exactly as
-- 003_least_privilege.sql does it.
DO $$
DECLARE
    owner_role text := current_user;
BEGIN
    EXECUTE format(
        'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA catalog '
        'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO sqm_app', owner_role);

    EXECUTE format(
        'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA catalog '
        'GRANT USAGE, SELECT ON SEQUENCES TO sqm_app', owner_role);
END
$$;
