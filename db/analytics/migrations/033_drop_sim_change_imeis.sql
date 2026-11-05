-- 033_drop_sim_change_imeis.sql
--
-- risk_sim_change_day kept an IMEI on each side of a SIM change, for "previous device" and "new
-- device" columns in a list that was built without them (migration 023). Nothing has ever read the
-- two columns.
--
-- THEY WERE AN ARBITRARY PICK. anyIf() over the day's rows: when a number has several candidate
-- handsets, which happens in 10-25% of changes, the value depends on the order parts are read. The
-- laptop and the department server hold the same changes and differed in these two columns on 154
-- of 245 common days (measured 2026-11-03), and no other column differed.
--
-- DROPPED RATHER THAN MADE DETERMINISTIC. A stable pick would need all 268 days rewritten on both
-- machines to agree, for columns no screen shows, and would still name one handset where there are
-- several. A list that needs the device can read it from binding_event, and should show every
-- candidate. The product owner asked on 2026-11-05 for this to be settled; this is the cheaper way.
--
-- The worker's INSERT stops naming them in the same release. The deploy waits for a running import,
-- runs migrations, and only then starts the new worker, so the old statement never meets the new
-- table except in a job that starts in those few seconds, which fails, retries and succeeds.

ALTER TABLE sqm.risk_sim_change_day
    DROP COLUMN IF EXISTS new_imei,
    DROP COLUMN IF EXISTS old_imei;
