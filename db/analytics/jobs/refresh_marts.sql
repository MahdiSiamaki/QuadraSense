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
-- 4. Relationship marts.
--
--    These are the expensive ones: grouping 126M rows into ~79M distinct
--    subscribers measured at 52s live. Computed once here, read in milliseconds
--    by the dashboard.
-- --------------------------------------------------------------------------
ALTER TABLE sqm.agg_devices_per_subscriber  DROP PARTITION {seq:UInt16};
ALTER TABLE sqm.agg_subscribers_per_device  DROP PARTITION {seq:UInt16};
ALTER TABLE sqm.agg_msisdn_prefix           DROP PARTITION {seq:UInt16};

INSERT INTO sqm.agg_devices_per_subscriber (seq, data_date, bucket, n_msisdn)
SELECT {seq:UInt16}, NULL, least(d, 10) AS bucket, count() AS n_msisdn
FROM (
    SELECT msisdn, uniqExact(imei) AS d
    FROM sqm.binding_current
    WHERE active = 1
    GROUP BY msisdn
)
GROUP BY bucket;

INSERT INTO sqm.agg_subscribers_per_device (seq, data_date, bucket, n_imei)
SELECT {seq:UInt16}, NULL, least(m, 10) AS bucket, count() AS n_imei
FROM (
    SELECT imei, uniqExact(msisdn) AS m
    FROM sqm.binding_current
    -- Excludes the 000000 sentinel: it is not a device, and counting how many
    -- subscribers "share" it would produce a meaningless 8.8M outlier.
    WHERE active = 1 AND length(imei) = 14
    GROUP BY imei
)
GROUP BY bucket;

INSERT INTO sqm.agg_msisdn_prefix (seq, data_date, prefix4, n)
SELECT {seq:UInt16}, NULL, substring(toString(msisdn), 1, 4) AS prefix4, count() AS n
FROM sqm.binding_current
WHERE active = 1
GROUP BY prefix4;
