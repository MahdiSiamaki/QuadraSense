-- 020_planning_permission.sql
--
-- planning.view: the network and product planning views (docs/architecture/19-network-planning.md)
-- - 2G/3G sunset, 5G and bands, eSIM, handset lifecycle, the IoT fleet, satellite devices.
--
-- WHO. The views are aggregates about device models and the numbers that hold them, the same kind
-- of figure a dashboard shows, so the permission goes to the roles that see dashboards: Viewer,
-- Analyst and Administrator. Not Data Operator, whose work is the imports. A page of numbers in a
-- view also needs lookup.subscriber, identifier.reveal decides masking, and an aggregate CSV needs
-- data.export - so a Viewer sees the figures and nobody by name. A list of numbers for a campaign
-- leaves the system only through the controlled export, a permission of its own.
INSERT INTO auth.permission (code, category, display_name, description, is_dangerous, sort_order)
VALUES (
    'planning.view',
    'Analytics',
    'View network planning',
    'See the planning views: 2G/3G sunset readiness, 5G and band readiness, eSIM readiness, handset lifecycle, the IoT fleet and satellite devices - counts of numbers, bindings and handsets by device capability. A page of numbers also needs lookup.subscriber.',
    false,
    32)
ON CONFLICT (code) DO NOTHING;

INSERT INTO auth.role_permission (role_id, permission_code, granted_by)
SELECT r.id, 'planning.view', 'migration:020'
FROM auth.role r
WHERE r.code IN ('viewer', 'analyst', 'administrator')
ON CONFLICT (role_id, permission_code) DO NOTHING;
