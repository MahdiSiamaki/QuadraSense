import duckdb, time
con = duckdb.connect('D:/_sqm_discovery/disc.duckdb')
con.execute("SET memory_limit='8GB'; SET threads=8; SET temp_directory='D:/_sqm_discovery/tmp'; SET preserve_insertion_order=false;")
t=time.time()
con.execute(r"""
COPY (
  SELECT msisdn, imsi, imei, label,
         regexp_extract(replace(filename,'\','/'), 'dump-[0-9]+\.[0-9]+', 0) AS batch,
         regexp_extract(replace(filename,'\','/'), '[^/]+$', 0) AS fname,
         replace(filename,'\','/') AS filename
  FROM read_csv(['D:/SQM/2026-05-13/*/*.csv','D:/SQM/2026-05-16/*/*.csv'],
     header=true, columns={'msisdn':'VARCHAR','imsi':'VARCHAR','imei':'VARCHAR','label':'VARCHAR'},
     filename=true, strict_mode=false, ignore_errors=true, parallel=true, union_by_name=true)
) TO 'D:/_sqm_discovery/pq/delta.parquet'
  (FORMAT parquet, COMPRESSION zstd, ROW_GROUP_SIZE 2000000);
""")
print("delta parquet in %.1fs" % (time.time()-t))
D="'D:/_sqm_discovery/pq/delta.parquet'"
print("rows:", con.execute(f"SELECT count(*) FROM {D}").fetchone()[0])
print("\nper-batch: batch | files | rows | adds | removes | other")
for r in con.execute(f"""SELECT batch, count(DISTINCT fname) files, count(*) rows,
   count(*) FILTER (WHERE label='add') adds, count(*) FILTER (WHERE label='remove') removes,
   count(*) FILTER (WHERE label NOT IN ('add','remove')) other
   FROM {D} GROUP BY 1 ORDER BY 1""").fetchall():
    print("  ", r)
print("\ndistinct labels:", con.execute(f"SELECT label, count(*) FROM {D} GROUP BY 1 ORDER BY 2 DESC").fetchall())
