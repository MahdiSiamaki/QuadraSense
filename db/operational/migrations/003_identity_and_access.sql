-- 003_identity_and_access.sql
--
-- Authentication, authorisation and audit. PostgreSQL, per ADR-002 and ADR-006.
--
-- Two new schemas rather than one, because they have different lifetimes and different grants:
--
--   auth   - who exists, what they may do, and which sessions are live. Read on every request.
--   audit  - what happened. Append-only by GRANT, never updated or deleted by any application path.
--
-- The import platform's own audit table (imports.import_audit) is left where it is. It has
-- columns this one does not (job_id, file_id, tac_version_id) and working, tested code writing
-- to it. A view at the bottom of this file presents both as one stream, which is what the Audit
-- Log page reads - one page, one query, no migration of live data.

CREATE SCHEMA IF NOT EXISTS auth;
CREATE SCHEMA IF NOT EXISTS audit;

-- ===========================================================================
-- PERMISSIONS
--
-- The catalogue is a TABLE, not a PostgreSQL enum and not a C# enum compiled
-- into the binary. Adding a capability is an INSERT in a migration; it does not
-- require a type change, nor a frontend deployment for the permission merely to
-- be listed, granted and displayed.
--
-- The code IS the primary key. An integer surrogate would buy nothing - this
-- table holds tens of rows, never thousands - and would cost readability in
-- every join, every audit entry and every debugging session.
--
-- Codes are `resource.action`, deliberately matching the action strings already
-- written to imports.import_audit ('import.upload', 'tac.activate'), so a
-- permission and the audit entry it authorises read the same.
-- ===========================================================================
CREATE TABLE auth.permission (
    code          text PRIMARY KEY,
    -- Groups the permission matrix in the UI. Free text on purpose: a new
    -- category is an INSERT, not a schema change.
    category      text        NOT NULL,
    display_name  text        NOT NULL,
    description   text        NOT NULL,
    -- Shown with a warning in the UI and highlighted in the audit log. Not a
    -- security control - it changes how a grant is presented, never whether it
    -- is enforced.
    is_dangerous  boolean     NOT NULL DEFAULT false,
    sort_order    smallint    NOT NULL DEFAULT 0
);

COMMENT ON TABLE auth.permission IS
    'The catalogue of capabilities. Extensible by INSERT in a migration. Every code here must '
    'have a matching constant in Sqm.Application.Identity.Permissions - a test asserts both '
    'directions, so a permission cannot exist in the database without code able to require it, '
    'nor in code without a row able to grant it.';

INSERT INTO auth.permission (code, category, display_name, description, is_dangerous, sort_order) VALUES
    -- Analytics -------------------------------------------------------------
    ('dashboard.view',      'Analytics', 'View dashboards',
     'See aggregate device population figures, vendor and model breakdowns, and change over time.',
     false, 10),
    ('lookup.subscriber',   'Analytics', 'Look up a subscriber',
     'Resolve an individual MSISDN to the SIMs and handsets bound to it. This exposes raw identifiers for a named person, and is withheld from Viewer deliberately.',
     true, 20),
    ('data.export',         'Analytics', 'Export data',
     'Download query results as a file. Every export is audited with its filter and row count.',
     true, 30),

    -- Imports ---------------------------------------------------------------
    ('import.view',         'Imports', 'View import history',
     'Open the Import Center: job history, progress, quarantine samples and file previews.',
     false, 10),
    ('import.upload.sqm',   'Imports', 'Upload SQM daily files',
     'Submit a daily subscriber change file for processing.',
     false, 20),
    ('import.upload.tac',   'Imports', 'Upload TAC database',
     'Submit a new GSMA TAC snapshot. Uploading does not activate it.',
     false, 30),
    ('import.reprocess',    'Imports', 'Reprocess an import',
     'Re-run a completed or failed import from the stored original file.',
     false, 40),
    ('import.cancel',       'Imports', 'Cancel a running import',
     'Request cancellation of a queued or running job.',
     false, 50),
    ('import.delete',       'Imports', 'Delete an import',
     'Remove an import and the analytics rows it produced. Irreversible without the original file.',
     true, 60),
    ('tac.activate',        'Imports', 'Activate a TAC version',
     'Promote an uploaded TAC snapshot to ACTIVE. Changes manufacturer and model names on every screen in the system.',
     true, 70),
    ('tac.rollback',        'Imports', 'Roll back a TAC version',
     'Return to a previously active TAC snapshot.',
     true, 80),

    -- Administration --------------------------------------------------------
    ('user.view',           'Administration', 'View users',
     'See the user list, a user''s roles, and their effective permissions.',
     false, 10),
    ('user.manage',         'Administration', 'Manage users',
     'Create users, edit profiles, assign roles, grant or deny individual permissions, activate and deactivate accounts, reset passwords and revoke sessions.',
     true, 20),
    ('role.view',           'Administration', 'View roles',
     'See roles, their permissions and their members.',
     false, 30),
    ('role.manage',         'Administration', 'Manage roles',
     'Create and edit roles and change which permissions they carry.',
     true, 40),
    ('audit.view',          'Administration', 'View the audit log',
     'Read the append-only record of logins, administrative changes and data operations.',
     false, 50),
    ('system.admin',        'Administration', 'System administration',
     'Operational settings and maintenance actions not covered by a narrower permission.',
     true, 60);

-- ===========================================================================
-- ROLES
-- ===========================================================================
CREATE TABLE auth.role (
    id           bigserial PRIMARY KEY,
    code         text        NOT NULL,
    display_name text        NOT NULL,
    description  text        NOT NULL DEFAULT '',
    -- Seeded roles. They cannot be deleted or renamed, because deployment notes,
    -- documentation and any future directory-group mapping refer to them by code.
    -- Their PERMISSIONS remain editable - that is the point of Role Management.
    is_system    boolean     NOT NULL DEFAULT false,
    created_at   timestamptz NOT NULL DEFAULT now(),
    created_by   text,
    updated_at   timestamptz NOT NULL DEFAULT now(),
    updated_by   text
);

-- Case-insensitive uniqueness. 'Analyst' and 'analyst' must not both exist: two
-- roles a human reads as one is exactly how a permission leak goes unnoticed.
CREATE UNIQUE INDEX ux_role_code ON auth.role (lower(code));

CREATE TABLE auth.role_permission (
    role_id         bigint      NOT NULL REFERENCES auth.role(id) ON DELETE CASCADE,
    permission_code text        NOT NULL REFERENCES auth.permission(code) ON DELETE RESTRICT,
    granted_at      timestamptz NOT NULL DEFAULT now(),
    granted_by      text,
    PRIMARY KEY (role_id, permission_code)
);

-- The four roles from docs/architecture/04-security-model.md section 3, with one
-- correction: Data Operator uploads TAC snapshots but does NOT activate them.
-- That is decision D4 of the import platform ("Operator imports, Admin
-- activates"), and the security model's table predates it.
INSERT INTO auth.role (code, display_name, description, is_system) VALUES
    ('viewer',        'Viewer',
     'Aggregate analytics only. Cannot resolve an individual subscriber.', true),
    ('analyst',       'Analyst',
     'Analytics, subscriber lookup and export. No write access to the pipeline.', true),
    ('data_operator', 'Data Operator',
     'Everything an Analyst can do, plus submitting and re-running imports. Cannot activate a TAC version, delete an import, or administer the system.', true),
    ('administrator', 'Administrator',
     'Full access, including user and role management and the audit log.', true);

INSERT INTO auth.role_permission (role_id, permission_code)
SELECT r.id, p.code
FROM auth.role r
CROSS JOIN auth.permission p
WHERE (r.code = 'viewer' AND p.code IN ('dashboard.view', 'import.view'))
   OR (r.code = 'analyst' AND p.code IN (
        'dashboard.view', 'import.view', 'lookup.subscriber', 'data.export'))
   OR (r.code = 'data_operator' AND p.code IN (
        'dashboard.view', 'import.view', 'lookup.subscriber', 'data.export',
        'import.upload.sqm', 'import.upload.tac', 'import.reprocess', 'import.cancel'))
   OR (r.code = 'administrator');

-- ===========================================================================
-- USERS
--
-- `provider` is here from the first migration even though only 'local' is
-- implemented, because retrofitting it means rewriting the uniqueness rules on a
-- table that already holds accounts. It costs one column now and makes adding
-- LDAP/AD/OIDC an additive change - see ADR-006.
-- ===========================================================================
CREATE TABLE auth.user_account (
    id                   bigserial PRIMARY KEY,
    username             text        NOT NULL,
    display_name         text        NOT NULL,
    email                text,
    -- Shown on the profile and user detail pages; not used for anything else.
    job_title            text,
    phone                text,

    provider             text        NOT NULL DEFAULT 'local'
        CHECK (provider IN ('local', 'ldap', 'oidc')),
    -- The directory's own identifier (objectGUID, `sub` claim). NULL for local accounts.
    external_id          text,

    -- PHC string: $argon2id$v=19$m=...,t=...,p=...$<salt>$<hash>. Self-describing, so the work
    -- factor can be raised later and existing hashes still verify - the login path rehashes them
    -- on the next successful sign-in. NULL for directory-backed accounts, which have no local
    -- password by design.
    password_hash        text,
    password_updated_at  timestamptz,
    must_change_password boolean     NOT NULL DEFAULT false,

    is_active            boolean     NOT NULL DEFAULT true,
    deactivated_at       timestamptz,
    deactivated_by       text,
    deactivation_reason  text,

    -- Lockout counters. Reset on any successful authentication.
    failed_login_count   smallint    NOT NULL DEFAULT 0,
    last_failed_login_at timestamptz,
    locked_until         timestamptz,

    last_login_at        timestamptz,
    last_login_ip        inet,
    previous_login_at    timestamptz,

    created_at           timestamptz NOT NULL DEFAULT now(),
    created_by           text,
    updated_at           timestamptz NOT NULL DEFAULT now(),
    updated_by           text,

    -- A local account must be able to authenticate; a directory account must not carry a local
    -- password. Stated as a constraint rather than trusted to the application, because a local
    -- account with a NULL hash would silently accept nothing and look like a bug in the login page.
    CONSTRAINT ck_user_local_has_password
        CHECK ((provider = 'local' AND password_hash IS NOT NULL)
            OR (provider <> 'local' AND password_hash IS NULL)),
    CONSTRAINT ck_user_external_id
        CHECK ((provider = 'local' AND external_id IS NULL)
            OR (provider <> 'local' AND external_id IS NOT NULL))
);

CREATE UNIQUE INDEX ux_user_username ON auth.user_account (lower(username));
CREATE UNIQUE INDEX ux_user_external ON auth.user_account (provider, external_id)
    WHERE external_id IS NOT NULL;
CREATE INDEX ix_user_active ON auth.user_account (is_active, lower(display_name));

COMMENT ON COLUMN auth.user_account.previous_login_at IS
    'The sign-in before the current one. This is what the profile page shows as "last sign-in", '
    'because last_login_at during a session is the session the user is looking at, which tells '
    'them nothing about whether someone else has used their account.';

-- MFA is not implemented: the product owner confirmed a strictly internal deployment and chose
-- "not now, but make it easy to add". Deliberately no dead columns here. Adding TOTP later is:
--   1. a migration creating auth.user_mfa (user_id, secret, enrolled_at, recovery_code_hashes),
--   2. a second step in the login endpoint, between password check and session creation,
--   3. an enrolment page,
--   4. an mfa_required flag on auth.role, so it can be demanded of administrators only.
-- ADR-006 records this so the next person does not have to infer it.

-- ---------------------------------------------------------------------------
-- Role assignment. Many-to-many: a user may hold several roles at once and
-- receives the union of their permissions.
-- ---------------------------------------------------------------------------
CREATE TABLE auth.user_role (
    user_id    bigint      NOT NULL REFERENCES auth.user_account(id) ON DELETE CASCADE,
    role_id    bigint      NOT NULL REFERENCES auth.role(id) ON DELETE RESTRICT,
    granted_at timestamptz NOT NULL DEFAULT now(),
    granted_by text,
    PRIMARY KEY (user_id, role_id)
);

CREATE INDEX ix_user_role_role ON auth.user_role (role_id);

-- ---------------------------------------------------------------------------
-- Per-user permission overrides.
--
-- WHY THIS EXISTS. Without it, one exception forces a new role: "Analyst, but
-- may not run subscriber lookups" becomes a fifth role, then a sixth, and a year
-- later nobody can say what separates analyst_b from analyst_c. That is the
-- failure mode RBAC actually has in the field, and it is worse than the
-- complexity it was avoiding.
--
-- DENY WINS, always. A deny is the control an auditor asks about - "you removed
-- her lookup access on the 3rd; show me" - so it must not be defeatable by
-- adding a role. The resolution is shown explicitly on the user detail page:
-- every permission says where it came from and what overrode it.
-- ---------------------------------------------------------------------------
CREATE TABLE auth.user_permission (
    user_id         bigint      NOT NULL REFERENCES auth.user_account(id) ON DELETE CASCADE,
    permission_code text        NOT NULL REFERENCES auth.permission(code) ON DELETE RESTRICT,
    effect          text        NOT NULL CHECK (effect IN ('grant', 'deny')),
    reason          text,
    granted_at      timestamptz NOT NULL DEFAULT now(),
    granted_by      text,
    PRIMARY KEY (user_id, permission_code)
);

COMMENT ON TABLE auth.user_permission IS
    'Direct grants and denies, layered over role membership. Effective set = '
    '(union of role permissions + direct grants) minus direct denies.';

-- ===========================================================================
-- SESSIONS
--
-- Server-side, per ADR-006. The cookie carries an opaque random value and
-- nothing else; every fact about the session lives here.
--
-- What that buys, and the reason it was chosen over a JWT: deactivating a user
-- or changing their roles takes effect on their VERY NEXT request. There is no
-- window in which a revoked account still works, and no revocation list to
-- maintain alongside a token whose whole point was not needing one.
-- ===========================================================================
CREATE TABLE auth.session (
    id                  uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             bigint      NOT NULL REFERENCES auth.user_account(id) ON DELETE CASCADE,

    -- SHA-256 of the cookie value. The cookie itself is never stored, so a dump of this table
    -- does not hand the reader a set of live sessions. Plain SHA-256 is correct here and would
    -- not be for a password: the input is 256 bits of CSPRNG output, so there is nothing to
    -- brute-force and nothing to slow an attacker down from.
    token_sha256        bytea       NOT NULL,

    created_at          timestamptz NOT NULL DEFAULT now(),
    last_seen_at        timestamptz NOT NULL DEFAULT now(),
    -- Rolls forward as the session is used. A session idle past this is dead.
    idle_expires_at     timestamptz NOT NULL,
    -- Does not roll forward. A session cannot outlive this however active it is.
    absolute_expires_at timestamptz NOT NULL,

    revoked_at          timestamptz,
    revoked_by          text,
    revoked_reason      text,

    ip                  inet,
    user_agent          text
);

CREATE UNIQUE INDEX ux_session_token ON auth.session (token_sha256);
CREATE INDEX ix_session_user ON auth.session (user_id, created_at DESC);
-- For the sweeper. Partial, because only live rows are ever swept.
CREATE INDEX ix_session_expiry ON auth.session (absolute_expires_at)
    WHERE revoked_at IS NULL;

-- ===========================================================================
-- AUDIT
--
-- Append-only by GRANT (see db/operational/grants/003_grants.sql), not by
-- convention. The application role has INSERT and SELECT here and nothing else,
-- so no code path - including a compromised one - can rewrite history.
--
-- actor_name is denormalised on purpose. An audit entry must still name its
-- actor after that account is gone, and a join returning NULL for deleted users
-- makes the log useless in precisely the case where it matters.
-- ===========================================================================
CREATE TABLE audit.event (
    id             bigserial PRIMARY KEY,
    occurred_at    timestamptz NOT NULL DEFAULT now(),

    actor_user_id  bigint,
    -- The username as it was at the time. NOT a foreign key - see above.
    actor_name     text        NOT NULL,

    -- 'auth.login', 'user.create', 'role.permission.grant', 'lookup.subscriber', ...
    action         text        NOT NULL,
    category       text        NOT NULL
        CHECK (category IN ('authentication', 'user', 'role', 'data', 'import', 'system')),
    outcome        text        NOT NULL
        CHECK (outcome IN ('success', 'failure', 'denied')),

    target_type    text,
    target_id      text,
    -- Human-readable name of the target at the time, for the same reason as actor_name.
    target_name    text,

    source_ip      inet,
    user_agent     text,
    correlation_id text,
    detail         jsonb
);

CREATE INDEX ix_audit_time ON audit.event (occurred_at DESC);
CREATE INDEX ix_audit_actor ON audit.event (actor_user_id, occurred_at DESC);
CREATE INDEX ix_audit_category ON audit.event (category, occurred_at DESC);
CREATE INDEX ix_audit_action ON audit.event (action, occurred_at DESC);
-- Failures and denials are what anyone actually goes looking for, and they are a
-- small fraction of the table. A partial index keeps that search cheap forever.
CREATE INDEX ix_audit_outcome ON audit.event (outcome, occurred_at DESC)
    WHERE outcome <> 'success';

-- ---------------------------------------------------------------------------
-- One stream for the Audit Log page.
--
-- imports.import_audit predates this schema and has working, tested code writing
-- to it. Rather than migrate live rows and rewrite those repositories, both
-- tables are presented through one view with a common shape. The import side has
-- no outcome column, so its entries report 'success' - which is accurate: the
-- import platform writes an audit row when something HAPPENED, and records
-- failures as job state plus an import_event, not as an audit entry.
--
-- Cost: the union cannot use a single composite index, so ordering by time sorts
-- the combined set. At the observed rate - a few thousand rows a year from
-- imports, tens of thousands from logins - that is a sort over a small table. If
-- it ever stops being one, the fix is a materialised view refreshed on write,
-- and nothing above this line changes.
-- ---------------------------------------------------------------------------
CREATE VIEW audit.event_log AS
SELECT
    'audit:' || e.id::text  AS entry_id,
    e.occurred_at           AS occurred_at,
    e.actor_user_id         AS actor_user_id,
    e.actor_name            AS actor_name,
    e.action                AS action,
    e.category              AS category,
    e.outcome               AS outcome,
    e.target_type           AS target_type,
    e.target_id             AS target_id,
    e.target_name           AS target_name,
    e.source_ip             AS source_ip,
    e.correlation_id        AS correlation_id,
    e.detail                AS detail
FROM audit.event e
UNION ALL
SELECT
    'import:' || a.id::text,
    a.occurred_at,
    u.id,
    a.actor,
    a.action,
    'import',
    'success',
    CASE WHEN a.job_id IS NOT NULL         THEN 'import_job'
         WHEN a.tac_version_id IS NOT NULL THEN 'tac_version'
         WHEN a.file_id IS NOT NULL        THEN 'import_file'
    END,
    COALESCE(a.job_id, a.tac_version_id, a.file_id)::text,
    NULL,
    a.source_ip,
    a.correlation_id,
    a.detail
FROM imports.import_audit a
LEFT JOIN auth.user_account u ON lower(u.username) = lower(a.actor);

COMMENT ON VIEW audit.event_log IS
    'Every audited action, from both the identity schema and the import platform. This is what '
    'the Audit Log page reads. Writers use the underlying tables directly.';
