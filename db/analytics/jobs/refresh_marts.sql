-- refresh_marts.sql
-- Rebuilds the dashboard marts from binding_current.
--
-- Run after each delivery is folded in.
--
-- ONE READ OF binding_current. Section 1b groups it by TAC and keeps the distinct counts as
-- uniq() states, and everything else is merged from those. Every dashboard figure is a property
-- of the TAC or of the whole population, so nothing needs the table twice. It used to be read 14
-- times, 66 GiB and about 9 minutes each on the department VM: 2 h 12 min of an import
-- (`docs/research/2026-11-worker-capacity.md`). Merged, the marts came out identical to the digit
-- on both machines' data, and a whole refresh took 514 s on the laptop where it took 50-68 minutes.
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
-- FINAL costs a merge-on-read over every version of every binding. It is paid once per delivery,
-- against figures that cannot silently double. It is not close.
-- --------------------------------------------------------------------------

ALTER TABLE sqm.agg_kpi_daily    DROP PARTITION {seq:UInt16};
ALTER TABLE sqm.agg_device_model DROP PARTITION {seq:UInt16};
ALTER TABLE sqm.agg_tac_population DROP PARTITION {seq:UInt16};
ALTER TABLE sqm.agg_filter_population DROP PARTITION {seq:UInt16};

-- --------------------------------------------------------------------------
-- 1b. Device model rollup: one row per (seq, tac), active bindings only.
--
--     What the Devices module reads. ~98,000 rows, so sorting, filtering and
--     paging a device catalogue is a scan of something tiny instead of a 2.35 s
--     aggregate over 295M rows per keystroke.
--
--     It carries four counts, and the distinct ones cannot be summed later:
--     handsets, SIMs and subscribers do
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
-- 2. Headline counters. A merge of every model's states is uniq() over the union:
--    the same estimator, and the same figure to the digit (2026-11-05).
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
    sum(b.bindings)                                  AS active_bindings,
    uniqMerge(b.subscribers)                         AS distinct_subscribers,
    -- Real devices only: the states hold 14-digit IMEIs, so '000000', one literal value shared by
    -- millions of rows, adds no phantom handset to the total.
    uniqMerge(b.handsets)                            AS distinct_devices,
    sum(b.unknown_device)                            AS unknown_device_bindings,
    -- Numeric but not 14 digits, so no TAC can be derived. A defect, unlike the sentinel above.
    sum(b.malformed_imei)                            AS malformed_imei_bindings,
    sumIf(b.bindings, b.tac != '' AND b.tac IN (SELECT tac FROM sqm.tac)) AS tac_matched_bindings,
    uniqMerge(b.sims)                                AS distinct_sims
FROM sqm.agg_tac_population AS b
-- b.seq, not seq: "AS seq" above would win, and the condition would be 247 = 247 for every delivery.
WHERE b.seq = {seq:UInt16};

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
WITH (SELECT (groupArray(tac), groupArray(device_class)) FROM sqm.tac_radio_active) AS m
SELECT {seq:UInt16}, NULL, measure, device_class, n
FROM (
    SELECT
        multiIf(b.tac = '', 'Unknown device', transform(b.tac, m.1, m.2, 'Unregistered TAC')) AS device_class,
        toUInt64(sum(b.bindings))       AS by_bindings,
        uniqMerge(b.subscribers)        AS by_subscribers,
        uniqMerge(b.handsets)           AS by_handsets
    FROM sqm.agg_tac_population AS b
    WHERE b.seq = {seq:UInt16}
    GROUP BY device_class
)
ARRAY JOIN
    ['bindings', 'subscribers', 'handsets'] AS measure,
    [by_bindings, by_subscribers, by_handsets] AS n;

-- --------------------------------------------------------------------------
-- 5. Network / SIM capability, per measure.
--
--    Capabilities overlap (a device can be LTE and 5G and eSIM), so each row is an
--    independent supported/unsupported/unknown split rather than a slice of a whole.
--
--    'unknown' is carried explicitly instead of being folded into 'unsupported'.
--    Conflating them would claim we know a device lacks 5G when we do not know what the
--    device is. It holds real handsets only: a row with no handset has no capability to
--    know, and no count includes it (ADR-020).
--
--    VoLTE is absent because the dataset does not contain it: bandDetails mentions it in
--    2 rows of 270,166, and the IMS columns describe emergency calling, a different
--    capability, 'Not Known' for 94% of TACs.
--
--    One statement: each model's four flags are looked up by TAC, and the twelve figures of
--    each measure are conditional merges of the states. A flag of 9 is a TAC with no record.
-- --------------------------------------------------------------------------
ALTER TABLE sqm.agg_capability_daily DROP PARTITION {seq:UInt16};

INSERT INTO sqm.agg_capability_daily
    (seq, data_date, measure, capability, supported, unsupported, unknown)
WITH (
    SELECT (groupArray(tac), groupArray(has_lte), groupArray(has_5g), groupArray(has_esim), groupArray(ims_emergency))
    FROM sqm.tac_capability
) AS m
SELECT {seq:UInt16}, NULL, row.1, row.2, row.3, row.4, row.5
FROM (
    SELECT arrayJoin([
            ('bindings', 'LTE', bs0, bu0, bx0),
            ('bindings', '5G', bs1, bu1, bx1),
            ('bindings', 'eSIM', bs2, bu2, bx2),
            ('bindings', 'IMS emergency calling', bs3, bu3, bx3),
            ('subscribers', 'LTE', ss0, su0, sx0),
            ('subscribers', '5G', ss1, su1, sx1),
            ('subscribers', 'eSIM', ss2, su2, sx2),
            ('subscribers', 'IMS emergency calling', ss3, su3, sx3),
            ('handsets', 'LTE', hs0, hu0, hx0),
            ('handsets', '5G', hs1, hu1, hx1),
            ('handsets', 'eSIM', hs2, hu2, hx2),
            ('handsets', 'IMS emergency calling', hs3, hu3, hx3)
    ]) AS row
    FROM (
        SELECT
            toUInt64(sumIf(bindings, f0 = 1)) AS bs0, toUInt64(sumIf(bindings, f0 = 0)) AS bu0,
            toUInt64(sumIf(bindings, tac != '' AND f0 >= 2)) AS bx0,
            toUInt64(sumIf(bindings, f1 = 1)) AS bs1, toUInt64(sumIf(bindings, f1 = 0)) AS bu1,
            toUInt64(sumIf(bindings, tac != '' AND f1 >= 2)) AS bx1,
            toUInt64(sumIf(bindings, f2 = 1)) AS bs2, toUInt64(sumIf(bindings, f2 = 0)) AS bu2,
            toUInt64(sumIf(bindings, tac != '' AND f2 >= 2)) AS bx2,
            toUInt64(sumIf(bindings, f3 = 1)) AS bs3, toUInt64(sumIf(bindings, f3 = 0)) AS bu3,
            toUInt64(sumIf(bindings, tac != '' AND f3 >= 2)) AS bx3,
            uniqMergeIf(subscribers, f0 = 1) AS ss0, uniqMergeIf(subscribers, f0 = 0) AS su0,
            uniqMergeIf(subscribers, tac != '' AND f0 >= 2) AS sx0,
            uniqMergeIf(subscribers, f1 = 1) AS ss1, uniqMergeIf(subscribers, f1 = 0) AS su1,
            uniqMergeIf(subscribers, tac != '' AND f1 >= 2) AS sx1,
            uniqMergeIf(subscribers, f2 = 1) AS ss2, uniqMergeIf(subscribers, f2 = 0) AS su2,
            uniqMergeIf(subscribers, tac != '' AND f2 >= 2) AS sx2,
            uniqMergeIf(subscribers, f3 = 1) AS ss3, uniqMergeIf(subscribers, f3 = 0) AS su3,
            uniqMergeIf(subscribers, tac != '' AND f3 >= 2) AS sx3,
            uniqMergeIf(handsets, f0 = 1) AS hs0, uniqMergeIf(handsets, f0 = 0) AS hu0,
            uniqMergeIf(handsets, f0 >= 2) AS hx0,
            uniqMergeIf(handsets, f1 = 1) AS hs1, uniqMergeIf(handsets, f1 = 0) AS hu1,
            uniqMergeIf(handsets, f1 >= 2) AS hx1,
            uniqMergeIf(handsets, f2 = 1) AS hs2, uniqMergeIf(handsets, f2 = 0) AS hu2,
            uniqMergeIf(handsets, f2 >= 2) AS hx2,
            uniqMergeIf(handsets, f3 = 1) AS hs3, uniqMergeIf(handsets, f3 = 0) AS hu3,
            uniqMergeIf(handsets, f3 >= 2) AS hx3
        FROM (
            SELECT
                tac, bindings, subscribers, handsets,
                transform(tac, m.1, m.2, toUInt8(9)) AS f0,
                transform(tac, m.1, m.3, toUInt8(9)) AS f1,
                transform(tac, m.1, m.4, toUInt8(9)) AS f2,
                transform(tac, m.1, m.5, toUInt8(9)) AS f3
            FROM sqm.agg_tac_population
            WHERE seq = {seq:UInt16}
        )
    )
);

-- --------------------------------------------------------------------------
-- 6. Per-dimension counts, three ways.
--
--    Distinct counts cannot be derived at query time (see migration 009), so they are
--    materialised here for every dimension a breakdown can group by, merged from the states
--    the way section 1c is: each model's value looked up by TAC, no join carrying the states.
--
--    One statement per dimension, so each keeps a hash table of its own values only.
--
--    The '(unknown device)' and '(unknown TAC)' labels are produced here rather than in the
--    API, so every consumer of this table sees the same buckets.
-- --------------------------------------------------------------------------
ALTER TABLE sqm.agg_dimension_daily DROP PARTITION {seq:UInt16};

INSERT INTO sqm.agg_dimension_daily
    (seq, data_date, dimension, dim_value, bindings, subscribers, handsets)
WITH (
    SELECT (groupArray(t.tac),
            groupArray(coalesce(coalesce(nullIf(v.vendor_canonical, ''), nullIf(t.manufacturer, '')), '(unknown TAC)')))
    FROM sqm.tac AS t
    LEFT JOIN (SELECT * FROM sqm.tac_vendor_map FINAL) AS v ON v.raw_manufacturer = t.manufacturer
) AS m
SELECT
    {seq:UInt16}, NULL, 'vendor',
    multiIf(b.tac = '', '(unknown device)', transform(b.tac, m.1, m.2, '(unknown TAC)')) AS dim_value,
    sum(b.bindings)                AS bindings,
    uniqMerge(b.subscribers)       AS subscribers,
    uniqMerge(b.handsets)          AS handsets
FROM sqm.agg_tac_population AS b
WHERE b.seq = {seq:UInt16}
GROUP BY dim_value;

INSERT INTO sqm.agg_dimension_daily
    (seq, data_date, dimension, dim_value, bindings, subscribers, handsets)
WITH (SELECT (groupArray(tac), groupArray(coalesce(nullIf(manufacturer, ''), '(unknown TAC)'))) FROM sqm.tac) AS m
SELECT
    {seq:UInt16}, NULL, 'manufacturer',
    multiIf(b.tac = '', '(unknown device)', transform(b.tac, m.1, m.2, '(unknown TAC)')) AS dim_value,
    sum(b.bindings)                AS bindings,
    uniqMerge(b.subscribers)       AS subscribers,
    uniqMerge(b.handsets)          AS handsets
FROM sqm.agg_tac_population AS b
WHERE b.seq = {seq:UInt16}
GROUP BY dim_value;

INSERT INTO sqm.agg_dimension_daily
    (seq, data_date, dimension, dim_value, bindings, subscribers, handsets)
WITH (SELECT (groupArray(tac), groupArray(coalesce(nullIf(marketingName, ''), '(unknown TAC)'))) FROM sqm.tac) AS m
SELECT
    {seq:UInt16}, NULL, 'model',
    multiIf(b.tac = '', '(unknown device)', transform(b.tac, m.1, m.2, '(unknown TAC)')) AS dim_value,
    sum(b.bindings)                AS bindings,
    uniqMerge(b.subscribers)       AS subscribers,
    uniqMerge(b.handsets)          AS handsets
FROM sqm.agg_tac_population AS b
WHERE b.seq = {seq:UInt16}
GROUP BY dim_value;

INSERT INTO sqm.agg_dimension_daily
    (seq, data_date, dimension, dim_value, bindings, subscribers, handsets)
WITH (SELECT (groupArray(tac), groupArray(coalesce(nullIf(deviceType, ''), '(unknown TAC)'))) FROM sqm.tac) AS m
SELECT
    {seq:UInt16}, NULL, 'deviceType',
    multiIf(b.tac = '', '(unknown device)', transform(b.tac, m.1, m.2, '(unknown TAC)')) AS dim_value,
    sum(b.bindings)                AS bindings,
    uniqMerge(b.subscribers)       AS subscribers,
    uniqMerge(b.handsets)          AS handsets
FROM sqm.agg_tac_population AS b
WHERE b.seq = {seq:UInt16}
GROUP BY dim_value;

INSERT INTO sqm.agg_dimension_daily
    (seq, data_date, dimension, dim_value, bindings, subscribers, handsets)
WITH (SELECT (groupArray(tac), groupArray(coalesce(nullIf(trim(operatingSystem), ''), '(unknown TAC)'))) FROM sqm.tac) AS m
SELECT
    {seq:UInt16}, NULL, 'os',
    multiIf(b.tac = '', '(unknown device)', transform(b.tac, m.1, m.2, '(unknown TAC)')) AS dim_value,
    sum(b.bindings)                AS bindings,
    uniqMerge(b.subscribers)       AS subscribers,
    uniqMerge(b.handsets)          AS handsets
FROM sqm.agg_tac_population AS b
WHERE b.seq = {seq:UInt16}
GROUP BY dim_value;

-- The TAC slice needs no merge: section 1b has already grouped by TAC.
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
