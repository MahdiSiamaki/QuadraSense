-- 016_dashboards.sql
--
-- The dashboard builder (ADR-016): dashboards a person owns, the grants that share them, and each
-- person's default and favourites.
--
-- THE DEFINITION, NEVER ITS RESULTS. A dashboard is the widgets and their layout as JSON. What a
-- widget shows is recomputed when it is opened, against the data and the permissions of whoever
-- opens it, so a shared dashboard can never become a copy of what its owner was allowed to see.
--
-- ACCESS IS RELATIONAL. Ownership, grants and preferences are rows, because they are what every read
-- decides access from; the widgets live inside the definition, because a dashboard is read and saved
-- whole. Whether a definition is valid - which widget types, which settings - is the application's to
-- check against the widget contract; the database holds the size ceilings as a backstop.
--
-- Templates are code (frontend/src/features/dashboard/templates/), never seeded here: a seed per
-- person would change everybody's state whenever a template was added.
CREATE SCHEMA IF NOT EXISTS dashboard;

COMMENT ON SCHEMA dashboard IS
    'Dashboard builder: dashboards, their grants, and each person''s default and favourites.';

CREATE TABLE IF NOT EXISTS dashboard.dashboard (
    id                  bigserial   PRIMARY KEY,
    owner_user_id       bigint      NOT NULL REFERENCES auth.user_account (id) ON DELETE CASCADE,

    name                text        NOT NULL
                        CONSTRAINT ck_dashboard_name CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    description         text        NOT NULL DEFAULT ''
                        CONSTRAINT ck_dashboard_description CHECK (length(description) <= 1000),

    -- The validated definition: { schemaVersion, defaults, refreshMinutes, widgets[] }. The
    -- application's limit is lower and configurable (Dashboards:MaxDefinitionBytes, 128 KiB).
    definition          jsonb       NOT NULL
                        CONSTRAINT ck_dashboard_definition_size CHECK (octet_length(definition::text) <= 262144),
    schema_version      integer     NOT NULL DEFAULT 1
                        CONSTRAINT ck_dashboard_schema_version CHECK (schema_version >= 1),

    -- Optimistic concurrency: every update names the revision it started from and is refused (409)
    -- when somebody else saved in between. Starts at 1.
    revision            integer     NOT NULL DEFAULT 1
                        CONSTRAINT ck_dashboard_revision CHECK (revision >= 1),

    -- Which template it was made from, for provenance only: "device-population@1". Never consulted.
    source_template     text        NULL
                        CONSTRAINT ck_dashboard_source_template CHECK (source_template IS NULL OR length(source_template) <= 64),

    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    updated_by_user_id  bigint      NULL REFERENCES auth.user_account (id) ON DELETE SET NULL
);

-- One name per owner, whatever the case or surrounding spaces: two "Operations" in one switcher
-- cannot be told apart.
CREATE UNIQUE INDEX IF NOT EXISTS ux_dashboard_owner_name
    ON dashboard.dashboard (owner_user_id, lower(btrim(name)));

CREATE INDEX IF NOT EXISTS ix_dashboard_owner_updated
    ON dashboard.dashboard (owner_user_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS dashboard.dashboard_grant (
    id                  bigserial   PRIMARY KEY,
    dashboard_id        bigint      NOT NULL REFERENCES dashboard.dashboard (id) ON DELETE CASCADE,

    -- Exactly one subject: a person or a role. A deleted account or role takes its grants with it.
    user_id             bigint      NULL REFERENCES auth.user_account (id) ON DELETE CASCADE,
    role_id             bigint      NULL REFERENCES auth.role (id) ON DELETE CASCADE,
    CONSTRAINT ck_dashboard_grant_one_subject CHECK (num_nonnulls(user_id, role_id) = 1),

    -- viewer opens it; editor also changes it, and only while holding dashboard.edit. Neither can
    -- share, delete or transfer it.
    level               text        NOT NULL
                        CONSTRAINT ck_dashboard_grant_level CHECK (level IN ('viewer', 'editor')),

    granted_at          timestamptz NOT NULL DEFAULT now(),
    granted_by_user_id  bigint      NULL REFERENCES auth.user_account (id) ON DELETE SET NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_dashboard_grant_user
    ON dashboard.dashboard_grant (dashboard_id, user_id) WHERE user_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_dashboard_grant_role
    ON dashboard.dashboard_grant (dashboard_id, role_id) WHERE role_id IS NOT NULL;

-- "Shared with me" is a lookup by subject.
CREATE INDEX IF NOT EXISTS ix_dashboard_grant_user ON dashboard.dashboard_grant (user_id) WHERE user_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_dashboard_grant_role ON dashboard.dashboard_grant (role_id) WHERE role_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS dashboard.favorite (
    user_id             bigint      NOT NULL REFERENCES auth.user_account (id) ON DELETE CASCADE,
    dashboard_id        bigint      NOT NULL REFERENCES dashboard.dashboard (id) ON DELETE CASCADE,
    created_at          timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, dashboard_id)
);

-- What `/` opens for a person: one of their dashboards or a template, or neither (Device Population).
-- A favourite or default whose dashboard the person can no longer open is ignored on read, not
-- deleted: access is decided when read, and the grant may come back.
CREATE TABLE IF NOT EXISTS dashboard.user_default (
    user_id             bigint      PRIMARY KEY REFERENCES auth.user_account (id) ON DELETE CASCADE,
    dashboard_id        bigint      NULL REFERENCES dashboard.dashboard (id) ON DELETE SET NULL,
    template_key        text        NULL
                        CONSTRAINT ck_user_default_template CHECK (template_key IS NULL OR template_key ~ '^[a-z][a-z0-9-]{1,47}$'),
    CONSTRAINT ck_user_default_one CHECK (num_nonnulls(dashboard_id, template_key) <= 1),
    updated_at          timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------- permissions
--
-- None of these reaches data. A widget always queries as the person looking at it, with their own
-- permissions; sharing a dashboard shares its arrangement, never what its owner may see.
INSERT INTO auth.permission (code, category, display_name, description, is_dangerous, sort_order)
VALUES
    ('dashboard.create', 'Analytics', 'Create dashboards',
     'Create, duplicate and import dashboards of one''s own. Grants no access to data: every widget still needs its own permission.',
     false, 12),
    ('dashboard.edit', 'Analytics', 'Edit dashboards',
     'Change the widgets and layout of one''s own dashboards, and of dashboards shared with one as editor.',
     false, 13),
    ('dashboard.share', 'Analytics', 'Share dashboards',
     'Share one''s own dashboards with users and roles as viewer or editor. Sharing never shares data: each person sees only what their own permissions allow.',
     false, 14),
    ('dashboard.manage', 'Analytics', 'Manage all dashboards',
     'Edit, share and delete any person''s dashboard. An administrator''s override; it reaches no data.',
     true, 15)
ON CONFLICT (code) DO NOTHING;

-- Analyst and Data Operator build and share their own; Administrator also manages everyone's.
-- Viewer opens what is shared with them and keeps a default and favourites, which need only
-- dashboard.view.
INSERT INTO auth.role_permission (role_id, permission_code, granted_by)
SELECT r.id, p.code, 'migration:016'
FROM auth.role r
CROSS JOIN (VALUES ('dashboard.create'), ('dashboard.edit'), ('dashboard.share')) AS p(code)
WHERE r.code IN ('analyst', 'data_operator', 'administrator')
ON CONFLICT (role_id, permission_code) DO NOTHING;

INSERT INTO auth.role_permission (role_id, permission_code, granted_by)
SELECT r.id, 'dashboard.manage', 'migration:016'
FROM auth.role r
WHERE r.code = 'administrator'
ON CONFLICT (role_id, permission_code) DO NOTHING;
