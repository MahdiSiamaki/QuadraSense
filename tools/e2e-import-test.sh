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
# Usage: SQM_USER=... SQM_PASSWORD=... tools/e2e-import-test.sh [api-base-url]
#
# The account needs import upload permission for SQM. Uploads need a signed-in session and the
# CSRF token that comes with it; without them every request is a 401 and the test cannot start.
# The container names default to infra/docker-compose.yml's and can be overridden.
set -e

API="${1:-http://localhost:5202}"
PG="${SQM_PG_CONTAINER:-sqm-postgres}"
CH="${SQM_CH_CONTAINER:-sqm-clickhouse}"
DAY="2026-06-14"
FILE="E:/job/QuadraSense/SQM/New CDR/New CDR/daily_subs_device_sim_info_${DAY}.csv"

q_ch() { docker exec "$CH" clickhouse-client --query "$1"; }
q_pg() { docker exec "$PG" psql -U sqm -d sqm -tAc "$1"; }

[ -n "$SQM_USER" ] && [ -n "$SQM_PASSWORD" ] \
  || { echo "FAIL: set SQM_USER and SQM_PASSWORD to an account that may upload SQM files"; exit 1; }

JAR=$(mktemp)
trap 'rm -f "$JAR"' EXIT

json_escape() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

LOGIN=$(curl -s -o /dev/null -w "%{http_code}" -c "$JAR" -H "Content-Type: application/json" \
  -d "{\"username\":\"$(json_escape "$SQM_USER")\",\"password\":\"$(json_escape "$SQM_PASSWORD")\"}" \
  "${API}/api/v1/auth/login")
[ "$LOGIN" = "200" ] || { echo "FAIL: sign-in returned HTTP $LOGIN"; exit 1; }

# The CSRF cookie is '__Host-sqm_csrf', or 'sqm_csrf' where development drops the prefix.
CSRF=$(awk '$6 ~ /sqm_csrf$/ { print $7 }' "$JAR")
[ -n "$CSRF" ] || { echo "FAIL: the sign-in set no CSRF cookie"; exit 1; }

upload() { curl -s -b "$JAR" -H "X-CSRF-Token: $CSRF" -X POST -F "file=@${FILE}" "$@" \
  "${API}/api/v1/imports/SQM/upload"; }

echo "=============================================================="
echo " End-to-end import test - $DAY"
echo "=============================================================="

BEFORE=$(q_ch "SELECT count() FROM sqm.binding_event WHERE data_date = '$DAY'")
echo "events currently loaded for $DAY: $BEFORE"

echo
echo "--- 1. duplicate detection: upload a file the platform already knows -------"
DUP=$(upload -o /dev/null -w "%{http_code}")
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
RESPONSE=$(upload)
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

# Equal counts alone prove nothing: a job that failed or was quarantined before it touched the
# day - or one still running when the wait ran out - leaves the count exactly as it was.
if [ "$STATUS" != "COMPLETED" ]; then
  echo
  echo "FAIL - the import ended $STATUS, not COMPLETED."
  exit 1
elif [ "$BEFORE" = "$AFTER" ]; then
  echo
  echo "PASS - the day was replaced, not doubled and not truncated."
else
  echo
  echo "FAIL - the count changed. Idempotency is broken."
  exit 1
fi
