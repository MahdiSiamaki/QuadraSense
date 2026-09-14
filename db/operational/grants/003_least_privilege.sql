-- 003_least_privilege.sql
--
-- The application's database role, and the grants that make the audit log evidence.
--
-- WHY THIS IS A SEPARATE FILE, not a migration. Migrations describe the SHAPE of the database and
-- are checksummed and immutable once applied. Grants describe WHO MAY DO WHAT, and that is an
-- operational fact which legitimately changes as processes are added or split. Mixing them means
-- a routine privilege change forces a new migration, or - worse - an edit to an applied one.
--
-- Run once per environment, AFTER migrations, as the schema owner (which must also be able to
-- create a role):
--
--   psql -U sqm -d sqm -v app_password="a-strong-password" \
--        -f db/operational/grants/003_least_privilege.sql
--
-- Re-running is safe, and re-running with a different password rotates it.
--
-- ---------------------------------------------------------------------------------------------
-- WHAT THIS ACTUALLY BUYS
--
-- docs/architecture/04-security-model.md section 8 says the audit log is "append-only by grant,
-- not by convention". Until this file existed, that was not true: the application connected as
-- the schema owner and could have rewritten any audit row. The claim was aspirational and read
-- as a statement of fact, which is the worst kind of security documentation.
--
-- After this runs, the application role holds INSERT and SELECT on the audit tables and nothing
-- else. UPDATE and DELETE are refused by PostgreSQL, so the audit trail survives a bug in the
-- application, a compromised API process, and anyone holding the application's password. It does
-- not survive the owner's or a superuser's credentials - nothing at this layer does, and
-- pretending otherwise would be the same mistake again.
--
-- Verified, not assumed: backend/tests/Sqm.Integration.Tests/AuditAppendOnlyTests.cs connects as
-- this role and asserts that UPDATE and DELETE on audit.event are refused.
-- ---------------------------------------------------------------------------------------------

\set ON_ERROR_STOP on

\if :{?app_password}
\else
\warn 'pass the application role password: -v app_password="..."'
\quit
\endif

-- The application role. One role for the API and the ingestion worker: they share
-- PostgresImportJobRepository, so splitting them would mean two sets of grants over the same code
-- paths and a guarantee that only holds while nobody calls the wrong method. If those processes
-- are ever separated properly, this is where that starts.
SELECT format('CREATE ROLE sqm_app LOGIN PASSWORD %L', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sqm_app')
\gexec

SELECT format('ALTER ROLE sqm_app WITH LOGIN PASSWORD %L', :'app_password')
\gexec

-- Connection and schema visibility.
SELECT format('GRANT CONNECT ON DATABASE %I TO sqm_app', current_database())
\gexec

GRANT USAGE ON SCHEMA imports, auth, audit TO sqm_app;

-- ---------------------------------------------------------------------------
-- imports: full DML. The import platform owns this schema's data lifecycle.
-- No DDL: the application cannot create, alter or drop anything. Schema change
-- is the migrator's job, and the migrator connects as the owner.
-- ---------------------------------------------------------------------------
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA imports TO sqm_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA imports TO sqm_app;

-- ...except the audit table, which is append-only like the other one.
REVOKE UPDATE, DELETE, TRUNCATE ON imports.import_audit FROM sqm_app;

-- ---------------------------------------------------------------------------
-- auth: full DML. User and role administration is a normal application feature,
-- and every change to it lands in the audit log that the application cannot edit.
-- ---------------------------------------------------------------------------
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA auth TO sqm_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA auth TO sqm_app;

-- ---------------------------------------------------------------------------
-- audit: INSERT and SELECT only. This is the whole point of the file.
-- ---------------------------------------------------------------------------
GRANT SELECT, INSERT ON audit.event TO sqm_app;
GRANT USAGE, SELECT ON SEQUENCE audit.event_id_seq TO sqm_app;
REVOKE UPDATE, DELETE, TRUNCATE ON audit.event FROM sqm_app;

-- The unified read view. A view executes with its owner's privileges, which is fine here: the
-- caller has SELECT on both underlying tables anyway, and the endpoint behind it requires the
-- audit.view permission.
GRANT SELECT ON audit.event_log TO sqm_app;

-- ---------------------------------------------------------------------------
-- Future tables. Without this, every new migration silently creates a table the
-- application cannot touch, and the failure surfaces as a 500 in production
-- rather than as an error at deploy time.
--
-- FOR ROLE matters: default privileges attach to the role that CREATES the
-- object, so they must name the migrator's role, not whoever runs this script.
-- Detected rather than hardcoded, so this is correct in an environment whose
-- owner is not called `sqm`.
-- ---------------------------------------------------------------------------
SELECT format(
    'ALTER DEFAULT PRIVILEGES FOR ROLE %s IN SCHEMA imports, auth '
    'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO sqm_app',
    nspowner::regrole)
FROM pg_namespace WHERE nspname = 'imports'
\gexec

SELECT format(
    'ALTER DEFAULT PRIVILEGES FOR ROLE %s IN SCHEMA imports, auth '
    'GRANT USAGE, SELECT ON SEQUENCES TO sqm_app',
    nspowner::regrole)
FROM pg_namespace WHERE nspname = 'imports'
\gexec

-- Deliberately NOT extended to the audit schema. A new table there should have its grants stated
-- explicitly, because the default that is right for the rest of the database is the wrong default
-- for an audit trail.

-- ---------------------------------------------------------------------------
-- What is NOT granted, listed so the absence is visible rather than implied:
--   - No CREATE on any schema. The application cannot add a table.
--   - No ownership of anything. The application cannot ALTER or DROP.
--   - No access to the migrator's history table.
--   - No SUPERUSER, CREATEDB, CREATEROLE, REPLICATION or BYPASSRLS.
-- ---------------------------------------------------------------------------

\echo 'granted. verify with: \\dp audit.event'
