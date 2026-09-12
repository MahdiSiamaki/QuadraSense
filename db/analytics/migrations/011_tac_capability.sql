-- 011_tac_capability.sql
--
-- Capability flags resolved once per TAC, instead of re-derived per binding.
--
-- WHY. Capability is a property of the device model, so deriving it inside a 126M-row
-- aggregation meant joining the full 26-column TAC table -- including `bandDetails`, which
-- holds multi-hundred-character band lists -- and then running ILIKE over it for every
-- binding. That join exhausted a 6 GB server.
--
-- Here the text parsing happens once over 270,166 rows, and the aggregation joins four
-- small integer columns instead. It is also the better place for the logic: these are
-- statements about a device model, and now they can be inspected and tested directly
-- rather than being buried in an aggregate.

CREATE TABLE IF NOT EXISTS sqm.tac_capability
(
    tac          String,
    has_lte      UInt8,
    has_5g       UInt8,
    has_esim     UInt8,
    -- 1 = supported, 0 = not supported, 2 = the GSMA record does not say.
    -- Three states, not two: 'Not Known' covers 94% of TACs, and folding it into "no"
    -- would assert something the source never claimed.
    ims_emergency UInt8
)
ENGINE = MergeTree
ORDER BY tac;
