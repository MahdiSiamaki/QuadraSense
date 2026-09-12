-- refresh_marts.sql
-- Rebuilds the dashboard marts from binding_current.
--
-- Run after each delivery is folded in.
--
-- IDEMPOTENCY. Each mart is partitioned by `seq`, and every section below drops that
-- partition before inserting. Re-running a delivery therefore produces the same result
-- instead of adding to it.
--
-- This is not theoretical caution. An earlier version of this file only INSERTed, and a
-- single accidental re-run doubled every count in agg_device_daily to 251,879,046 against
-- a real 125,939,523 — silently, because a SummingMergeTree sums duplicates without
-- complaint. DROP PARTITION is atomic and instant, so correctness here is structural
-- rather than something the operator has to remember.
--
-- `seq` 0 is the initial snapshot; later deliveries write their own sequence.

ALTER TABLE sqm.agg_device_daily       DROP PARTITION {seq:UInt16};
ALTER TABLE sqm.agg_kpi_daily          DROP PARTITION {seq:UInt16};
ALTER TABLE sqm.agg_device_class_daily DROP PARTITION {seq:UInt16};

-- --------------------------------------------------------------------------
-- 1. Device rollup: one row per (seq, tac, active).
--    ~95,000 rows per delivery. This is what every vendor / model / OS / type
--    widget reads, instead of scanning 126M rows.
-- --------------------------------------------------------------------------
INSERT INTO sqm.agg_device_daily (seq, data_date, tac, active, n)
SELECT
    {seq:UInt16}  AS seq,
    NULL          AS data_date,   -- no source dates yet; backfilled when supplied
    tac,
    active,
    count()       AS n
FROM sqm.binding_current
GROUP BY tac, active;

-- --------------------------------------------------------------------------
-- 2. Headline counters, including the distinct counts that cannot be derived
--    from the rollup above.
--
--    uniq() is a HyperLogLog estimate with ~0.5% error. On a KPI card reading
--    "79.5M" that is invisible, and it is three orders of magnitude cheaper than
--    uniqExact, which measured at ~52s. Exact counts stay available via export.
-- --------------------------------------------------------------------------
INSERT INTO sqm.agg_kpi_daily
    (seq, data_date, active_bindings, distinct_subscribers, distinct_devices,
     unknown_device_bindings, malformed_imei_bindings, tac_matched_bindings)
SELECT
    {seq:UInt16} AS seq,
    NULL         AS data_date,
    count()                                          AS active_bindings,
    uniq(msisdn)                                     AS distinct_subscribers,
    uniq(imei)                                       AS distinct_devices,
    countIf(imei = '000000')                         AS unknown_device_bindings,
    -- Numeric but not 14 digits, so no TAC can be derived. A defect, unlike the sentinel above.
    countIf(length(imei) != 14 AND imei != '000000')  AS malformed_imei_bindings,
    countIf(tac != '' AND tac IN (SELECT tac FROM sqm.tac)) AS tac_matched_bindings
FROM sqm.binding_current
WHERE active = 1;

-- --------------------------------------------------------------------------
-- 3. Device-class mix.
--
--    The class mapping is deliberately coarse. The point of this widget is the
--    handset-versus-machine split, which a 19-category chart obscures.
-- --------------------------------------------------------------------------
INSERT INTO sqm.agg_device_class_daily (seq, data_date, device_class, n)
SELECT
    {seq:UInt16} AS seq,
    NULL         AS data_date,
    multiIf(
        b.tac = '',                                              'Unknown device',
        t.tac = '',                                              'Unregistered TAC',
        t.deviceType IN ('Smartphone'),                          'Smartphone',
        t.deviceType IN ('Mobile Phone/Feature phone'),          'Feature phone',
        t.deviceType IN ('Tablet', 'e-Book'),                    'Tablet',
        t.deviceType IN ('Modem', 'Dongle', 'Module', 'WLAN Router',
                         'IoT Device', 'Vehicle', 'Vehicle TCU',
                         'Connected Computer', 'UAS/UAV', 'Satellite'), 'IoT / M2M',
        t.deviceType IN ('Wearable'),                            'Wearable',
        'Other'
    ) AS device_class,
    count() AS n
FROM sqm.binding_current AS b
LEFT JOIN sqm.tac AS t ON t.tac = b.tac
WHERE b.active = 1
GROUP BY device_class;

-- --------------------------------------------------------------------------
-- 4. Network / SIM capability.
--
--    Capabilities overlap (a device can be LTE and 5G and eSIM), so each row is an
--    independent supported/unsupported/unknown split rather than a slice of a whole.
--
--    "unknown" is carried explicitly instead of being folded into "unsupported".
--    Conflating them would claim we know a device lacks 5G when in fact we do not
--    know what the device is - and that is 7% of the population.
-- --------------------------------------------------------------------------
ALTER TABLE sqm.agg_capability_daily DROP PARTITION {seq:UInt16};

INSERT INTO sqm.agg_capability_daily (seq, data_date, capability, supported, unsupported, unknown)
WITH joined AS (
    SELECT
        b.n                                   AS n,
        t.tac != ''                           AS enriched,
        t.bandDetails                         AS bands,
        toUInt8OrZero(t.removableEUICC)
          + toUInt8OrZero(t.nonremovableEUICC) AS euicc_count,
        t.authenticatedIMSEmergencyCallSupport AS ims
    FROM sqm.agg_device_daily AS b
    LEFT JOIN sqm.tac AS t ON t.tac = b.tac
    WHERE b.seq = {seq:UInt16} AND b.active = 1
)
SELECT {seq:UInt16}, NULL, capability, supported, unsupported, unknown FROM (
    SELECT
        'LTE' AS capability,
        sumIf(n, enriched AND bands ILIKE '%LTE%')     AS supported,
        sumIf(n, enriched AND bands NOT ILIKE '%LTE%') AS unsupported,
        sumIf(n, NOT enriched)                         AS unknown
    FROM joined
    UNION ALL
    SELECT
        '5G',
        -- Two spellings appear in the source: "5G NR: n2, n66" for handsets and
        -- "5G NA Options: 5G-5Series" for modules. Matching bare '5G' would also catch
        -- 135 rows that mention it incidentally.
        sumIf(n, enriched AND (bands ILIKE '%5G NR%' OR bands ILIKE '%5G NA%')),
        sumIf(n, enriched AND NOT (bands ILIKE '%5G NR%' OR bands ILIKE '%5G NA%')),
        sumIf(n, NOT enriched)
    FROM joined
    UNION ALL
    SELECT
        'eSIM',
        -- These GSMA fields are eUICC *counts*, not flags: 0 means none, 1+ means the
        -- device has an embedded UICC. '00' and '0' both occur and both parse to 0.
        sumIf(n, enriched AND euicc_count > 0),
        sumIf(n, enriched AND euicc_count = 0),
        sumIf(n, NOT enriched)
    FROM joined
    UNION ALL
    SELECT
        -- Deliberately NOT called VoLTE. This is IMS emergency-call support, which is a
        -- different thing, and it is 'Not Known' for 94% of TACs. It is published so the
        -- gap is visible rather than silently filled with a guess.
        'IMS emergency calling',
        sumIf(n, enriched AND ims IN ('Y', 'Yes')),
        sumIf(n, enriched AND ims IN ('N', 'No')),
        sumIf(n, (NOT enriched) OR ims NOT IN ('Y', 'Yes', 'N', 'No'))
    FROM joined
);
