-- 021_campaign_export.sql
--
-- The controlled export of a campaign list: the numbers in one planning class, taken out of the
-- system for a campaign - a 2G/3G migration, an eSIM offer.
--
-- WHY A GATE, decided by the product owner on 2026-10-21 (docs/research/2026-10-feature-research.md
-- section 7): subscriber lists for campaigns are allowed, with controls - a permission of their own,
-- a recorded reason, and a second person's approval above a size threshold - built before any list
-- leaves. A list of millions of numbers is a different act from looking one up, and the audit log is
-- what carries the weight, since masking is off by decision.
--
-- HOW. A request records what was asked for (the class and filters of one planning run, never the
-- numbers), how many it counted, who asked and why. At or below the threshold
-- (CampaignExport:ApprovalThreshold) it is approved on the spot; above it, it waits for someone else
-- holding campaign.approve - never the requester, which the CHECK below holds even against a bug in
-- the application. An approved list can be downloaded by its requester until it expires, and only
-- from the run it was counted on, so what is approved is what leaves.
--
-- WHO. Administrator holds both permissions. Granting them to anyone else is a decision for the role
-- matrix, not for this migration. campaign.export also needs lookup.subscriber, checked by the API.

CREATE SCHEMA IF NOT EXISTS campaign;
COMMENT ON SCHEMA campaign IS
    'Controlled export of campaign lists: requests, their reasons, approvals and downloads.';

CREATE TABLE IF NOT EXISTS campaign.export_request (
    id                  bigserial   PRIMARY KEY,
    -- What was asked for: { view, class, reason, vendor, model, deviceType, runId, asOfSequence, asOfDate }.
    definition          jsonb       NOT NULL
                        CONSTRAINT ck_export_definition_size CHECK (octet_length(definition::text) <= 4096),
    row_count           bigint      NOT NULL CONSTRAINT ck_export_rows CHECK (row_count >= 0),
    reason              text        NOT NULL
                        CONSTRAINT ck_export_reason CHECK (length(btrim(reason)) BETWEEN 10 AND 1000),
    -- Accounts are deactivated, not deleted; if one ever is, its requests go with it (the audit log
    -- keeps the record of each step regardless).
    requested_by        bigint      NOT NULL REFERENCES auth.user_account (id) ON DELETE CASCADE,
    requested_at        timestamptz NOT NULL DEFAULT now(),
    -- Whether the row count was above the threshold in force when it was asked for.
    needs_approval      boolean     NOT NULL,
    status              text        NOT NULL
                        CONSTRAINT ck_export_status CHECK (status IN ('pending', 'approved', 'rejected', 'withdrawn')),
    decided_by          bigint      NULL REFERENCES auth.user_account (id),
    decided_at          timestamptz NULL,
    decision_note       text        NULL CONSTRAINT ck_export_note CHECK (decision_note IS NULL OR length(decision_note) <= 1000),
    expires_at          timestamptz NULL,
    downloads           integer     NOT NULL DEFAULT 0,
    last_downloaded_at  timestamptz NULL,

    -- A second person: nobody approves or rejects their own request.
    CONSTRAINT ck_export_four_eyes CHECK (decided_by IS NULL OR decided_by <> requested_by),
    -- A list that needed approval is approved only by a decision.
    CONSTRAINT ck_export_decided CHECK (status <> 'approved' OR NOT needs_approval OR decided_by IS NOT NULL)
);

CREATE INDEX IF NOT EXISTS ix_export_request_requester ON campaign.export_request (requested_by, requested_at DESC);
CREATE INDEX IF NOT EXISTS ix_export_request_pending ON campaign.export_request (requested_at) WHERE status = 'pending';

INSERT INTO auth.permission (code, category, display_name, description, is_dangerous, sort_order)
VALUES
    ('campaign.export', 'Analytics', 'Export campaign lists',
     'Request a list of the numbers in a planning class for a campaign, with a recorded reason, and download it once approved. Above the size threshold a second person must approve. Also needs lookup.subscriber.',
     true, 52),
    ('campaign.approve', 'Analytics', 'Approve campaign lists',
     'Approve or reject another person''s request for a campaign list. Never one''s own.',
     true, 53)
ON CONFLICT (code) DO NOTHING;

INSERT INTO auth.role_permission (role_id, permission_code, granted_by)
SELECT r.id, p.code, 'migration:021'
FROM auth.role r
CROSS JOIN (VALUES ('campaign.export'), ('campaign.approve')) AS p (code)
WHERE r.code = 'administrator'
ON CONFLICT (role_id, permission_code) DO NOTHING;
