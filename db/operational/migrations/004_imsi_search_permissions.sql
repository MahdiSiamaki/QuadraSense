-- 004_imsi_search_permissions.sql
--
-- Two permissions for the IMSI search, and the demonstration that the catalogue really is
-- extensible by INSERT: no enum widened, no type altered, no code deployed for these to appear in
-- the permission matrix and be grantable.

-- ---------------------------------------------------------------------------
-- lookup.imsi
--
-- Separate from lookup.subscriber on purpose. The two answer different questions and are needed
-- by different people: "which handset is this SIM in" is a SIM-operations question, and "which
-- SIM and handset does this person have" is a subscriber question. An organisation can now grant
-- the first without the second.
--
-- Both are sensitive, because both resolve an individual - the SIM identity is no less personal
-- than the number attached to it.
-- ---------------------------------------------------------------------------
INSERT INTO auth.permission (code, category, display_name, description, is_dangerous, sort_order)
VALUES (
    'lookup.imsi',
    'Analytics',
    'Search by IMSI',
    'Find the numbers and handsets bound to a SIM, by full IMSI or by a prefix of at least ten digits.',
    true,
    25)
ON CONFLICT (code) DO NOTHING;

-- ---------------------------------------------------------------------------
-- identifier.reveal
--
-- Governs whether identifiers come back complete or redacted. Masking is applied SERVER-SIDE:
-- a caller without this permission receives the masked string and the raw value is never in the
-- response at all.
--
-- WHY THIS EXISTS AT ALL, given masking was switched off by product decision. That decision said
-- what the default should be, not that the capability should be absent - and the security model
-- has recorded "masking: built, disabled by default" since Phase 1. This is that switch, at
-- per-user granularity rather than per-deployment.
--
-- WHY IT IS GRANTED TO EVERY ROLE THAT CAN ALREADY SEE IDENTIFIERS. Adding a permission that
-- nobody holds would silently redact screens that worked yesterday, which is a product change
-- disguised as a migration. Every role whose members see raw identifiers today keeps seeing them;
-- what is new is that an administrator can now take it away from one person without inventing a
-- role, and that a new role starts without it.
-- ---------------------------------------------------------------------------
INSERT INTO auth.permission (code, category, display_name, description, is_dangerous, sort_order)
VALUES (
    'identifier.reveal',
    'Analytics',
    'See identifiers in full',
    'Without this, MSISDN, IMSI and IMEI are returned masked. The redaction happens on the server, so the complete value never reaches the browser.',
    true,
    26)
ON CONFLICT (code) DO NOTHING;

-- ---------------------------------------------------------------------------
-- Role assignment.
--
-- lookup.imsi follows lookup.subscriber: the roles trusted to resolve an individual by number are
-- trusted to resolve one by SIM.
--
-- identifier.reveal follows the same set, for the reason above: Viewer never sees an identifier
-- in the first place, so its absence there changes nothing.
-- ---------------------------------------------------------------------------
INSERT INTO auth.role_permission (role_id, permission_code, granted_by)
SELECT r.id, p.code, 'migration:004'
FROM auth.role r
CROSS JOIN (VALUES ('lookup.imsi'), ('identifier.reveal')) AS p(code)
WHERE r.code IN ('analyst', 'data_operator', 'administrator')
ON CONFLICT (role_id, permission_code) DO NOTHING;
