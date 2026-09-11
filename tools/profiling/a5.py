q("Do REMOVEs in first delta file reference triples present in BASE?", """
WITH f AS (SELECT msisdn,imsi,imei FROM delta WHERE fname='dayli_01.csv' AND label='remove')
SELECT count(*) removes, count(*) FILTER (WHERE b.msisdn IS NOT NULL) found_in_base,
  round(100.0*count(*) FILTER (WHERE b.msisdn IS NOT NULL)/count(*),2) pct_found
FROM f LEFT JOIN base b USING (msisdn,imsi,imei)""")
q("Do ADDs in first delta file ALREADY exist in BASE? (would mean re-add)", """
WITH f AS (SELECT msisdn,imsi,imei FROM delta WHERE fname='dayli_01.csv' AND label='add')
SELECT count(*) adds, count(*) FILTER (WHERE b.msisdn IS NOT NULL) already_in_base,
  round(100.0*count(*) FILTER (WHERE b.msisdn IS NOT NULL)/count(*),2) pct
FROM f LEFT JOIN base b USING (msisdn,imsi,imei)""")
q("Same test for LAST day (dump-07) vs BASE", """
WITH f AS (SELECT msisdn,imsi,imei,label FROM delta WHERE batch='dump-07.260516092203')
SELECT label, count(*) n, count(*) FILTER (WHERE b.msisdn IS NOT NULL) in_base,
  round(100.0*count(*) FILTER (WHERE b.msisdn IS NOT NULL)/count(*),2) pct
FROM f LEFT JOIN base b USING (msisdn,imsi,imei) GROUP BY label""")
q("DELTA global: distinct triples and event counts", """
SELECT count(*) n_rows, count(DISTINCT (msisdn,imsi,imei)) d_triple,
  count(DISTINCT msisdn) d_msisdn, count(DISTINCT imei) d_imei, count(DISTINCT imsi) d_imsi
FROM delta""")
q("DELTA IMEI quality", """
SELECT CASE WHEN imei='000000' THEN 'literal 000000' WHEN length(imei)=14 THEN 'valid-len 14'
   ELSE 'other len '||length(imei) END cls, count(*) n,
   round(100.0*count(*)/677580701,4) pct
FROM delta GROUP BY 1 ORDER BY n DESC LIMIT 12""")
