-- 005_device_module.sql
--
-- The Devices module: four permissions, and a place to keep device photographs.
--
-- The permissions go in by INSERT, again. That is the third time the catalogue has been extended
-- without widening an enum, altering a type or deploying code for the rows to become grantable -
-- which is what "extensible" was supposed to mean when it was designed in migration 003.

-- ===========================================================================================
-- 1. Permissions
--
-- Four, because they carry genuinely different risk and an organisation should be able to
-- separate them. The device CATALOGUE identifies nobody; the identifiers bound to a device
-- identify a great many people at once.
-- ===========================================================================================

-- ---------------------------------------------------------------------------
-- device.view - the catalogue.
--
-- Models, manufacturers, brands, capabilities, how many handsets of each are on the network, and
-- how that changed. Every one of those is a statement about a device model. None of them names
-- anybody, which is why this sits alongside dashboard.view rather than alongside the lookups.
-- ---------------------------------------------------------------------------
INSERT INTO auth.permission (code, category, display_name, description, is_dangerous, sort_order)
VALUES (
    'device.view',
    'Analytics',
    'Browse devices',
    'Open the Devices section: models, manufacturers, brands, capabilities and how many handsets of each are on the network. Contains no personal identifiers.',
    false,
    20)
ON CONFLICT (code) DO NOTHING;

-- ---------------------------------------------------------------------------
-- lookup.imei - one handset.
--
-- The exact counterpart of lookup.imsi, one layer down: that one answers "which handsets has this
-- SIM been in", this one answers "which SIMs have been in this handset". Separate because they
-- are separate jobs - device fraud and handset recovery ask the second, subscriber support asks
-- neither - and because the whole point of the catalogue is that an organisation can grant one
-- without the other.
-- ---------------------------------------------------------------------------
INSERT INTO auth.permission (code, category, display_name, description, is_dangerous, sort_order)
VALUES (
    'lookup.imei',
    'Analytics',
    'Search by IMEI',
    'Resolve one handset to the SIMs and numbers bound to it, now and historically.',
    true,
    27)
ON CONFLICT (code) DO NOTHING;

-- ---------------------------------------------------------------------------
-- device.identifiers - every handset of a model.
--
-- THIS IS THE DANGEROUS ONE, and it is deliberately not folded into lookup.imei.
--
-- Resolving one IMEI exposes one person's handset. Listing a device model's identifiers exposes
-- everybody who owns that model: the most populous TAC on this network holds 296,686 bindings
-- across 208,895 handsets. Those are different acts even though they read the same table, and a
-- permission model that cannot tell them apart is not describing the risk.
--
-- Granted narrowly for that reason, and audited on every call with the count of rows returned.
-- ---------------------------------------------------------------------------
INSERT INTO auth.permission (code, category, display_name, description, is_dangerous, sort_order)
VALUES (
    'device.identifiers',
    'Analytics',
    'List a device''s identifiers',
    'Page through every IMEI, IMSI and MSISDN bound to a device model. Bulk exposure: a popular model covers hundreds of thousands of subscribers. Audited on every request.',
    true,
    28)
ON CONFLICT (code) DO NOTHING;

-- ---------------------------------------------------------------------------
-- device.image.manage - curating the photographs.
--
-- Not dangerous in the identifier sense, but it is a write, it changes what every user sees, and
-- the bytes come from outside the system. It belongs to whoever curates reference data - the same
-- people who upload TAC snapshots - rather than to everyone who can read the catalogue.
-- ---------------------------------------------------------------------------
INSERT INTO auth.permission (code, category, display_name, description, is_dangerous, sort_order)
VALUES (
    'device.image.manage',
    'Reference data',
    'Manage device images',
    'Upload or replace the photograph shown on a device page. Device images are curated here; the GSMA TAC record contains no imagery.',
    false,
    29)
ON CONFLICT (code) DO NOTHING;

-- ---------------------------------------------------------------------------
-- Role assignment.
--
-- device.view goes to everyone including Viewer, because it is the product. A Viewer who can see
-- the dashboard's "Samsung 43.2%" and cannot open Samsung has been given a chart and denied the
-- thing it is about.
--
-- lookup.imei follows lookup.imsi exactly: the roles trusted to resolve an individual by SIM are
-- trusted to resolve one by handset.
--
-- device.identifiers does NOT follow them. Analyst and Administrator get it; Data Operator does
-- not. A Data Operator's job is getting files in and marts rebuilt, and nothing in that needs a
-- list of two hundred thousand people's handsets. This is the first grant in the system where
-- data_operator is deliberately narrower than analyst, and it is deliberate.
--
-- device.image.manage follows import.upload.tac - reference-data curation, same people.
-- ---------------------------------------------------------------------------
INSERT INTO auth.role_permission (role_id, permission_code, granted_by)
SELECT r.id, 'device.view', 'migration:005'
FROM auth.role r
WHERE r.code IN ('viewer', 'analyst', 'data_operator', 'administrator')
ON CONFLICT (role_id, permission_code) DO NOTHING;

INSERT INTO auth.role_permission (role_id, permission_code, granted_by)
SELECT r.id, 'lookup.imei', 'migration:005'
FROM auth.role r
WHERE r.code IN ('analyst', 'data_operator', 'administrator')
ON CONFLICT (role_id, permission_code) DO NOTHING;

INSERT INTO auth.role_permission (role_id, permission_code, granted_by)
SELECT r.id, 'device.identifiers', 'migration:005'
FROM auth.role r
WHERE r.code IN ('analyst', 'administrator')
ON CONFLICT (role_id, permission_code) DO NOTHING;

INSERT INTO auth.role_permission (role_id, permission_code, granted_by)
SELECT r.id, 'device.image.manage', 'migration:005'
FROM auth.role r
WHERE r.code IN ('data_operator', 'administrator')
ON CONFLICT (role_id, permission_code) DO NOTHING;

-- ===========================================================================================
-- 2. Device images
--
-- WHERE THE PICTURES COME FROM, since the question has a real answer and it is not obvious.
--
-- The GSMA TAC database has 26 columns - manufacturer, modelName, marketingName, brandName,
-- allocationDate, organisationId, deviceType, bluetooth, nfc, wlan, the IMS and UICC columns,
-- ntnConnectivity, simSlot, imeiQuantity, operatingSystem, oem, bandDetails - and not one of them
-- is an image, a URL or a reference to one. There is no imagery to import.
--
-- The deployment is internal-network-only (ADR-006), so a device-catalogue CDN is not merely
-- undesirable, it is unreachable.
--
-- So images are CURATED HERE: an administrator uploads one per device model, and until they do,
-- the page draws a typed placeholder. That was the product owner's choice among the options, and
-- it is the only one that works on day one without an external dependency.
--
-- WHY bytea AND NOT THE IMPORT FILE STORE. Import files are multi-gigabyte CSVs and live on disk
-- behind IImportFileStore, correctly. These are not those. A device photograph is tens of
-- kilobytes, there will be hundreds rather than hundreds of thousands, and every one of them is
-- served on a page view. Holding them in the row keeps the bytes and the metadata atomic - no
-- orphaned files, no rows pointing at files that a restore did not bring back - and puts them in
-- the same backup as everything else. The 512 KB ceiling below is what keeps that true.
-- ===========================================================================================
CREATE SCHEMA IF NOT EXISTS catalog;

COMMENT ON SCHEMA catalog IS
    'Curated reference data about device models, maintained in this system rather than imported.';

CREATE TABLE IF NOT EXISTS catalog.device_image (
    -- The 8-digit Type Allocation Code. Not a foreign key: the TAC dimension lives in ClickHouse,
    -- and an image for a TAC that the current GSMA snapshot does not list is still worth keeping -
    -- the next snapshot may well add it.
    tac             text        PRIMARY KEY
                    CONSTRAINT ck_device_image_tac CHECK (tac ~ '^[0-9]{8}$'),

    content_type    text        NOT NULL
                    CONSTRAINT ck_device_image_type
                    CHECK (content_type IN ('image/png', 'image/jpeg', 'image/webp')),

    -- The bytes. Capped at 512 KB: large enough for a detailed product photograph, small enough
    -- that this table stays something you can hold in a backup without thinking about it.
    bytes           bytea       NOT NULL
                    CONSTRAINT ck_device_image_size
                    CHECK (octet_length(bytes) > 0 AND octet_length(bytes) <= 524288),

    -- Content hash, which is what the HTTP ETag is built from. A browser that already has this
    -- image gets a 304 and no bytes, which matters on a list of forty devices.
    sha256          bytea       NOT NULL
                    CONSTRAINT ck_device_image_sha CHECK (octet_length(sha256) = 32),

    -- Free text from the uploader: where the picture came from, so provenance survives the person.
    source_note     text        NOT NULL DEFAULT '',

    uploaded_by     bigint      NOT NULL REFERENCES auth.user_account (id),
    uploaded_at     timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE catalog.device_image IS
    'One curated photograph per device model. Uploaded by administrators; the GSMA TAC record has no imagery and this deployment has no internet access.';

-- Lets the list endpoint answer "which of these forty TACs have a picture" with one indexed read
-- instead of fetching forty images to find out.
CREATE INDEX IF NOT EXISTS ix_device_image_updated ON catalog.device_image (updated_at DESC);
