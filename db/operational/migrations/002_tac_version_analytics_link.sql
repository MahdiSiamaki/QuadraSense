-- 002_tac_version_analytics_link.sql
--
-- Links an operational TAC version row to the version number the analytics store knows it by,
-- and records the one diff figure that says whether a change matters.
--
-- WHY A SEPARATE COLUMN. imports.tac_version.id is a bigserial owned by PostgreSQL;
-- sqm.tac_all.version_id is a UInt32 owned by ClickHouse. Making one double as the other would
-- mean one database allocating the other's keys, so that the two could only ever be kept in
-- step by convention. An explicit, unique link is one column and no convention.

ALTER TABLE imports.tac_version
    ADD COLUMN analytics_version_id integer,
    -- How many currently active bindings sit on a TAC this version adds, removes or changes.
    --
    -- The row counts next to it describe the file; this describes the effect. 1,600 changed
    -- TACs on models nobody carries is noise; 40 changed TACs covering eight million handsets
    -- is the reason activation is a human decision.
    ADD COLUMN affected_bindings bigint;

CREATE UNIQUE INDEX ux_tac_version_analytics
    ON imports.tac_version (analytics_version_id)
    WHERE analytics_version_id IS NOT NULL;

COMMENT ON COLUMN imports.tac_version.analytics_version_id IS
    'sqm.tac_all.version_id in the analytics store.';
COMMENT ON COLUMN imports.tac_version.affected_bindings IS
    'Active bindings resolving to a TAC this version adds, removes or changes.';
