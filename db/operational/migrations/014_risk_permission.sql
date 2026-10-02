-- 014_risk_permission.sql
--
-- risk.view: the Risk signals page, the risk section of an entity, and the risk lists.
--
-- WHY A PERMISSION OF ITS OWN, decided by the product owner on 2026-10-02. A risk list names
-- numbers, SIMs and handsets the system has singled out, which is a different act from looking one
-- up or querying a population: the list itself is a judgement about people. The identifier rules
-- still apply on top of this one - a list of SIMs also needs lookup.imsi, of handsets lookup.imei,
-- of numbers lookup.subscriber; identifier.reveal decides whether they come back complete; and
-- taking rows away needs data.export.
--
-- WHO. Analyst and Administrator. Not Viewer, whose role is aggregate analytics only, and not Data
-- Operator, whose work is the imports: the owner's decision, not an inference from explorer.query.
INSERT INTO auth.permission (code, category, display_name, description, is_dangerous, sort_order)
VALUES (
    'risk.view',
    'Analytics',
    'View risk signals',
    'See the Risk signals page and the risk section of a number, SIM or handset: observed patterns judged against the configured thresholds. Lists that name individuals also need the matching lookup permission. Every list is audited.',
    true,
    31)
ON CONFLICT (code) DO NOTHING;

INSERT INTO auth.role_permission (role_id, permission_code, granted_by)
SELECT r.id, 'risk.view', 'migration:014'
FROM auth.role r
WHERE r.code IN ('analyst', 'administrator')
ON CONFLICT (role_id, permission_code) DO NOTHING;
