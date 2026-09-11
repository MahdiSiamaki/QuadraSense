-- 004_kpi_malformed_imei.sql
--
-- Splits "no TAC could be derived" into its two genuinely different causes.
--
-- The data-quality screen was reporting an unregistered-TAC figure ~31,000 rows above the
-- measured 255,676, because the arithmetic subtracted only the `000000` sentinel. There is a
-- second, separate population: IMEIs that are numeric but not 14 digits (`000100`, `000510`,
-- `501515014`, …), which also yield no TAC.
--
-- Conflating them hides the distinction that matters operationally:
--
--   1. imei = '000000'          8,776,237   the SOURCE says it does not know the device
--   2. malformed IMEI              31,209   the VALUE ITSELF is wrong - a data defect
--   3. TAC not in GSMA DB         255,676   we know the device, GSMA does not list it
--                               ----------
--   + TAC matched            116,876,401
--                            = 125,939,523
--
-- Only (2) is a defect worth chasing with the source. Reporting it inside (1) or (3) makes
-- a real problem invisible.

ALTER TABLE sqm.agg_kpi_daily
    ADD COLUMN IF NOT EXISTS malformed_imei_bindings UInt64 DEFAULT 0;
