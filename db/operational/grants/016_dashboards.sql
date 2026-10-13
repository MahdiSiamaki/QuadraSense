-- 016_dashboards.sql
--
-- The application role's grants on the `dashboard` schema, added by migration 016.
--
-- A separate file for the reason 005_device_catalog.sql gives: migrations describe the shape of the
-- database and are immutable once applied; grants describe who may do what. A new schema is
-- invisible to sqm_app until this file says otherwise - the good failure mode, a 500 on the first
-- request rather than an application quietly running as owner.
--
-- Run once per environment, AFTER migration 016, as the schema owner:
--
--   psql -U sqm -d sqm -f db/operational/grants/016_dashboards.sql
--
-- Re-running is safe.

\set ON_ERROR_STOP on

GRANT USAGE ON SCHEMA dashboard TO sqm_app;

-- Full CRUD: people create, change, share and delete dashboards. Whose rows a statement may touch is
-- the application's to enforce - every statement it issues names the person and their access.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA dashboard TO sqm_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA dashboard TO sqm_app;

DO $$
DECLARE
    owner_role text := current_user;
BEGIN
    EXECUTE format(
        'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA dashboard '
        'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO sqm_app', owner_role);

    EXECUTE format(
        'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA dashboard '
        'GRANT USAGE, SELECT ON SEQUENCES TO sqm_app', owner_role);
END
$$;
