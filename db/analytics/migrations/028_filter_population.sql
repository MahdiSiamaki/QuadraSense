-- 028_filter_population.sql
--
-- The active population for each value of each dashboard filter, per delivery, as finished
-- figures - so a dashboard filtered by one thing reads one row.
--
-- WHY. Migration 027 made a filtered view a merge of per-model uniq() states instead of a read of
-- binding_current FINAL (100 s alone, 183 s on the dashboard, 2026-10-19). That is correct - the
-- figures match the raw path exactly - but merging states costs what they weigh, and they weigh
-- 1,005 MiB for a delivery. Measured on delivery 246, for the KPI row (bindings, subscribers, SIMs,
-- handsets), at the API's limits:
--
--   one TAC              1 model         0.19 s
--   Modem            1,971 models        2.5 s     a type's models are spread across the TAC order,
--   Samsung         14,581 models        2.7 s     so even a small type touches most blocks
--   Android         29,980 models        3.2 s
--   Smartphone      35,069 models        3.4 s
--
-- against a dashboard budget of 500 ms. The product owner chose this table (2026-10-19): one filter
-- on its own - the case of a link like /?deviceType=Smartphone - answers from a row finished at
-- refresh, and only combinations merge states.
--
-- CORRECTION to 027's header, which is immutable once applied: its "336 s at two threads, 149 MiB"
-- was measured on a query whose outer SELECT never read the state columns, so ClickHouse never
-- built them. Built for real at the refresh's limits, the pass failed twice at 1.20 GiB and then,
-- written in 8,192-row blocks, took 842 s at a 368 MiB peak (refresh_marts.sql, section 1b).
--
-- KEYED by exactly what the filter compares (FilterBuilder.ForCurrentState): t.deviceType,
-- trim(t.operatingSystem), t.manufacturer, and the vendor as coalesce(nullIf(v.vendor_canonical,
-- ''), t.manufacturer) - the same, case for case, so two values the filter treats as one are one
-- row here, and a distinct count is never added across rows. The device catalogue is read at
-- refresh, as every other mart's is.
--
-- 'knownDevice' (value '') is the population without the '' model row - unknown and malformed
-- IMEIs - which is what the filter's "exclude unknown devices" switch alone selects.
--
-- PARTITION BY seq, and the refresh drops the partition before inserting, as for every mart.
CREATE TABLE IF NOT EXISTS sqm.agg_filter_population
(
    seq             UInt16,
    dimension       LowCardinality(String),   -- deviceType | operatingSystem | manufacturer | vendor | knownDevice
    value           String,
    models          UInt64,
    bindings        UInt64,
    unknown_device  UInt64,
    malformed_imei  UInt64,
    tac_matched     UInt64,                   -- bindings of models in the device catalogue
    subscribers     UInt64,                   -- uniq(), as everywhere: ~0.5% error, labelled estimated
    sims            UInt64,
    handsets        UInt64                    -- 14-digit IMEIs only
)
ENGINE = MergeTree
PARTITION BY seq
ORDER BY (seq, dimension, value);
