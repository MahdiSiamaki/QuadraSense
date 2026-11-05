-- 034_drop_device_rollup.sql
--
-- agg_device_daily cost a whole read of binding_current on every refresh, about 9 minutes on the
-- department VM, and nothing read it but the check that it agreed with the KPI row. The comment
-- calling it what every vendor, model, OS and type widget reads had been out of date since the
-- dimension marts took over. The refresh now reads binding_current once, into agg_tac_population,
-- and that is what the check compares.
--
-- Dropping a table rewrites no parts, so this does not hold the site down for long, unlike a
-- part-rewriting ALTER (docs/operations/deployment/05-department-ci-cd.md, section 7).

DROP TABLE IF EXISTS sqm.agg_device_daily;
