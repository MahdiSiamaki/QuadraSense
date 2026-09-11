q("BASE invalid IMEI patterns", """
SELECT CASE WHEN imei IS NULL THEN 'NULL'
            WHEN imei='000000' THEN 'literal 000000'
            WHEN imei SIMILAR TO '0+' THEN 'all zeros (other len)'
            WHEN length(imei)=14 THEN 'valid-len 14'
            ELSE 'other len '||length(imei) END AS cls,
       count(*) n, round(100.0*count(*)/sum(count(*)) OVER (),4) pct
FROM base GROUP BY 1 ORDER BY n DESC""")
q("BASE top short-imei values", """
SELECT imei, count(*) n FROM base WHERE length(imei)<>14 GROUP BY 1 ORDER BY n DESC LIMIT 15""")
q("BASE msisdn -> #rows distribution", """
WITH c AS (SELECT msisdn, count(*) n FROM base GROUP BY 1)
SELECT LEAST(n,10) AS rows_per_msisdn, count(*) n_msisdn,
       round(100.0*count(*)/sum(count(*)) OVER (),3) pct
FROM c GROUP BY 1 ORDER BY 1""")
q("BASE msisdn -> distinct imei / imsi", """
WITH c AS (SELECT msisdn, count(DISTINCT imei) di, count(DISTINCT imsi) ds FROM base GROUP BY 1)
SELECT LEAST(di,8) AS distinct_imei, count(*) n_msisdn FROM c GROUP BY 1 ORDER BY 1""")
q("BASE imsi -> distinct msisdn (is imsi<->msisdn 1:1?)", """
WITH c AS (SELECT imsi, count(DISTINCT msisdn) dm FROM base GROUP BY 1)
SELECT LEAST(dm,5) AS distinct_msisdn_per_imsi, count(*) n_imsi FROM c GROUP BY 1 ORDER BY 1""")
q("BASE imei -> distinct msisdn (shared devices)", """
WITH c AS (SELECT imei, count(DISTINCT msisdn) dm FROM base WHERE length(imei)=14 GROUP BY 1)
SELECT LEAST(dm,8) AS distinct_msisdn_per_imei, count(*) n_imei FROM c GROUP BY 1 ORDER BY 1""")
