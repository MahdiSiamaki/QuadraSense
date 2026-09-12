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

ALTER TABLE sqm.agg_device_daily DROP PARTITION {seq:UInt16};
ALTER TABLE sqm.agg_kpi_daily    DROP PARTITION {seq:UInt16};

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
     unknown_device_bindings, malformed_imei_bindings, tac_matched_bindings, distinct_sims)
SELECT
    {seq:UInt16} AS seq,
    NULL         AS data_date,
    count()                                          AS active_bindings,
    uniq(msisdn)                                     AS distinct_subscribers,
    -- Real devices only. '000000' is one literal value shared by 8.8M bindings, so
    -- counting it would add exactly one phantom handset to the device total.
    uniqIf(imei, length(imei) = 14)                  AS distinct_devices,
    countIf(imei = '000000')                         AS unknown_device_bindings,
    -- Numeric but not 14 digits, so no TAC can be derived. A defect, unlike the sentinel above.
    countIf(length(imei) != 14 AND imei != '000000')  AS malformed_imei_bindings,
    countIf(tac != '' AND tac IN (SELECT tac FROM sqm.tac)) AS tac_matched_bindings,
    uniq(imsi)                                       AS distinct_sims
FROM sqm.binding_current
WHERE active = 1;

-- --------------------------------------------------------------------------
-- 3. Capability flags per TAC.
--
--    Derived once over 270,166 device models rather than re-parsed for every one of the
--    126M bindings. Joining the full TAC table into that aggregation -- `bandDetails`
--    holds multi-hundred-character band lists -- exhausted a 6 GB server.
-- --------------------------------------------------------------------------
TRUNCATE TABLE sqm.tac_capability;

INSERT INTO sqm.tac_capability (tac, has_lte, has_5g, has_esim, ims_emergency)
SELECT
    tac,
    bandDetails ILIKE '%LTE%' AS has_lte,
    -- Two spellings occur: "5G NR: n2, n66" for handsets and "5G NA Options: 5G-5Series"
    -- for modules. Matching bare '5G' also catches 135 rows that mention it incidentally.
    (bandDetails ILIKE '%5G NR%' OR bandDetails ILIKE '%5G NA%') AS has_5g,
    -- The GSMA eUICC fields are counts, not flags: '0' and '00' both mean none, and
    -- 1, 2 and 3 all occur and all mean the device has an embedded UICC.
    (toUInt8OrZero(removableEUICC) + toUInt8OrZero(nonremovableEUICC)) > 0 AS has_esim,
    multiIf(authenticatedIMSEmergencyCallSupport IN ('Y', 'Yes'), 1,
            authenticatedIMSEmergencyCallSupport IN ('N', 'No'), 0,
            2) AS ims_emergency
FROM sqm.tac;

-- --------------------------------------------------------------------------
-- 4. Device-class mix, per measure.
--
--    The class mapping is deliberately coarse. The point of this widget is the
--    handset-versus-machine split, which a 19-category chart obscures.
--
--    Under 'bindings' and 'handsets' the classes PARTITION the population - every row
--    falls in exactly one. Under 'subscribers' they OVERLAP: someone owning a smartphone
--    and a feature phone is counted in both. That is the honest answer to "how many
--    subscribers have at least one device of this class", and the UI labels it as such.
-- --------------------------------------------------------------------------
ALTER TABLE sqm.agg_device_class_daily DROP PARTITION {seq:UInt16};

INSERT INTO sqm.agg_device_class_daily (seq, data_date, measure, device_class, n)
SELECT
    {seq:UInt16}, NULL, 'bindings',
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
LEFT JOIN (SELECT tac, deviceType FROM sqm.tac) AS t ON t.tac = b.tac
WHERE b.active = 1
GROUP BY device_class;

INSERT INTO sqm.agg_device_class_daily (seq, data_date, measure, device_class, n)
SELECT
    {seq:UInt16}, NULL, 'subscribers',
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
    uniq(b.msisdn) AS n
FROM sqm.binding_current AS b
LEFT JOIN (SELECT tac, deviceType FROM sqm.tac) AS t ON t.tac = b.tac
WHERE b.active = 1
GROUP BY device_class;

INSERT INTO sqm.agg_device_class_daily (seq, data_date, measure, device_class, n)
SELECT
    {seq:UInt16}, NULL, 'handsets',
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
    uniqIf(b.imei, length(b.imei) = 14) AS n
FROM sqm.binding_current AS b
LEFT JOIN (SELECT tac, deviceType FROM sqm.tac) AS t ON t.tac = b.tac
WHERE b.active = 1
GROUP BY device_class;

-- --------------------------------------------------------------------------
-- 5. Network / SIM capability, per measure.
--
--    Capabilities overlap (a device can be LTE and 5G and eSIM), so each row is an
--    independent supported/unsupported/unknown split rather than a slice of a whole.
--
--    'unknown' is carried explicitly instead of being folded into 'unsupported'.
--    Conflating them would claim we know a device lacks 5G when we do not know what the
--    device is - and that is 7% of the population.
--
--    VoLTE is absent because the dataset does not contain it: bandDetails mentions it in
--    2 rows of 270,166, and the IMS columns describe emergency calling, a different
--    capability, 'Not Known' for 94% of TACs.
--
--    ONE SCAN PER MEASURE. All four capabilities are aggregated in a single pass and then
--    expanded into rows with arrayJoin. Written as four statements it was four full scans
--    of 126M rows per measure - twelve in total.
-- --------------------------------------------------------------------------
ALTER TABLE sqm.agg_capability_daily DROP PARTITION {seq:UInt16};

INSERT INTO sqm.agg_capability_daily
    (seq, data_date, measure, capability, supported, unsupported, unknown)
SELECT {seq:UInt16}, NULL, 'bindings', row.1, row.2, row.3, row.4
FROM (
    SELECT arrayJoin([
            ('LTE', s0, u0, x0),
            ('5G', s1, u1, x1),
            ('eSIM', s2, u2, x2),
            ('IMS emergency calling', s3, u3, x3)
    ]) AS row
    FROM (
        SELECT
            countIf(c.tac != '' AND c.has_lte = 1) AS s0,
            countIf(c.tac != '' AND c.has_lte = 0) AS u0,
            countIf(c.tac = '') AS x0,
            countIf(c.tac != '' AND c.has_5g = 1) AS s1,
            countIf(c.tac != '' AND c.has_5g = 0) AS u1,
            countIf(c.tac = '') AS x1,
            countIf(c.tac != '' AND c.has_esim = 1) AS s2,
            countIf(c.tac != '' AND c.has_esim = 0) AS u2,
            countIf(c.tac = '') AS x2,
            countIf(c.tac != '' AND c.ims_emergency = 1) AS s3,
            countIf(c.tac != '' AND c.ims_emergency = 0) AS u3,
            countIf(c.tac = '' OR c.ims_emergency = 2) AS x3
        FROM sqm.binding_current AS b
        LEFT JOIN sqm.tac_capability AS c ON c.tac = b.tac
        WHERE b.active = 1
    )
);

INSERT INTO sqm.agg_capability_daily
    (seq, data_date, measure, capability, supported, unsupported, unknown)
SELECT {seq:UInt16}, NULL, 'subscribers', row.1, row.2, row.3, row.4
FROM (
    SELECT arrayJoin([
            ('LTE', s0, u0, x0),
            ('5G', s1, u1, x1),
            ('eSIM', s2, u2, x2),
            ('IMS emergency calling', s3, u3, x3)
    ]) AS row
    FROM (
        SELECT
            uniqIf(b.msisdn, c.tac != '' AND c.has_lte = 1) AS s0,
            uniqIf(b.msisdn, c.tac != '' AND c.has_lte = 0) AS u0,
            uniqIf(b.msisdn, c.tac = '') AS x0,
            uniqIf(b.msisdn, c.tac != '' AND c.has_5g = 1) AS s1,
            uniqIf(b.msisdn, c.tac != '' AND c.has_5g = 0) AS u1,
            uniqIf(b.msisdn, c.tac = '') AS x1,
            uniqIf(b.msisdn, c.tac != '' AND c.has_esim = 1) AS s2,
            uniqIf(b.msisdn, c.tac != '' AND c.has_esim = 0) AS u2,
            uniqIf(b.msisdn, c.tac = '') AS x2,
            uniqIf(b.msisdn, c.tac != '' AND c.ims_emergency = 1) AS s3,
            uniqIf(b.msisdn, c.tac != '' AND c.ims_emergency = 0) AS u3,
            uniqIf(b.msisdn, c.tac = '' OR c.ims_emergency = 2) AS x3
        FROM sqm.binding_current AS b
        LEFT JOIN sqm.tac_capability AS c ON c.tac = b.tac
        WHERE b.active = 1
    )
);

INSERT INTO sqm.agg_capability_daily
    (seq, data_date, measure, capability, supported, unsupported, unknown)
SELECT {seq:UInt16}, NULL, 'handsets', row.1, row.2, row.3, row.4
FROM (
    SELECT arrayJoin([
            ('LTE', s0, u0, x0),
            ('5G', s1, u1, x1),
            ('eSIM', s2, u2, x2),
            ('IMS emergency calling', s3, u3, x3)
    ]) AS row
    FROM (
        SELECT
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac != '' AND c.has_lte = 1)) AS s0,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac != '' AND c.has_lte = 0)) AS u0,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac = '')) AS x0,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac != '' AND c.has_5g = 1)) AS s1,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac != '' AND c.has_5g = 0)) AS u1,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac = '')) AS x1,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac != '' AND c.has_esim = 1)) AS s2,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac != '' AND c.has_esim = 0)) AS u2,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac = '')) AS x2,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac != '' AND c.ims_emergency = 1)) AS s3,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac != '' AND c.ims_emergency = 0)) AS u3,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac = '' OR c.ims_emergency = 2)) AS x3
        FROM sqm.binding_current AS b
        LEFT JOIN sqm.tac_capability AS c ON c.tac = b.tac
        WHERE b.active = 1
    )
);

-- --------------------------------------------------------------------------
-- 6. Per-dimension counts, three ways.
--
--    Distinct counts cannot be derived at query time (see migration 009), so they are
--    materialised here for every dimension a breakdown can group by.
--
--    ONE STATEMENT PER DIMENSION, deliberately. Computing all six in a single query with
--    UNION ALL keeps six HyperLogLog aggregation states alive at once and exhausted a 3 GB
--    server. Sequential statements re-scan the table but keep peak memory flat, which is
--    the right trade for a job that runs once a day on hardware we do not control.
--
--    The '(unknown device)' and '(unknown TAC)' labels are produced here rather than in the
--    API, so every consumer of this table sees the same buckets.
--    MEMORY. These group by up to 95,572 distinct TACs, each holding two HyperLogLog
--    states. run_refresh.sh applies the per-query ceiling and spill settings that keep
--    that within a modest server's budget - see the note there.
-- --------------------------------------------------------------------------
ALTER TABLE sqm.agg_dimension_daily DROP PARTITION {seq:UInt16};

INSERT INTO sqm.agg_dimension_daily
    (seq, data_date, dimension, dim_value, bindings, subscribers, handsets)
SELECT
    {seq:UInt16}, NULL, 'vendor',
    multiIf(b.tac = '', '(unknown device)',
            t.tac = '',  '(unknown TAC)',
            coalesce(coalesce(nullIf(v.vendor_canonical, ''), nullIf(t.manufacturer, '')), '(unknown TAC)')) AS dim_value,
    count()                        AS bindings,
    uniq(b.msisdn)                 AS subscribers,
    uniqIf(b.imei, length(b.imei) = 14) AS handsets
FROM sqm.binding_current AS b
LEFT JOIN sqm.tac AS t ON t.tac = b.tac
LEFT JOIN sqm.tac_vendor_map AS v ON v.raw_manufacturer = t.manufacturer
WHERE b.active = 1
GROUP BY dim_value;

INSERT INTO sqm.agg_dimension_daily
    (seq, data_date, dimension, dim_value, bindings, subscribers, handsets)
SELECT
    {seq:UInt16}, NULL, 'manufacturer',
    multiIf(b.tac = '', '(unknown device)',
            t.tac = '',  '(unknown TAC)',
            coalesce(nullIf(t.manufacturer, ''), '(unknown TAC)')) AS dim_value,
    count()                        AS bindings,
    uniq(b.msisdn)                 AS subscribers,
    uniqIf(b.imei, length(b.imei) = 14) AS handsets
FROM sqm.binding_current AS b
LEFT JOIN sqm.tac AS t ON t.tac = b.tac
LEFT JOIN sqm.tac_vendor_map AS v ON v.raw_manufacturer = t.manufacturer
WHERE b.active = 1
GROUP BY dim_value;

INSERT INTO sqm.agg_dimension_daily
    (seq, data_date, dimension, dim_value, bindings, subscribers, handsets)
SELECT
    {seq:UInt16}, NULL, 'model',
    multiIf(b.tac = '', '(unknown device)',
            t.tac = '',  '(unknown TAC)',
            coalesce(nullIf(t.marketingName, ''), '(unknown TAC)')) AS dim_value,
    count()                        AS bindings,
    uniq(b.msisdn)                 AS subscribers,
    uniqIf(b.imei, length(b.imei) = 14) AS handsets
FROM sqm.binding_current AS b
LEFT JOIN sqm.tac AS t ON t.tac = b.tac
LEFT JOIN sqm.tac_vendor_map AS v ON v.raw_manufacturer = t.manufacturer
WHERE b.active = 1
GROUP BY dim_value;

INSERT INTO sqm.agg_dimension_daily
    (seq, data_date, dimension, dim_value, bindings, subscribers, handsets)
SELECT
    {seq:UInt16}, NULL, 'deviceType',
    multiIf(b.tac = '', '(unknown device)',
            t.tac = '',  '(unknown TAC)',
            coalesce(nullIf(t.deviceType, ''), '(unknown TAC)')) AS dim_value,
    count()                        AS bindings,
    uniq(b.msisdn)                 AS subscribers,
    uniqIf(b.imei, length(b.imei) = 14) AS handsets
FROM sqm.binding_current AS b
LEFT JOIN sqm.tac AS t ON t.tac = b.tac
LEFT JOIN sqm.tac_vendor_map AS v ON v.raw_manufacturer = t.manufacturer
WHERE b.active = 1
GROUP BY dim_value;

INSERT INTO sqm.agg_dimension_daily
    (seq, data_date, dimension, dim_value, bindings, subscribers, handsets)
SELECT
    {seq:UInt16}, NULL, 'os',
    multiIf(b.tac = '', '(unknown device)',
            t.tac = '',  '(unknown TAC)',
            coalesce(nullIf(trim(t.operatingSystem), ''), '(unknown TAC)')) AS dim_value,
    count()                        AS bindings,
    uniq(b.msisdn)                 AS subscribers,
    uniqIf(b.imei, length(b.imei) = 14) AS handsets
FROM sqm.binding_current AS b
LEFT JOIN sqm.tac AS t ON t.tac = b.tac
LEFT JOIN sqm.tac_vendor_map AS v ON v.raw_manufacturer = t.manufacturer
WHERE b.active = 1
GROUP BY dim_value;

INSERT INTO sqm.agg_dimension_daily
    (seq, data_date, dimension, dim_value, bindings, subscribers, handsets)
SELECT
    {seq:UInt16}, NULL, 'tac',
    if(tac = '', '(unknown device)', tac) AS dim_value,
    count()                        AS bindings,
    uniq(msisdn)                   AS subscribers,
    uniqIf(imei, length(imei) = 14) AS handsets
FROM sqm.binding_current
WHERE active = 1
GROUP BY dim_value;
