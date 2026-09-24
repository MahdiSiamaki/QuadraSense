-- ---------------------------------------------------------------------------
-- A daily import whose every row landed is COMPLETED, warnings or not.
--
-- PARTIALLY_COMPLETED used to mean "anything was rejected OR WARNED". Every
-- daily file carries a few thousand IMEIs that are neither 14 digits nor the
-- 000000 sentinel, so all 60 days imported through the platform read partially
-- completed while not one row of 422.9 million had been rejected. A status that
-- is always the same says nothing.
--
-- Decided by the product owner, 2026-09-24: PARTIALLY_COMPLETED means rows were
-- rejected and are not in the data. The rule is SqmDailyProcessor.StatusFor;
-- this brings the days imported under the old rule into line, so the history
-- does not mean two things at once.
--
-- Narrow on purpose:
--   * SQM only. For a TAC snapshot PARTIALLY_COMPLETED means "loaded, not yet
--     activated", which this decision did not touch.
--   * rows_rejected = 0 only. A NULL count is left alone: unknown is not none.
--
-- Measured on the laptop's database before writing this: 60 SQM jobs match,
-- every one with rows_rejected = 0.
--
-- Nothing is lost. warning_count and imports.quarantine_rule_summary still hold
-- every warning each of these days raised.
-- ---------------------------------------------------------------------------

UPDATE imports.import_job
   SET status = 'COMPLETED'
 WHERE source_code   = 'SQM'
   AND status        = 'PARTIALLY_COMPLETED'
   AND rows_rejected = 0;
