-- 035_invalid_imei.sql
--
-- A 14-digit IMEI that no handset could carry: a zero TAC, a shifted IMEI, or a reporting body GSMA
-- never had. Section 1b of the refresh moves such a row to the empty TAC, where every mart already
-- treats it as a row with no handset, and these two columns keep how many there were, so that
-- Enrichment can still account for every active row (ADR-020).
--
-- Adding a column rewrites no parts, so this does not hold the site down, unlike a part-rewriting
-- ALTER (docs/operations/deployment/05-department-ci-cd.md, section 7). A delivery refreshed before
-- this reads 0, and the rule reaches the dashboard with the next refresh.

ALTER TABLE sqm.agg_tac_population ADD COLUMN IF NOT EXISTS invalid_imei UInt64 DEFAULT 0 AFTER malformed_imei;

ALTER TABLE sqm.agg_kpi_daily ADD COLUMN IF NOT EXISTS invalid_imei_bindings UInt64 DEFAULT 0 AFTER malformed_imei_bindings;
