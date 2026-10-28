-- 031_planning_lifecycle.sql
--
-- Handset lifecycle (N4), built by the planning run beside its other measures: when each handset
-- held now was first seen on the network, which model each number moved from and to, and how long a
-- number kept a handset before moving. docs/architecture/19-network-planning.md section 6.
--
-- WHAT A CHANGE IS - the product owner's decision of 2026-10-27, taken from a measurement. On one
-- thirty-second of the numbers, among those with exactly two handsets over time, the old handset most
-- often left the day after the new one arrived (41,923 numbers); the count per day of overlap was
-- flat by days 4-7, and 63,610 numbers kept both for more than 30 days. So handset B replaces A on a
-- number when A leaves at most 7 days after B arrives - a longer overlap is two devices used side by
-- side, not a change - or A left at most 30 days before B arrived; after a longer gap the number may
-- have passed to someone else, and A ended with no successor.
--
-- WHAT A HANDSET ON A NUMBER IS - decided 2026-10-27, from a measurement. Of one chunk's 18.4M
-- spells (an IMEI on a number, first day to last), 59.3% lasted 0-1 days and 5.6% 2-3 days, then a
-- flat tail (1.6% 4-7 days, 1.1% 8-14); 91% of the one-day ones were smartphones, crowded onto
-- numbers with many handsets. A SIM tried in another phone for a day is not a handset change, so a
-- handset counts as the number's only if it stayed at least 7 days; shorter sightings are left out
-- altogether - nothing changes into or out of them.
--
-- WHAT IS NOT COUNTED. A dual-SIM handset's second IMEI is the same handset: the higher of a pair
-- (the same TAC and a serial one apart, or a TAC a hundred apart and the same serial) is set aside.
-- Shifted IMEIs and unknown devices are not handsets. A number with more than 40 such IMEIs, or 20
-- handsets once the twins are set aside (SIMs listed under many numbers, test SIMs), is counted as
-- not assessed rather than read as twenty changes.
--
-- THE OBSERVATION WINDOW. The binding history starts with the initial dump, a month of observations
-- without dates (2025-12-27..2026-01-25): a handset in it was first seen "before 2026-01-26", and how
-- long a number had held it is unknown - its spell is reported apart, never as a cohort. A spell still
-- open at the run's delivery is censored there; one that ended with no successor is censored where it
-- ended.
--
-- EXACT COUNTS, no uniq() states: a number is in one number chunk and an IMEI in one IMEI chunk.

-- A chunk's bindings over all time, one row per binding: its first day from the history and its end
-- from current state. Filled and emptied within the chunk, like planning_handset_stage.
CREATE TABLE IF NOT EXISTS sqm.planning_spell_stage
(
    run_id          UInt64,
    chunk           UInt16,
    msisdn          UInt64,
    imei            String,
    imsi            UInt64,
    start           Date,            -- the binding's first day; 2025-12-27 for one in the initial dump
    finish          Date,            -- its removal; 2100-01-01 while it is active
    dump            UInt8            -- 1 when the initial dump listed it
)
ENGINE = MergeTree
PARTITION BY (run_id, chunk)
ORDER BY (msisdn, imei, imsi);

-- The chunk's spells that count: one row per (number, IMEI) that stayed at least 7 days, from the
-- stage by a key-ordered aggregation, so a number's handsets arrive together. Filled and emptied
-- within the chunk.
CREATE TABLE IF NOT EXISTS sqm.planning_spell
(
    run_id          UInt64,
    chunk           UInt16,
    msisdn          UInt64,
    imei            String,
    start           Date,
    finish          Date,            -- 2100-01-01 while held
    dump            UInt8
)
ENGINE = MergeTree
PARTITION BY (run_id, chunk)
ORDER BY (msisdn, imei);

-- Changes of handset, by the month the new one arrived and the two models. kind: 1 a newer model
-- (later GSMA allocation date), 2 an older one, 3 neither can be said (same date, or one not stated),
-- 4 the same model again.
CREATE TABLE IF NOT EXISTS sqm.planning_change
(
    run_id          UInt64,
    chunk           UInt16,
    month           UInt32,          -- YYYYMM of the new handset's first day on the number
    from_tac        String,
    to_tac          String,
    kind            UInt8,
    changes         UInt64
)
ENGINE = MergeTree
PARTITION BY (run_id, chunk)
ORDER BY (month, from_tac, to_tac, kind);

-- Every spell of a handset on a number, as survival data: the cohort it began in, how many days it
-- lasted, and how it ended. outcome: 1 replaced (kind as above), 2 ended with no successor, 3 still
-- held at the run's delivery, 4 a number not assessed (days 0, counted once per number). cohort 0 is the initial dump.
CREATE TABLE IF NOT EXISTS sqm.planning_tenure
(
    run_id          UInt64,
    chunk           UInt16,
    cohort          UInt32,          -- YYYYMM of the spell's first day; 0 for the initial dump
    days            UInt16,
    outcome         UInt8,
    kind            UInt8,           -- for outcome 1; 0 otherwise
    spells          UInt64
)
ENGINE = MergeTree
PARTITION BY (run_id, chunk)
ORDER BY (cohort, outcome, kind, days);

-- Handsets held now, by model and the month the network first saw them (0: in the initial dump).
CREATE TABLE IF NOT EXISTS sqm.planning_device_age
(
    run_id          UInt64,
    chunk           UInt16,
    tac             String,
    first_month     UInt32,
    handsets        UInt64
)
ENGINE = MergeTree
PARTITION BY (run_id, chunk)
ORDER BY (tac, first_month);
