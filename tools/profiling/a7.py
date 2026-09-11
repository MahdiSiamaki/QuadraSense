con.execute("SET memory_limit='12GB'")
q("ALTERNATION TEST: do add/remove strictly alternate per triple? (ignores base)", """
WITH e AS (SELECT msisdn,imsi,imei,label,day_seq,
    lag(label) OVER (PARTITION BY msisdn,imsi,imei ORDER BY day_seq) prev
   FROM ev)
SELECT count(*) total_events,
  count(*) FILTER (WHERE prev IS NULL) first_events,
  count(*) FILTER (WHERE prev IS NOT NULL AND prev=label) violations,
  round(100.0*count(*) FILTER (WHERE prev IS NOT NULL AND prev=label)
        / nullif(count(*) FILTER (WHERE prev IS NOT NULL),0),4) pct_violation
FROM e""")
q("violation breakdown by label", """
WITH e AS (SELECT msisdn,imsi,imei,label,day_seq,
    lag(label) OVER (PARTITION BY msisdn,imsi,imei ORDER BY day_seq) prev FROM ev)
SELECT label, count(*) n FROM e WHERE prev=label GROUP BY 1""")
q("FIRST EVENT per triple vs BASE membership  (tests: is BASE the active state?)", """
WITH f AS (SELECT msisdn,imsi,imei, arg_min(label, day_seq) first_label
   FROM ev GROUP BY 1,2,3)
SELECT first_label, (b.msisdn IS NOT NULL) AS present_in_base, count(*) n_triples,
   round(100.0*count(*) / sum(count(*)) OVER (PARTITION BY first_label),2) pct_within_label
FROM f LEFT JOIN bs b USING (msisdn,imsi,imei)
GROUP BY 1,2 ORDER BY 1,2""")
q("Multiple triples ACTIVE per msisdn at base? (base rows per sampled msisdn)", """
WITH c AS (SELECT msisdn, count(*) n FROM bs GROUP BY 1)
SELECT LEAST(n,6) rows_per_msisdn, count(*) n_msisdn FROM c GROUP BY 1 ORDER BY 1""")
