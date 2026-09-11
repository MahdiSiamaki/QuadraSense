#!/bin/bash
# ClickHouse benchmark runner. Executed inside the clickhouse-server container.
set -u
CH="clickhouse-client --receive_timeout 3600 --max_execution_time 3600"

t() { local label="$1"; shift; local s=$(date +%s.%N)
      "$@" > /tmp/out.txt 2>/tmp/err.txt
      local rc=$?; local e=$(date +%s.%N)
      printf '%-34s %8.2fs  rc=%s\n' "$label" "$(echo "$e - $s" | bc)" "$rc"
      if [ $rc -ne 0 ]; then head -3 /tmp/err.txt; fi }

echo "================= SCHEMA ================="
$CH --multiquery < /tmp/bench.sql && echo "schema ok"

echo "================= L1 LOAD ================="
t "load TAC (270k)" $CH --query "
INSERT INTO sqm.tac
SELECT * FROM file('/data/tac/DeviceDatabase_TAC1Sep2026.csv', CSVWithNames,
 'tac String, manufacturer String, modelName String, marketingName String, brandName String,
  allocationDate String, lastUpdatedDate String, organisationId String, deviceType String,
  bluetooth String, nfc String, wlan String, authIMS String, unauthIMS String, imsNoUICC String,
  removableUICC String, removableEUICC String, nonRemUICC String, nonRemEUICC String,
  netSpecificId String, ntnConnectivity String, simSlot String, imeiQuantity String,
  operatingSystem String, oem String, bandDetails String')"

t "load base (125.9M rows)" $CH --query "
INSERT INTO sqm.binding_current (msisdn, imsi, imei)
SELECT msisdn, imsi, imei
FROM file('/data/sqm/1 Month/dump_subs_device_sim_info_20251227_20260125.csv', CSVWithNames,
 'imei String, imsi UInt64, msisdn UInt64')"

t "OPTIMIZE FINAL" $CH --query "OPTIMIZE TABLE sqm.binding_current FINAL"

echo "================= SIZE ================="
$CH --query "
SELECT table, formatReadableSize(sum(bytes_on_disk)) AS on_disk,
       formatReadableQuantity(sum(rows)) AS rows
FROM system.parts WHERE database='sqm' AND active GROUP BY table ORDER BY table" --format PrettyCompact

echo "================= QUERIES (3 runs each) ================="
run3() { local label="$1"; local sql="$2"
  for i in 1 2 3; do
    local s=$(date +%s.%N)
    $CH --query "$sql" > /tmp/q.txt 2>/tmp/qe.txt
    local rc=$?; local e=$(date +%s.%N)
    printf '%-26s run%d %8.3fs rc=%s\n' "$label" "$i" "$(echo "$e - $s" | bc)" "$rc"
    [ $rc -ne 0 ] && head -3 /tmp/qe.txt && break
  done
  echo "   -> $(head -3 /tmp/q.txt | tr '\n' ' | ')"
}

run3 "Q1 top manufacturers" "
SELECT t.manufacturer, count() AS n FROM sqm.binding_current AS b
INNER JOIN sqm.tac AS t ON t.tac = b.tac
WHERE b.tac != '' GROUP BY t.manufacturer ORDER BY n DESC LIMIT 20"

run3 "Q2 churn distribution" "
SELECT least(d,10) AS di, count() AS n FROM
 (SELECT msisdn, uniqExact(imei) AS d FROM sqm.binding_current GROUP BY msisdn)
GROUP BY di ORDER BY di"

run3 "Q3 point lookup" "
SELECT b.msisdn, b.imsi, b.imei, t.manufacturer, t.marketingName
FROM sqm.binding_current AS b LEFT JOIN sqm.tac AS t ON t.tac = b.tac
WHERE b.msisdn = 9140257910"

run3 "Q4 drill-down" "
SELECT t.deviceType, count() AS n FROM sqm.binding_current AS b
INNER JOIN sqm.tac AS t ON t.tac = b.tac
WHERE t.manufacturer = 'Samsung Korea' GROUP BY t.deviceType ORDER BY n DESC"

echo "================= Q5 DAILY MERGE ================="
t "load 1 delta day (7.1M)" $CH --query "
CREATE TABLE sqm.delta_day (msisdn UInt64, imsi UInt64, imei String, label String)
ENGINE = MergeTree ORDER BY (msisdn, imsi, imei)"
t "  insert delta" $CH --query "
INSERT INTO sqm.delta_day
SELECT msisdn, imsi, imei, label FROM file('/data/sqm/2026-05-16/dump-07.260516092203/01.csv',
 CSVWithNames, 'msisdn UInt64, imsi UInt64, imei String, label String')"

t "apply adds (ReplacingMergeTree)" $CH --query "
INSERT INTO sqm.binding_current (msisdn, imsi, imei, active)
SELECT msisdn, imsi, imei, 1 FROM sqm.delta_day WHERE label='add'"
t "apply removes" $CH --query "
INSERT INTO sqm.binding_current (msisdn, imsi, imei, active)
SELECT msisdn, imsi, imei, 0 FROM sqm.delta_day WHERE label='remove'"
t "OPTIMIZE after merge" $CH --query "OPTIMIZE TABLE sqm.binding_current FINAL"

$CH --query "SELECT active, count() FROM sqm.binding_current GROUP BY active ORDER BY active" --format PrettyCompact
echo "DONE"
