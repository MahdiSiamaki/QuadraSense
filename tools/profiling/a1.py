q("DELTA per-batch", """SELECT batch, count(DISTINCT fname) n_files, count(*) n_rows,
   count(*) FILTER (WHERE label='add') adds, count(*) FILTER (WHERE label='remove') removes,
   count(*) FILTER (WHERE label NOT IN ('add','remove')) other
   FROM delta GROUP BY 1 ORDER BY 1""")
q("DELTA distinct labels", "SELECT label, count(*) n FROM delta GROUP BY 1 ORDER BY n DESC")
q("BASE identifier cardinality", """SELECT count(*) n_rows,
   count(DISTINCT msisdn) d_msisdn, count(DISTINCT imsi) d_imsi, count(DISTINCT imei) d_imei,
   count(DISTINCT (msisdn,imsi,imei)) d_triple FROM base""")
q("BASE exact duplicate rows", """SELECT count(*) - count(DISTINCT (msisdn,imsi,imei)) AS dup_rows FROM base""")
q("BASE field length dists", """
SELECT 'msisdn' f, length(msisdn) len, count(*) n FROM base GROUP BY 1,2
UNION ALL SELECT 'imsi', length(imsi), count(*) FROM base GROUP BY 1,2
UNION ALL SELECT 'imei', length(imei), count(*) FROM base GROUP BY 1,2
ORDER BY 1,2""")
