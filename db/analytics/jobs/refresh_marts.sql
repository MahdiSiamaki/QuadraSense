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

-- --------------------------------------------------------------------------
-- WHY EVERY READ OF binding_current USES FINAL.
--
-- binding_current is a ReplacingMergeTree keyed on (msisdn, imsi, imei) and versioned by
-- last_change_seq. A daily fold inserts one row per binding the day touched, and those rows
-- duplicate keys already in the table until a background merge collapses them. Merges happen
-- when ClickHouse decides, not when the mart refresh runs.
--
-- Without FINAL, a mart built between the fold and the merge counts BOTH rows for every binding
-- that changed that day. Not an error, not a warning - just quietly larger numbers on every
-- screen. This project has already shipped that bug once, when an insert-only mart refresh
-- doubled agg_device_daily to 251,879,046 against a real 125,939,523.
--
-- FINAL costs a merge-on-read over ~295M rows. These marts are built once per delivery, so the
-- trade is a slower daily job against figures that cannot silently double. It is not close.
-- --------------------------------------------------------------------------

ALTER TABLE sqm.agg_device_daily DROP PARTITION {seq:UInt16};
ALTER TABLE sqm.agg_kpi_daily    DROP PARTITION {seq:UInt16};
ALTER TABLE sqm.agg_device_model DROP PARTITION {seq:UInt16};
ALTER TABLE sqm.agg_tac_population DROP PARTITION {seq:UInt16};
ALTER TABLE sqm.agg_filter_population DROP PARTITION {seq:UInt16};

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
FROM sqm.binding_current FINAL
GROUP BY tac, active;

-- --------------------------------------------------------------------------
-- 1b. Device model rollup: one row per (seq, tac), active bindings only.
--
--     What the Devices module reads. ~98,000 rows, so sorting, filtering and
--     paging a device catalogue is a scan of something tiny instead of a 2.35 s
--     aggregate over 295M rows per keystroke.
--
--     It carries four counts rather than the rollup above's one, because the
--     distinct ones cannot be derived later: handsets, SIMs and subscribers do
--     not sum across models. Someone who owns two phones is one subscriber of
--     each model and one subscriber overall, and no arithmetic on per-model
--     totals recovers that.
--
--     uniq() and not uniqExact, matching every other mart here: HyperLogLog at
--     ~0.5% error, three orders of magnitude cheaper. Using a different
--     estimator here would make this table disagree with the dashboard about
--     the same population, which is worse than either estimate.
--
--     first_seen / last_seen are NULL when no daily file has ever named a
--     binding of this model: min() and max() skip NULLs, and a model present
--     only in the initial dump has nothing but NULLs to skip.
--
--     The pass keeps the distinct counts as uniq() STATES first, in
--     agg_tac_population (migration 027), and agg_device_model is finalised from
--     them: finalizeAggregation(uniqState(x)) is uniq(x), so its numbers are the
--     ones it always had, from the same single read of binding_current. The
--     states are what a filtered dashboard view merges - every dashboard filter
--     is a property of the TAC - instead of reading binding_current FINAL, which
--     took 100 s alone and 183 s on the dashboard on 2026-10-19.
--
--     Written in blocks of 8,192 rows, not the default ~1M. The default gathers
--     every model's states into one block before writing - all 158,002 rows of a
--     delivery fit - so all the states are in memory at once, and at the
--     refresh's own limits (one thread, 1.2 GiB) that failed twice on
--     2026-10-19, at 1.20 GiB, merging the spilled aggregation back. Finished
--     numbers, as this pass wrote before, are small enough that it never showed.
--     In small blocks: 842 s, 368 MiB peak, 158,002 rows (delivery 246).
-- --------------------------------------------------------------------------
INSERT INTO sqm.agg_tac_population
    (seq, tac, bindings, unknown_device, malformed_imei, subscribers, sims, handsets, first_seen, last_seen)
SELECT
    {seq:UInt16}  AS seq,
    tac,
    count()                                             AS bindings,
    countIf(imei = '000000')                            AS unknown_device,
    countIf(length(imei) != 14 AND imei != '000000')    AS malformed_imei,
    uniqState(msisdn)                                   AS subscribers,
    uniqState(imsi)                                     AS sims,
    uniqStateIf(imei, length(imei) = 14)                AS handsets,
    min(last_change_date)                               AS first_seen,
    max(last_change_date)                               AS last_seen
FROM sqm.binding_current FINAL
WHERE active = 1
GROUP BY tac
SETTINGS min_insert_block_size_rows = 8192, min_insert_block_size_bytes = 0;

INSERT INTO sqm.agg_device_model
    (seq, data_date, tac, bindings, handsets, sims, subscribers, first_seen, last_seen)
SELECT
    seq,
    NULL                            AS data_date,
    tac,
    bindings,
    finalizeAggregation(handsets)   AS handsets,
    finalizeAggregation(sims)       AS sims,
    finalizeAggregation(subscribers) AS subscribers,
    first_seen,
    last_seen
FROM sqm.agg_tac_population
WHERE seq = {seq:UInt16};

-- --------------------------------------------------------------------------
-- 1c. One filter's population, finished: a row per (dimension, value).
--
--     What a dashboard filtered by one thing reads (migration 028) - one row,
--     instead of merging the states of every model it matches, which took
--     2.5-3.4 s for a device type, a vendor or an OS on 2026-10-19.
--
--     Merged from the states just written, never from binding_current: no
--     second pass over the table. Each key is the filter's own comparison
--     (FilterBuilder.ForCurrentState), so a value the filter treats as one is
--     one row. An empty value is never a filter, and is left out.
--
--     One statement per dimension: each groups its own values, and the largest
--     - 3,020 vendors - stays a separate, smaller hash table.
--
--     No join carries the states. Each model's value is looked up by TAC -
--     transform() over two arrays read once from sqm.tac - and "matched" is an
--     IN. A LEFT JOIN of the states to sqm.tac was what this did until
--     2026-10-25, and ClickHouse 25.8's planner may swap such a join and build
--     its hash table from the states side: every model's states in memory at
--     once. On the department VM that took 2.96-3.41 GiB per statement (837 MiB
--     on the laptop, whose plan did not swap) and failed every rebuild after
--     the first daily file. The lookup takes 628-702 MiB there and 507-583 MiB
--     on the laptop, about twice as fast, with every statement's output
--     identical row for row on both machines' data.
-- --------------------------------------------------------------------------
INSERT INTO sqm.agg_filter_population
    (seq, dimension, value, models, bindings, unknown_device, malformed_imei, tac_matched, subscribers, sims, handsets)
WITH (SELECT (groupArray(tac), groupArray(deviceType)) FROM sqm.tac) AS m
SELECT
    {seq:UInt16}                        AS seq,
    'deviceType'                        AS dimension,
    transform(b.tac, m.1, m.2, '')      AS value,
    count()                             AS models,
    sum(b.bindings)                     AS bindings,
    sum(b.unknown_device)               AS unknown_device,
    sum(b.malformed_imei)               AS malformed_imei,
    sumIf(b.bindings, b.tac IN (SELECT tac FROM sqm.tac)) AS tac_matched,
    uniqMerge(b.subscribers)            AS subscribers,
    uniqMerge(b.sims)                   AS sims,
    uniqMerge(b.handsets)               AS handsets
FROM sqm.agg_tac_population AS b
WHERE b.seq = {seq:UInt16} AND value != ''
GROUP BY value;

INSERT INTO sqm.agg_filter_population
    (seq, dimension, value, models, bindings, unknown_device, malformed_imei, tac_matched, subscribers, sims, handsets)
WITH (SELECT (groupArray(tac), groupArray(trim(operatingSystem))) FROM sqm.tac) AS m
SELECT
    {seq:UInt16}                        AS seq,
    'operatingSystem'                   AS dimension,
    transform(b.tac, m.1, m.2, '')      AS value,
    count()                             AS models,
    sum(b.bindings)                     AS bindings,
    sum(b.unknown_device)               AS unknown_device,
    sum(b.malformed_imei)               AS malformed_imei,
    sumIf(b.bindings, b.tac IN (SELECT tac FROM sqm.tac)) AS tac_matched,
    uniqMerge(b.subscribers)            AS subscribers,
    uniqMerge(b.sims)                   AS sims,
    uniqMerge(b.handsets)               AS handsets
FROM sqm.agg_tac_population AS b
WHERE b.seq = {seq:UInt16} AND value != ''
GROUP BY value;

INSERT INTO sqm.agg_filter_population
    (seq, dimension, value, models, bindings, unknown_device, malformed_imei, tac_matched, subscribers, sims, handsets)
WITH (SELECT (groupArray(tac), groupArray(manufacturer)) FROM sqm.tac) AS m
SELECT
    {seq:UInt16}                        AS seq,
    'manufacturer'                      AS dimension,
    transform(b.tac, m.1, m.2, '')      AS value,
    count()                             AS models,
    sum(b.bindings)                     AS bindings,
    sum(b.unknown_device)               AS unknown_device,
    sum(b.malformed_imei)               AS malformed_imei,
    sumIf(b.bindings, b.tac IN (SELECT tac FROM sqm.tac)) AS tac_matched,
    uniqMerge(b.subscribers)            AS subscribers,
    uniqMerge(b.sims)                   AS sims,
    uniqMerge(b.handsets)               AS handsets
FROM sqm.agg_tac_population AS b
WHERE b.seq = {seq:UInt16} AND value != ''
GROUP BY value;

-- The vendor of each TAC is resolved in the lookup itself: a join of sqm.tac
-- to the vendor map, which carries no states.
INSERT INTO sqm.agg_filter_population
    (seq, dimension, value, models, bindings, unknown_device, malformed_imei, tac_matched, subscribers, sims, handsets)
WITH (
    SELECT (groupArray(t.tac), groupArray(coalesce(nullIf(v.vendor_canonical, ''), t.manufacturer)))
    FROM sqm.tac AS t
    LEFT JOIN (SELECT * FROM sqm.tac_vendor_map FINAL) AS v ON v.raw_manufacturer = t.manufacturer
) AS m
SELECT
    {seq:UInt16}                        AS seq,
    'vendor'                            AS dimension,
    transform(b.tac, m.1, m.2, '')      AS value,
    count()                             AS models,
    sum(b.bindings)                     AS bindings,
    sum(b.unknown_device)               AS unknown_device,
    sum(b.malformed_imei)               AS malformed_imei,
    sumIf(b.bindings, b.tac IN (SELECT tac FROM sqm.tac)) AS tac_matched,
    uniqMerge(b.subscribers)            AS subscribers,
    uniqMerge(b.sims)                   AS sims,
    uniqMerge(b.handsets)               AS handsets
FROM sqm.agg_tac_population AS b
WHERE b.seq = {seq:UInt16} AND value != ''
GROUP BY value;

INSERT INTO sqm.agg_filter_population
    (seq, dimension, value, models, bindings, unknown_device, malformed_imei, tac_matched, subscribers, sims, handsets)
SELECT
    {seq:UInt16}                        AS seq,
    'knownDevice'                       AS dimension,
    ''                                  AS value,
    count()                             AS models,
    sum(b.bindings)                     AS bindings,
    sum(b.unknown_device)               AS unknown_device,
    sum(b.malformed_imei)               AS malformed_imei,
    sumIf(b.bindings, b.tac IN (SELECT tac FROM sqm.tac)) AS tac_matched,
    uniqMerge(b.subscribers)            AS subscribers,
    uniqMerge(b.sims)                   AS sims,
    uniqMerge(b.handsets)               AS handsets
FROM sqm.agg_tac_population AS b
WHERE b.seq = {seq:UInt16} AND b.tac != ''
GROUP BY value;

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
FROM sqm.binding_current FINAL
WHERE active = 1;

-- --------------------------------------------------------------------------
-- 3. Capability flags per TAC.
--
--    Derived once over the device models rather than re-parsed for every one of the
--    126M bindings. Joining the full TAC table into that aggregation -- `bandDetails`
--    holds multi-hundred-character band lists -- exhausted a 6 GB server.
--
--    Read from the radio profiles (migration 029) since 2026-10-27: the parsing is done once
--    per GSMA version, in Sqm.Domain.Devices.RadioProfileParser, and tested there. Every flag is
--    1 supported, 0 not, 2 not stated. Two definitions changed with it, by the product owner's
--    decision:
--      * eSIM: '00' in the eUICC counts is "not stated", not zero. It was read as zero, so
--        every record predating the field was "known to have no eSIM".
--      * 5G: an NR band. "5G NA Options" alone - an architecture option and no band - no
--        longer counts; about a thousand records, feature phones among them, did.
--    LTE is any LTE, LTE-M and NB-IoT included, as the old search for "LTE" was.
-- --------------------------------------------------------------------------
TRUNCATE TABLE sqm.tac_capability;

INSERT INTO sqm.tac_capability (tac, has_lte, has_5g, has_esim, ims_emergency)
SELECT tac, lte, five_g, esim, ims_emergency
FROM sqm.tac_radio_active;

-- --------------------------------------------------------------------------
-- 4. Device-class mix, per measure.
--
--    The class mapping is deliberately coarse. The point of this widget is the
--    handset-versus-machine split, which a 19-category chart obscures. Since 2026-10-27 it
--    is one mapping, RadioProfileParser.DeviceClassOf, stored with each radio profile
--    (migration 029); it was written out three times here.
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
    multiIf(b.tac = '', 'Unknown device', r.tac = '', 'Unregistered TAC', r.device_class) AS device_class,
    count() AS n
FROM sqm.binding_current AS b FINAL
LEFT JOIN (SELECT tac, device_class FROM sqm.tac_radio_active) AS r ON r.tac = b.tac
WHERE b.active = 1
GROUP BY device_class;

INSERT INTO sqm.agg_device_class_daily (seq, data_date, measure, device_class, n)
SELECT
    {seq:UInt16}, NULL, 'subscribers',
    multiIf(b.tac = '', 'Unknown device', r.tac = '', 'Unregistered TAC', r.device_class) AS device_class,
    uniq(b.msisdn) AS n
FROM sqm.binding_current AS b FINAL
LEFT JOIN (SELECT tac, device_class FROM sqm.tac_radio_active) AS r ON r.tac = b.tac
WHERE b.active = 1
GROUP BY device_class;

INSERT INTO sqm.agg_device_class_daily (seq, data_date, measure, device_class, n)
SELECT
    {seq:UInt16}, NULL, 'handsets',
    multiIf(b.tac = '', 'Unknown device', r.tac = '', 'Unregistered TAC', r.device_class) AS device_class,
    uniqIf(b.imei, length(b.imei) = 14) AS n
FROM sqm.binding_current AS b FINAL
LEFT JOIN (SELECT tac, device_class FROM sqm.tac_radio_active) AS r ON r.tac = b.tac
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
            countIf(c.tac = '' OR c.has_lte = 2) AS x0,
            countIf(c.tac != '' AND c.has_5g = 1) AS s1,
            countIf(c.tac != '' AND c.has_5g = 0) AS u1,
            countIf(c.tac = '' OR c.has_5g = 2) AS x1,
            countIf(c.tac != '' AND c.has_esim = 1) AS s2,
            countIf(c.tac != '' AND c.has_esim = 0) AS u2,
            countIf(c.tac = '' OR c.has_esim = 2) AS x2,
            countIf(c.tac != '' AND c.ims_emergency = 1) AS s3,
            countIf(c.tac != '' AND c.ims_emergency = 0) AS u3,
            countIf(c.tac = '' OR c.ims_emergency = 2) AS x3
        FROM sqm.binding_current AS b FINAL
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
            uniqIf(b.msisdn, c.tac = '' OR c.has_lte = 2) AS x0,
            uniqIf(b.msisdn, c.tac != '' AND c.has_5g = 1) AS s1,
            uniqIf(b.msisdn, c.tac != '' AND c.has_5g = 0) AS u1,
            uniqIf(b.msisdn, c.tac = '' OR c.has_5g = 2) AS x1,
            uniqIf(b.msisdn, c.tac != '' AND c.has_esim = 1) AS s2,
            uniqIf(b.msisdn, c.tac != '' AND c.has_esim = 0) AS u2,
            uniqIf(b.msisdn, c.tac = '' OR c.has_esim = 2) AS x2,
            uniqIf(b.msisdn, c.tac != '' AND c.ims_emergency = 1) AS s3,
            uniqIf(b.msisdn, c.tac != '' AND c.ims_emergency = 0) AS u3,
            uniqIf(b.msisdn, c.tac = '' OR c.ims_emergency = 2) AS x3
        FROM sqm.binding_current AS b FINAL
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
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac = '' OR c.has_lte = 2)) AS x0,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac != '' AND c.has_5g = 1)) AS s1,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac != '' AND c.has_5g = 0)) AS u1,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac = '' OR c.has_5g = 2)) AS x1,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac != '' AND c.has_esim = 1)) AS s2,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac != '' AND c.has_esim = 0)) AS u2,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac = '' OR c.has_esim = 2)) AS x2,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac != '' AND c.ims_emergency = 1)) AS s3,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac != '' AND c.ims_emergency = 0)) AS u3,
            uniqIf(b.imei, length(b.imei) = 14 AND (c.tac = '' OR c.ims_emergency = 2)) AS x3
        FROM sqm.binding_current AS b FINAL
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
FROM sqm.binding_current AS b FINAL
LEFT JOIN sqm.tac AS t ON t.tac = b.tac
LEFT JOIN (SELECT * FROM sqm.tac_vendor_map FINAL) AS v ON v.raw_manufacturer = t.manufacturer
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
FROM sqm.binding_current AS b FINAL
LEFT JOIN sqm.tac AS t ON t.tac = b.tac
LEFT JOIN (SELECT * FROM sqm.tac_vendor_map FINAL) AS v ON v.raw_manufacturer = t.manufacturer
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
FROM sqm.binding_current AS b FINAL
LEFT JOIN sqm.tac AS t ON t.tac = b.tac
LEFT JOIN (SELECT * FROM sqm.tac_vendor_map FINAL) AS v ON v.raw_manufacturer = t.manufacturer
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
FROM sqm.binding_current AS b FINAL
LEFT JOIN sqm.tac AS t ON t.tac = b.tac
LEFT JOIN (SELECT * FROM sqm.tac_vendor_map FINAL) AS v ON v.raw_manufacturer = t.manufacturer
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
FROM sqm.binding_current AS b FINAL
LEFT JOIN sqm.tac AS t ON t.tac = b.tac
LEFT JOIN (SELECT * FROM sqm.tac_vendor_map FINAL) AS v ON v.raw_manufacturer = t.manufacturer
WHERE b.active = 1
GROUP BY dim_value;

-- The TAC slice is the one dimension that is NOT a fresh aggregate over 295M rows.
-- Section 1b has already grouped current state by TAC with the same filter and the same
-- estimator, so this reads 98,000 rows instead of repeating the pass. Two statements
-- computing the same per-TAC counts from the same source could only ever agree or be a
-- bug; one statement cannot disagree with itself.
INSERT INTO sqm.agg_dimension_daily
    (seq, data_date, dimension, dim_value, bindings, subscribers, handsets)
SELECT
    {seq:UInt16}, NULL, 'tac',
    if(tac = '', '(unknown device)', tac) AS dim_value,
    bindings,
    subscribers,
    handsets
FROM sqm.agg_device_model
WHERE seq = {seq:UInt16};
