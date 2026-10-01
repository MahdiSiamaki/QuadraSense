-- 013_explorer.sql
--
-- The application role's grants on the `explorer` schema, added by migration 013.
--
-- A separate file for the reason 005_device_catalog.sql gives: migrations describe the shape of the
-- database and are immutable once applied; grants describe who may do what. A new schema is
-- invisible to sqm_app until this file says otherwise - the good failure mode, a 500 on the first
-- request rather than an application quietly running as owner.
--
-- Run once per environment, AFTER migration 013, as the schema owner:
--
--   psql -U sqm -d sqm -f db/operational/grants/013_explorer.sql
--
-- Re-running is safe.

\set ON_ERROR_STOP on

GRANT USAGE ON SCHEMA explorer TO sqm_app;

-- Full CRUD: a person saves, renames, edits and deletes their own queries. Which rows are theirs is
-- the application's to enforce - every statement it issues is scoped to the owner.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA explorer TO sqm_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA explorer TO sqm_app;

DO $$
DECLARE
    owner_role text := current_user;
BEGIN
    EXECUTE format(
        'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA explorer '
        'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO sqm_app', owner_role);

    EXECUTE format(
        'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA explorer '
        'GRANT USAGE, SELECT ON SEQUENCES TO sqm_app', owner_role);
END
$$;
