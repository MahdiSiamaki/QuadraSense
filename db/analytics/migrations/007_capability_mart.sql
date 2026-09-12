-- 007_capability_mart.sql
--
-- Network and SIM capability of the active device population.
--
-- WHAT THE GSMA DATA ACTUALLY SUPPORTS. Measured against the real 270,166-row TAC export:
--
--   LTE   -- bandDetails lists LTE bands explicitly.   130,599 TACs / 85.31% of bindings
--   5G    -- bandDetails lists "5G NR:" or "5G NA".     37,314 TACs / 18.78% of bindings
--   eSIM  -- removableEUICC / nonremovableEUICC are eUICC counts. ~25k TACs / 5.62%
--
--   VoLTE -- NOT PRESENT. `bandDetails` mentions VoLTE in 2 rows out of 270,166. The three
--            IMS columns are about *emergency calling over IMS*, not VoLTE, and are
--            'Not Known' for 254,264 TACs (94.1%). There is no honest way to report VoLTE
--            support from this dataset, so this mart does not pretend to. IMS emergency
--            support is recorded separately and labelled for what it is.
--
-- Capabilities OVERLAP -- a device can be LTE and 5G and eSIM at once -- so each is stored
-- as its own independent count. They must never be charted as slices of one whole.

CREATE TABLE IF NOT EXISTS sqm.agg_capability_daily
(
    seq         UInt16,
    data_date   Nullable(Date),
    capability  String,
    -- Bindings whose device is known to have the capability.
    supported   UInt64,
    -- Bindings whose device is known NOT to have it.
    unsupported UInt64,
    -- Bindings we cannot assess: unknown device, unregistered TAC, or the GSMA record
    -- does not state it. Kept explicit so a percentage always has a stated denominator.
    unknown     UInt64
)
ENGINE = SummingMergeTree((supported, unsupported, unknown))
PARTITION BY seq
ORDER BY (seq, capability);
