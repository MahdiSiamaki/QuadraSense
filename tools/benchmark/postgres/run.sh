#!/bin/bash
# PostgreSQL benchmark runner. Executed inside the bench-pg container.
set -u
PSQL="psql -U postgres -d bench -q"

t() { local label="$1"; shift
      local s=$(date +%s%N)
      "$@" >/tmp/out.txt 2>/tmp/err.txt; local rc=$?
      local e=$(date +%s%N)
      printf '%-36s %9.2fs rc=%s\n' "$label" "$(echo "scale=2; ($e-$s)/1000000000" | bc)" "$rc"
      [ $rc -ne 0 ] && head -3 /tmp/err.txt
      return 0
}

echo "================= L1 TRANSFORM ================="
t "stage -> binding_current" $PSQL -c "
INSERT INTO sqm.binding_current (msisdn, imsi, imei)
SELECT msisdn::bigint, imsi::bigint, imei FROM sqm.stage_base
WHERE msisdn ~ '^[0-9]{1,18}\$' AND imsi ~ '^[0-9]{1,18}\$';"

t "load TAC" $PSQL -c "
\\copy sqm.tac FROM '/data/tac/DeviceDatabase_TAC1Sep2026.csv' WITH (FORMAT csv, HEADER true)"

t "PK index on binding_current" $PSQL -c "
ALTER TABLE sqm.binding_current ADD PRIMARY KEY (msisdn, imsi, imei);"

t "index on tac" $PSQL -c "CREATE INDEX ON sqm.binding_current (tac);"

t "ANALYZE" $PSQL -c "ANALYZE sqm.binding_current; ANALYZE sqm.tac;"

echo "================= SIZE ================="
$PSQL -c "
SELECT relname, pg_size_pretty(pg_total_relation_size(c.oid)) AS total,
       pg_size_pretty(pg_relation_size(c.oid)) AS heap,
       pg_size_pretty(pg_indexes_size(c.oid)) AS idx,
       to_char(n_live_tup,'999,999,999') AS rows
FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
LEFT JOIN pg_stat_user_tables s ON s.relid=c.oid
WHERE n.nspname='sqm' AND c.relkind='r' ORDER BY pg_total_relation_size(c.oid) DESC;"

echo "================= QUERIES (3 runs each) ================="
run3() { local label="$1"; local sql="$2"
  for i in 1 2 3; do
    local s=$(date +%s%N)
    $PSQL -c "$sql" >/tmp/q.txt 2>/tmp/qe.txt; local rc=$?
    local e=$(date +%s%N)
    printf '%-26s run%d %9.3fs rc=%s\n' "$label" "$i" "$(echo "scale=3; ($e-$s)/1000000000"|bc)" "$rc"
    [ $rc -ne 0 ] && head -3 /tmp/qe.txt && break
  done
  echo "   -> $(sed -n '3,5p' /tmp/q.txt | tr '\n' '|')"
}

run3 "Q1 top manufacturers" "
SELECT t.manufacturer, count(*) AS n FROM sqm.binding_current b
JOIN sqm.tac t ON t.tac = b.tac
GROUP BY t.manufacturer ORDER BY n DESC LIMIT 20;"

run3 "Q2 churn distribution" "
SELECT least(d,10) AS di, count(*) AS n FROM
 (SELECT msisdn, count(DISTINCT imei) AS d FROM sqm.binding_current GROUP BY msisdn) x
GROUP BY 1 ORDER BY 1;"

run3 "Q3 point lookup" "
SELECT b.msisdn, b.imsi, b.imei, t.manufacturer, t.\"marketingName\"
FROM sqm.binding_current b LEFT JOIN sqm.tac t ON t.tac=b.tac
WHERE b.msisdn = 9140257910;"

run3 "Q4 drill-down" "
SELECT t.\"deviceType\", count(*) AS n FROM sqm.binding_current b
JOIN sqm.tac t ON t.tac=b.tac
WHERE t.manufacturer='Samsung Korea' GROUP BY 1 ORDER BY n DESC;"

echo "================= Q5 DAILY MERGE ================="
t "COPY delta day (7.1M)" $PSQL -c "
\\copy sqm.stage_delta FROM '/data/sqm/2026-05-16/dump-07.260516092203/01.csv' WITH (FORMAT csv, HEADER true)"

t "merge adds (upsert)" $PSQL -c "
INSERT INTO sqm.binding_current (msisdn, imsi, imei, active)
SELECT msisdn::bigint, imsi::bigint, imei, 1 FROM sqm.stage_delta WHERE label='add'
ON CONFLICT (msisdn, imsi, imei) DO UPDATE SET active = 1;"

t "merge removes (upsert)" $PSQL -c "
INSERT INTO sqm.binding_current (msisdn, imsi, imei, active)
SELECT msisdn::bigint, imsi::bigint, imei, 0 FROM sqm.stage_delta WHERE label='remove'
ON CONFLICT (msisdn, imsi, imei) DO UPDATE SET active = 0;"

$PSQL -c "SELECT active, count(*) FROM sqm.binding_current GROUP BY active ORDER BY active;"
echo "DONE"
