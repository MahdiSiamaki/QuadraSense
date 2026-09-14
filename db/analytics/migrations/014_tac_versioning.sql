-- 014_tac_versioning.sql
--
-- Makes the TAC dimension versioned, with an activation step.
--
-- WHY. TAC decides the manufacturer, model and capability shown on every screen in the
-- product. Until now `sqm.tac` held one unversioned snapshot, so loading a new GSMA file meant
-- overwriting the only copy: no diff to review beforehand, no way back if the file was wrong,
-- and no way to reproduce a report that was run against the previous mapping.
--
-- HOW. Every version lives in one table, keyed by version first, and `sqm.tac` becomes a view
-- onto whichever version is active. Three consequences:
--
--   * Activation is a one-row write, not a data load. It is instant and it is reversible.
--   * Rollback is activation of an earlier version - the same operation, no special path.
--   * Every existing query against `sqm.tac` keeps working unchanged.
--
-- The cost of the view is a filter on `version_id`, which is the leading column of the sort
-- key, so ClickHouse prunes to the active version's granules rather than scanning all of them.
-- At 270,166 rows per version this was never going to be the expensive part of any query.

-- ---------------------------------------------------------------------------
-- All versions, in one table.
--
-- ORDER BY (version_id, tac) and not (tac, version_id): queries always want one whole
-- version, never one TAC across versions, so version must lead for the prune to work.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sqm.tac_all
(
    version_id                              UInt32,
    tac                                     String,
    manufacturer                            String,
    modelName                               String,
    marketingName                           String,
    brandName                               String,
    allocationDate                          String,
    lastUpdatedDate                         String,
    organisationId                          String,
    deviceType                              String,
    bluetooth                               String,
    nfc                                     String,
    wlan                                    String,
    authenticatedIMSEmergencyCallSupport    String,
    unauthenticatedIMSEmergencyCallSupport  String,
    imsEmergencyCallWithoutUICC             String,
    removableUICC                           String,
    removableEUICC                          String,
    nonremovableUICC                        String,
    nonremovableEUICC                       String,
    networkSpecificIdentifier               String,
    ntnConnectivity                         String,
    simSlot                                 String,
    imeiQuantity                            String,
    operatingSystem                         String,
    oem                                     String,
    bandDetails                             String
)
ENGINE = MergeTree
PARTITION BY version_id
ORDER BY (version_id, tac);

-- ---------------------------------------------------------------------------
-- Which version is active.
--
-- One logical row, kept by ReplacingMergeTree so activation is an insert rather than a
-- mutation - an insert is atomic and instant, a mutation rewrites parts.
--
-- PostgreSQL is the system of record for the activation lifecycle (imports.tac_version, with
-- a unique index enforcing exactly one ACTIVE). This table exists so the analytics store can
-- resolve the active version without a cross-database lookup on every query.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sqm.tac_active
(
    singleton    UInt8 DEFAULT 1,
    version_id   UInt32,
    activated_at DateTime64(3, 'UTC')
)
ENGINE = ReplacingMergeTree(activated_at)
ORDER BY singleton;

-- ---------------------------------------------------------------------------
-- Seed: the snapshot already loaded becomes version 1, and is activated.
--
-- Written so re-running the migration is harmless: the INSERT does nothing when version 1 is
-- already there.
-- ---------------------------------------------------------------------------
INSERT INTO sqm.tac_all
SELECT
    1 AS version_id,
    tac, manufacturer, modelName, marketingName, brandName, allocationDate, lastUpdatedDate,
    organisationId, deviceType, bluetooth, nfc, wlan,
    authenticatedIMSEmergencyCallSupport, unauthenticatedIMSEmergencyCallSupport,
    imsEmergencyCallWithoutUICC, removableUICC, removableEUICC, nonremovableUICC,
    nonremovableEUICC, networkSpecificIdentifier, ntnConnectivity, simSlot, imeiQuantity,
    operatingSystem, oem, bandDetails
FROM sqm.tac
WHERE (SELECT count() FROM sqm.tac_all WHERE version_id = 1) = 0;

INSERT INTO sqm.tac_active (singleton, version_id, activated_at)
SELECT 1, 1, now64(3)
WHERE (SELECT count() FROM sqm.tac_active) = 0;

-- ---------------------------------------------------------------------------
-- `sqm.tac` becomes the active version.
--
-- The old table is renamed rather than dropped. It is the only copy of the delivered GSMA
-- snapshot until the version table is confirmed good, and a migration that destroys its own
-- rollback path is not one that should be run on a Friday.
-- ---------------------------------------------------------------------------
RENAME TABLE sqm.tac TO sqm.tac_v1_original;

CREATE VIEW sqm.tac AS
SELECT
    tac, manufacturer, modelName, marketingName, brandName, allocationDate, lastUpdatedDate,
    organisationId, deviceType, bluetooth, nfc, wlan,
    authenticatedIMSEmergencyCallSupport, unauthenticatedIMSEmergencyCallSupport,
    imsEmergencyCallWithoutUICC, removableUICC, removableEUICC, nonremovableUICC,
    nonremovableEUICC, networkSpecificIdentifier, ntnConnectivity, simSlot, imeiQuantity,
    operatingSystem, oem, bandDetails
FROM sqm.tac_all
WHERE version_id = (SELECT version_id FROM sqm.tac_active FINAL ORDER BY activated_at DESC LIMIT 1);
