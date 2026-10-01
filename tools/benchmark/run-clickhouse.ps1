# ClickHouse benchmark — mirrors run-postgres.ps1 step for step.
# Source files are mounted under user_files/ because ClickHouse's file() function is
# restricted to that directory by default.

$ErrorActionPreference = "Continue"

docker rm -f bench-ch 2>$null | Out-Null
docker run -d --name bench-ch `
  --memory=4g --cpus=6 `
  -p 18123:8123 -p 19000:9000 `
  -e CLICKHOUSE_SKIP_USER_SETUP=1 `
  -v "E:\job\QuadraSense\SQM:/var/lib/clickhouse/user_files/sqm:ro" `
  -v "E:\job\QuadraSense\TAC:/var/lib/clickhouse/user_files/tac:ro" `
  -v "E:\job\QuadraSense\_sqm_discovery\chdata:/var/lib/clickhouse" `
  clickhouse/clickhouse-server:25.8 | Out-Null

Write-Host "waiting for clickhouse..."
for ($i = 0; $i -lt 60; $i++) {
    $ok = docker exec bench-ch clickhouse-client --query "SELECT 1" 2>$null
    if ($ok -eq "1") { Write-Host "clickhouse ready"; break }
    Start-Sleep -Seconds 2
}

function Step($label, $sql) {
    $t = Get-Date
    $out = docker exec bench-ch clickhouse-client --receive_timeout 7200 --max_execution_time 7200 --query $sql 2>&1
    $sec = ((Get-Date) - $t).TotalSeconds
    "{0,-38} {1,9:N1}s" -f $label, $sec
    if ($LASTEXITCODE -ne 0) { "   ERROR: " + ($out | Select-Object -First 3) }
}

function Query3($label, $sql) {
    for ($i = 1; $i -le 3; $i++) {
        $t = Get-Date
        $out = docker exec bench-ch clickhouse-client --max_execution_time 7200 --query $sql 2>&1
        $sec = ((Get-Date) - $t).TotalSeconds
        "{0,-26} run{1}  {2,8:N3}s" -f $label, $i, $sec
        if ($LASTEXITCODE -ne 0) { "   ERROR: " + ($out | Select-Object -First 3); break }
    }
    "   -> " + (($out | Select-Object -First 2) -join " | ")
}

"===================== SCHEMA ====================="
Step "create database + tables" @"
CREATE DATABASE IF NOT EXISTS sqm;
"@

docker exec bench-ch clickhouse-client --query "DROP TABLE IF EXISTS sqm.binding_current"
docker exec bench-ch clickhouse-client --query "DROP TABLE IF EXISTS sqm.tac"

docker exec bench-ch clickhouse-client --query @"
CREATE TABLE sqm.tac (
  tac String, manufacturer String, modelName String, marketingName String, brandName String,
  allocationDate String, lastUpdatedDate String, organisationId String, deviceType String,
  bluetooth String, nfc String, wlan String, authIMS String, unauthIMS String, imsNoUICC String,
  removableUICC String, removableEUICC String, nonRemUICC String, nonRemEUICC String,
  netSpecificId String, ntnConnectivity String, simSlot String, imeiQuantity String,
  operatingSystem String, oem String, bandDetails String
) ENGINE = MergeTree ORDER BY tac
"@

docker exec bench-ch clickhouse-client --query @"
CREATE TABLE sqm.binding_current (
  msisdn UInt64, imsi UInt64, imei String,
  tac String MATERIALIZED if(length(imei) = 14, substring(imei, 1, 8), ''),
  active UInt8 DEFAULT 1
) ENGINE = ReplacingMergeTree ORDER BY (msisdn, imsi, imei)
"@

"===================== L1 LOAD ====================="
Step "load TAC (270k)" @"
INSERT INTO sqm.tac SELECT * FROM file('tac/DeviceDatabase_TAC1Sep2026.csv', CSVWithNames,
 'tac String, manufacturer String, modelName String, marketingName String, brandName String,
  allocationDate String, lastUpdatedDate String, organisationId String, deviceType String,
  bluetooth String, nfc String, wlan String, authIMS String, unauthIMS String, imsNoUICC String,
  removableUICC String, removableEUICC String, nonRemUICC String, nonRemEUICC String,
  netSpecificId String, ntnConnectivity String, simSlot String, imeiQuantity String,
  operatingSystem String, oem String, bandDetails String')
"@

Step "load base 125.9M (CSV -> ordered)" @"
INSERT INTO sqm.binding_current (msisdn, imsi, imei)
SELECT msisdn, imsi, imei
FROM file('sqm/1 Month/dump_subs_device_sim_info_20251227_20260125.csv', CSVWithNames,
 'imei String, imsi UInt64, msisdn UInt64')
SETTINGS max_insert_threads = 6, input_format_allow_errors_num = 1000
"@

Step "OPTIMIZE FINAL" "OPTIMIZE TABLE sqm.binding_current FINAL"

"===================== SIZE ====================="
docker exec bench-ch clickhouse-client --query @"
SELECT table, formatReadableSize(sum(bytes_on_disk)) AS on_disk,
       formatReadableQuantity(sum(rows)) AS rows_
FROM system.parts WHERE database='sqm' AND active GROUP BY table ORDER BY table
FORMAT PrettyCompact
"@

"===================== QUERIES ====================="
Query3 "Q1 top manufacturers" @"
SELECT t.manufacturer, count() AS n FROM sqm.binding_current AS b
INNER JOIN sqm.tac AS t ON t.tac = b.tac
WHERE b.tac != '' GROUP BY t.manufacturer ORDER BY n DESC LIMIT 20
"@

Query3 "Q2 churn distribution" @"
SELECT least(d,10) AS di, count() AS n FROM
 (SELECT msisdn, uniqExact(imei) AS d FROM sqm.binding_current GROUP BY msisdn)
GROUP BY di ORDER BY di
"@

Query3 "Q3 point lookup" @"
SELECT b.msisdn, b.imsi, b.imei, t.manufacturer, t.marketingName
FROM sqm.binding_current AS b LEFT JOIN sqm.tac AS t ON t.tac = b.tac
WHERE b.msisdn = 9140257910
"@

Query3 "Q4 drill-down" @"
SELECT t.deviceType, count() AS n FROM sqm.binding_current AS b
INNER JOIN sqm.tac AS t ON t.tac = b.tac
WHERE t.manufacturer = 'Samsung Korea' GROUP BY t.deviceType ORDER BY n DESC
"@

"===================== Q5 DAILY MERGE ====================="
docker exec bench-ch clickhouse-client --query "DROP TABLE IF EXISTS sqm.delta_day"
docker exec bench-ch clickhouse-client --query @"
CREATE TABLE sqm.delta_day (msisdn UInt64, imsi UInt64, imei String, label String)
ENGINE = MergeTree ORDER BY (msisdn, imsi, imei)
"@

Step "load 1 delta day (7.1M)" @"
INSERT INTO sqm.delta_day
SELECT msisdn, imsi, imei, label
FROM file('sqm/2026-05-16/dump-07.260516092203/01.csv', CSVWithNames,
 'msisdn UInt64, imsi UInt64, imei String, label String')
"@

Step "apply adds + removes" @"
INSERT INTO sqm.binding_current (msisdn, imsi, imei, active)
SELECT msisdn, imsi, imei, if(label='add', 1, 0) FROM sqm.delta_day
"@

Step "OPTIMIZE after merge" "OPTIMIZE TABLE sqm.binding_current FINAL"

docker exec bench-ch clickhouse-client --query @"
SELECT active, count() FROM sqm.binding_current GROUP BY active ORDER BY active FORMAT PrettyCompact
"@

"DONE"
