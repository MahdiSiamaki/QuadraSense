-- 021_campaign_export.sql
--
-- The application role's grants on the `campaign` schema, added by migration 021.
--
-- A separate file for the reason 005_device_catalog.sql gives: migrations describe the shape of the
-- database and are immutable once applied; grants describe who may do what.
--
-- Run once per environment, AFTER migration 021, as the schema owner:
--
--   psql -U sqm -d sqm -f db/operational/grants/021_campaign_export.sql
--
-- Re-running is safe.

\set ON_ERROR_STOP on

GRANT USAGE ON SCHEMA campaign TO sqm_app;

-- No DELETE: a request is history. It is decided, withdrawn or left to expire, never removed by the
-- application.
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA campaign TO sqm_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA campaign TO sqm_app;

DO $$
DECLARE
    owner_role text := current_user;
BEGIN
    EXECUTE format(
        'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA campaign '
        'GRANT SELECT, INSERT, UPDATE ON TABLES TO sqm_app', owner_role);

    EXECUTE format(
        'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA campaign '
        'GRANT USAGE, SELECT ON SEQUENCES TO sqm_app', owner_role);
END
$$;
