q("DUMP-07 (single day) internal consistency", """
WITH d AS (SELECT * FROM delta WHERE batch='dump-07.260516092203')
SELECT count(*) n_rows,
  count(DISTINCT (msisdn,imsi,imei,label)) d_quad,
  count(DISTINCT (msisdn,imsi,imei)) d_triple,
  count(DISTINCT msisdn) d_msisdn
FROM d""")
q("DUMP-07: triples having BOTH add and remove", """
WITH d AS (SELECT msisdn,imsi,imei,
   count(*) FILTER (WHERE label='add') a, count(*) FILTER (WHERE label='remove') r
   FROM delta WHERE batch='dump-07.260516092203' GROUP BY 1,2,3)
SELECT a AS n_add, r AS n_remove, count(*) n_triples FROM d GROUP BY 1,2 ORDER BY n_triples DESC LIMIT 12""")
q("DUMP-07: msisdn appearing with both add and remove (device swap)", """
WITH d AS (SELECT msisdn, count(*) FILTER (WHERE label='add') a, count(*) FILTER (WHERE label='remove') r
   FROM delta WHERE batch='dump-07.260516092203' GROUP BY 1)
SELECT CASE WHEN a>0 AND r>0 THEN 'both' WHEN a>0 THEN 'add only' ELSE 'remove only' END pattern,
   count(*) n_msisdn FROM d GROUP BY 1 ORDER BY n_msisdn DESC""")
q("FILES = DAYS or SHARDS? overlap of triples across files within dump-04", """
WITH d AS (SELECT msisdn,imsi,imei,label, count(DISTINCT fname) nf
  FROM delta WHERE batch='dump-04.260513040914' GROUP BY 1,2,3,4)
SELECT LEAST(nf,5) AS files_containing_same_quad, count(*) n FROM d GROUP BY 1 ORDER BY 1""")
q("Per-file row counts + add/remove ratio (dump-04)", """
SELECT fname, count(*) n, count(*) FILTER (WHERE label='add') adds,
  count(*) FILTER (WHERE label='remove') removes,
  count(DISTINCT msisdn) d_msisdn
FROM delta WHERE batch='dump-04.260513040914' GROUP BY 1 ORDER BY 1""")
