-- 008_kpi_sims_and_real_devices.sql
--
-- Two corrections to the headline counters, both about saying exactly what is counted.
--
-- 1. SIMs were missing. The dashboard showed subscribers (MSISDN) and devices (IMEI) but
--    never SIMs (IMSI), which left the middle of the data model invisible. The two are
--    close but not equal - 79,849,817 IMSIs against 79,461,407 MSISDNs - and that gap is
--    precisely the SIM-swap population, so collapsing them would hide the signal.
--
-- 2. `distinct_devices` counted the '000000' sentinel as a device. It is a single literal
--    value shared by 8.8M bindings, so it contributed exactly one phantom handset to the
--    device count. Real devices are 14-digit IMEIs only.

ALTER TABLE sqm.agg_kpi_daily
    ADD COLUMN IF NOT EXISTS distinct_sims UInt64 DEFAULT 0;
