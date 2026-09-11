con.execute("SET memory_limit='12GB'")
q("RECONSTRUCTED ACTIVE STATE (sample x64 extrapolation)", """
WITH last_ev AS (SELECT msisdn,imsi,imei, arg_max(label, day_seq) last_label FROM ev GROUP BY 1,2,3),
 all_tr AS (
   SELECT msisdn,imsi,imei FROM bs
   UNION
   SELECT msisdn,imsi,imei FROM last_ev),
 st AS (SELECT a.msisdn,a.imsi,a.imei,
    CASE WHEN l.last_label IS NULL THEN (b.msisdn IS NOT NULL)
         ELSE l.last_label='add' END AS active
   FROM all_tr a LEFT JOIN last_ev l USING (msisdn,imsi,imei) LEFT JOIN bs b USING (msisdn,imsi,imei))
SELECT count(*) FILTER (WHERE active) active_bindings_sample,
       count(*) FILTER (WHERE active)*64 AS est_active_bindings_full,
       count(DISTINCT msisdn) FILTER (WHERE active)*64 AS est_active_msisdn_full,
       count(*) all_triples_sample, count(*)*64 est_all_triples_full
FROM st""")
q("ACTIVE bindings per msisdn (final state, sample)", """
WITH last_ev AS (SELECT msisdn,imsi,imei, arg_max(label, day_seq) last_label FROM ev GROUP BY 1,2,3),
 all_tr AS (SELECT msisdn,imsi,imei FROM bs UNION SELECT msisdn,imsi,imei FROM last_ev),
 st AS (SELECT a.msisdn,
    CASE WHEN l.last_label IS NULL THEN (b.msisdn IS NOT NULL) ELSE l.last_label='add' END AS active
   FROM all_tr a LEFT JOIN last_ev l USING (msisdn,imsi,imei) LEFT JOIN bs b USING (msisdn,imsi,imei)),
 c AS (SELECT msisdn, count(*) FILTER (WHERE active) n FROM st GROUP BY 1)
SELECT LEAST(n,6) active_per_msisdn, count(*) n_msisdn,
   round(100.0*count(*)/sum(count(*)) OVER (),2) pct FROM c GROUP BY 1 ORDER BY 1""")
q("Events per day: min/avg/max across the 82 files", """
SELECT min(n) min_rows, round(avg(n)) avg_rows, max(n) max_rows, count(*) n_files, sum(n) total
FROM (SELECT fname, batch, count(*) n FROM delta GROUP BY 1,2)""")
