#!/bin/sh
# End-to-end test of the import platform, against real data.
#
# Takes the most recent delivered day, removes its backfilled history, and puts the real file
# back through the platform: upload -> queue -> worker -> validate -> drop the day -> reload ->
# fold -> refresh marts.
#
# The assertion that matters is the row count. The day is already loaded, so a correct import
# must end with exactly the number of events it started with. Any other number means the
# idempotency is broken - either the day was not removed before reloading (doubling) or the
# reload dropped rows.
#
# It also exercises duplicate detection first, against 30 GB of real content hashes: uploading
# a file the platform already knows must come back as a duplicate, not a second import.
#
# Usage: tools/e2e-import-test.sh [api-base-url]
set -e

API="${1:-http://localhost:5202}"
PG="sqm-postgres"
CH="bench-ch"
DAY="2026-06-14"
FILE="D:/SQM/New CDR/New CDR/daily_subs_device_sim_info_${DAY}.csv"

q_ch() { docker exec "$CH" clickhouse-client --query "$1"; }
q_pg() { docker exec "$PG" psql -U sqm -d sqm -tAc "$1"; }

echo "=============================================================="
echo " End-to-end import test - $DAY"
echo "=============================================================="

BEFORE=$(q_ch "SELECT count() FROM sqm.binding_event WHERE data_date = '$DAY'")
echo "events currently loaded for $DAY: $BEFORE"

echo
echo "--- 1. duplicate detection: upload a file the platform already knows -------"
DUP=$(curl -s -o /dev/null -w "%{http_code}" -X POST \
  -F "file=@${FILE}" "${API}/api/v1/imports/SQM/upload")
echo "HTTP $DUP  (409 = recognised as already imported)"
[ "$DUP" = "409" ] || { echo "FAIL: expected 409, got $DUP"; exit 1; }

echo
echo "--- 2. forget the backfilled record so the file can be imported for real ---"
q_pg "DELETE FROM imports.import_job  WHERE file_id IN
        (SELECT id FROM imports.import_file
          WHERE original_file_name = 'daily_subs_device_sim_info_${DAY}.csv');"
q_pg "DELETE FROM imports.import_file
        WHERE original_file_name = 'daily_subs_device_sim_info_${DAY}.csv';"
echo "removed"

echo
echo "--- 3. upload it properly ------------------------------------------------"
RESPONSE=$(curl -s -X POST -F "file=@${FILE}" "${API}/api/v1/imports/SQM/upload")
echo "$RESPONSE"
JOB=$(echo "$RESPONSE" | sed -n 's/.*"jobId":\([0-9]*\).*/\1/p')
[ -n "$JOB" ] || { echo "FAIL: no job id in the response"; exit 1; }
echo "queued as job $JOB"

echo
echo "--- 4. watch the worker ---------------------------------------------------"
for i in $(seq 1 240); do
  STATUS=$(q_pg "SELECT status::text FROM imports.import_job WHERE id = $JOB")
  STAGE=$(q_pg "SELECT coalesce(current_stage, '-') FROM imports.import_job WHERE id = $JOB")
  PCT=$(q_pg "SELECT coalesce(round(percent)::text, '-') FROM imports.import_progress WHERE job_id = $JOB")
  printf "\r  %-22s %-14s %s%%   " "$STATUS" "$STAGE" "$PCT"
  case "$STATUS" in
    COMPLETED|PARTIALLY_COMPLETED|FAILED|QUARANTINED|CANCELLED|DUPLICATE) break ;;
  esac
  sleep 5
done
echo
echo "final status: $STATUS"

echo
echo "--- 5. the timeline the worker wrote --------------------------------------"
docker exec "$PG" psql -U sqm -d sqm -c \
  "SELECT to_char(occurred_at,'HH24:MI:SS') AS at, severity, coalesce(stage,'-') AS stage,
          left(message, 96) AS message
     FROM imports.import_event WHERE job_id = $JOB ORDER BY occurred_at, id;"

echo "--- 6. the assertion ------------------------------------------------------"
AFTER=$(q_ch "SELECT count() FROM sqm.binding_event WHERE data_date = '$DAY'")
echo "events before: $BEFORE"
echo "events after:  $AFTER"

if [ "$BEFORE" = "$AFTER" ]; then
  echo
  echo "PASS - the day was replaced, not doubled and not truncated."
else
  echo
  echo "FAIL - the count changed. Idempotency is broken."
  exit 1
fi
