-- 012_explorer_permission.sql
--
-- explorer.query: building and running ad-hoc queries in the Explorer.
--
-- WHY A PERMISSION OF ITS OWN, decided by the product owner on 2026-09-30. Looking up one number
-- and composing a query over the whole binding population are different acts even when both
-- return identifiers: the second is bulk by design. The identifier rules still apply on top of
-- this one - a query that filters on or returns phone numbers needs lookup.subscriber, SIMs need
-- lookup.imsi, handsets lookup.imei, and identifier.reveal decides whether they come back complete
-- - so holding explorer.query alone lets a person query models, TACs and dates, and nobody by name.
--
-- WHO. Analyst, Data Operator and Administrator: the roles already trusted to resolve individuals.
-- Not Viewer, whose role is aggregate analytics only.
INSERT INTO auth.permission (code, category, display_name, description, is_dangerous, sort_order)
VALUES (
    'explorer.query',
    'Analytics',
    'Run Explorer queries',
    'Compose and run queries across numbers, SIMs, handsets and models in the Explorer. Results that include identifiers also need the matching lookup permission. Every query is audited.',
    true,
    27)
ON CONFLICT (code) DO NOTHING;

INSERT INTO auth.role_permission (role_id, permission_code, granted_by)
SELECT r.id, 'explorer.query', 'migration:012'
FROM auth.role r
WHERE r.code IN ('analyst', 'data_operator', 'administrator')
ON CONFLICT (role_id, permission_code) DO NOTHING;
