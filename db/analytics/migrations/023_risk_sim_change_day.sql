-- 023_risk_sim_change_day.sql
--
-- The numbers whose SIM changed on a day, one row each: the rows behind the dashboard's daily
-- "SIM changes" count, kept so that Phase 4 can list them, with the SIMs and IMEIs on each side.
--
-- THE DEFINITION IS THE DASHBOARD'S, AND SAME-DAY (migration 016; product owner, 2026-10-02).
-- In one daily file, the number had a remove carrying one IMSI and an add carrying an IMSI that
-- was not also removed that day. A change spread over two files is counted on neither day, and
-- the risk pages say so. Because it is the same condition, the rows of a day reconcile exactly with
-- agg_sim_change_daily.msisdn_changed for that day - an integration test holds them to it.
--
-- FEED DEFECTS ARE SET ASIDE, NOT DROPPED. From 2026-07-27 the operator's files list SIMs under
-- several numbers on one day (up to 383,000 SIMs a day against 500-900 ordinarily), and every such
-- row can look like a SIM arriving on a number. A change whose new or previous IMSI is in that
-- day's dq_multi_number_sim_day is kept, with set_aside = 'multi_number', so it is counted on the
-- data-quality side and never listed as behaviour. When the day's feed-quality rows are missing,
-- the change is kept with set_aside = 'dq_unavailable' and counted nowhere until refreshed.
--
-- BUILT PER DAY, BY THE IMPORT, after the feed-quality step that writes the day's
-- dq_multi_number_sim_day partition. It reads only that day's event partition, so it does not
-- depend on the order days arrive in. Idempotent by partition: DROP PARTITION, then INSERT.
-- Size: about 100,000-140,000 rows a day, measured as agg_sim_change_daily over September.

CREATE TABLE IF NOT EXISTS sqm.risk_sim_change_day
(
    data_date   Date,
    msisdn      UInt64,

    -- The IMSIs the number gained (added, not also removed) and lost that day. Almost always one
    -- each; capped at 5, with the true counts beside them.
    new_imsis   Array(UInt64),
    old_imsis   Array(UInt64),
    new_count   UInt16,
    old_count   UInt16,

    -- An IMEI on each side, for the list's "previous device" and "new device" columns. Raw as the
    -- feed sent them: a sentinel or shifted IMEI is shown as a feed defect, and does not make the
    -- change any less of a SIM change.
    new_imei    String,
    old_imei    String,

    set_aside   Enum8('none' = 0, 'multi_number' = 1, 'dq_unavailable' = 2),
    computed_at DateTime64(3)
)
ENGINE = MergeTree
PARTITION BY data_date
ORDER BY (data_date, msisdn);
