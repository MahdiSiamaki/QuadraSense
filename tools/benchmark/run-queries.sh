#!/bin/bash
# Runs the same four query shapes against ClickHouse and PostgreSQL and prints timings.
# Each query runs 3 times; report the median. Run 1 is cold, runs 2-3 are warm.

ch() { docker exec bench-ch clickhouse-client --max_execution_time 7200 --query "$1"; }
pg() { docker exec bench-pg psql -U postgres -d bench -tAc "$1"; }

timeit() {   # timeit <engine> <label> <sql>
  local eng="$1" label="$2" sql="$3"
  for i in 1 2 3; do
    local s=$(date +%s%N)
    if [ "$eng" = "ch" ]; then out=$(ch "$sql" 2>&1); else out=$(pg "$sql" 2>&1); fi
    local rc=$?
    local ms=$(( ($(date +%s%N) - s) / 1000000 ))
    printf '%-12s %-24s run%d %10s ms  rc=%s\n' "$eng" "$label" "$i" "$ms" "$rc"
    if [ $rc -ne 0 ]; then echo "   ERR: $(echo "$out" | head -2)"; break; fi
  done
  echo "   -> $(echo "$out" | head -2 | tr '\n' ' ')"
}

echo "############ Q1  dashboard aggregate: top manufacturers ############"
timeit ch "Q1 top manufacturers" "
SELECT t.manufacturer, count() AS n FROM sqm.binding_current AS b
INNER JOIN sqm.tac AS t ON t.tac = b.tac
WHERE b.tac != '' GROUP BY t.manufacturer ORDER BY n DESC LIMIT 20"
timeit pg "Q1 top manufacturers" "
SELECT t.manufacturer, count(*) AS n FROM sqm.stage_base b
JOIN sqm.tac t ON t.tac = substr(b.imei,1,8)
WHERE length(b.imei)=14 GROUP BY 1 ORDER BY n DESC LIMIT 20"

echo "############ Q4  drill-down: device types for one manufacturer ############"
timeit ch "Q4 drill-down" "
SELECT t.deviceType, count() AS n FROM sqm.binding_current AS b
INNER JOIN sqm.tac AS t ON t.tac = b.tac
WHERE t.manufacturer = 'Samsung Korea' GROUP BY t.deviceType ORDER BY n DESC"
timeit pg "Q4 drill-down" "
SELECT t.\"deviceType\", count(*) AS n FROM sqm.stage_base b
JOIN sqm.tac t ON t.tac = substr(b.imei,1,8)
WHERE length(b.imei)=14 AND t.manufacturer='Samsung Korea' GROUP BY 1 ORDER BY n DESC"

echo "############ Q2  high-cardinality churn aggregate ############"
timeit ch "Q2 churn distribution" "
SELECT least(d,10) AS di, count() AS n FROM
 (SELECT msisdn, uniqExact(imei) AS d FROM sqm.binding_current GROUP BY msisdn)
GROUP BY di ORDER BY di"
timeit pg "Q2 churn distribution" "
SELECT least(d,10) AS di, count(*) AS n FROM
 (SELECT msisdn, count(DISTINCT imei) AS d FROM sqm.stage_base GROUP BY msisdn) x
GROUP BY 1 ORDER BY 1"

echo "############ Q3  point lookup (no PG index yet - see notes) ############"
timeit ch "Q3 point lookup" "
SELECT b.msisdn, b.imsi, b.imei, t.manufacturer FROM sqm.binding_current AS b
LEFT JOIN sqm.tac AS t ON t.tac = b.tac WHERE b.msisdn = 9140257910"

echo "############ SIZES ############"
ch "SELECT table, formatReadableSize(sum(bytes_on_disk)), sum(rows) FROM system.parts WHERE database='sqm' AND active GROUP BY table"
pg "SELECT relname||' '||pg_size_pretty(pg_total_relation_size(c.oid)) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='sqm' AND c.relkind='r' ORDER BY pg_total_relation_size(c.oid) DESC"
echo "ALLDONE"
