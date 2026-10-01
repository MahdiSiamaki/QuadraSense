-- 013_explorer_saved_queries.sql
--
-- My Queries: an Explorer query saved under a name, to run again, edit or clone.
--
-- PRIVATE TO ITS OWNER, decided by the product owner on 2026-09-30. Every read and write is scoped
-- to the owner in the application; sharing, if it comes, is a new table of grants beside this one,
-- not a change to it.
--
-- THE QUERY, NEVER ITS RESULTS. A saved query is the definition the builder produced - dataset,
-- conditions, grouping, columns - as JSON. Its results are recomputed when it runs, against the data
-- and the permissions of that moment, so a saved query can never become a stale copy of personal
-- data, or a way to keep what a person was later refused.
--
-- It can hold identifiers - a query for one number carries the number - which is why it is private
-- and why the audit records only its id and name.
CREATE SCHEMA IF NOT EXISTS explorer;

COMMENT ON SCHEMA explorer IS
    'Explorer state that belongs to a user: saved queries.';

CREATE TABLE IF NOT EXISTS explorer.saved_query (
    id              bigserial   PRIMARY KEY,
    owner_user_id   bigint      NOT NULL REFERENCES auth.user_account (id) ON DELETE CASCADE,

    name            text        NOT NULL
                    CONSTRAINT ck_saved_query_name CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    description     text        NOT NULL DEFAULT ''
                    CONSTRAINT ck_saved_query_description CHECK (length(description) <= 1000),

    -- The ExplorerQueryRequest as the API received it. Checked by the application before it is
    -- stored; checked again every time it runs, because the catalogue can change.
    query           jsonb       NOT NULL
                    CONSTRAINT ck_saved_query_size CHECK (octet_length(query::text) <= 65536),

    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

-- One name per person, whatever the case: "SIM swaps" and "sim swaps" are the same query to them.
CREATE UNIQUE INDEX IF NOT EXISTS ux_saved_query_owner_name
    ON explorer.saved_query (owner_user_id, lower(btrim(name)));

-- The list is one person's queries, most recently changed first.
CREATE INDEX IF NOT EXISTS ix_saved_query_owner_updated
    ON explorer.saved_query (owner_user_id, updated_at DESC);
