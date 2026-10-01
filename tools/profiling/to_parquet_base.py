import duckdb, time
con = duckdb.connect('E:/job/QuadraSense/_sqm_discovery/disc.duckdb')
con.execute("SET memory_limit='8GB'; SET threads=8; SET temp_directory='E:/job/QuadraSense/_sqm_discovery/tmp'; SET preserve_insertion_order=false;")
t=time.time()
con.execute(r"""
COPY (
  SELECT imei, imsi, msisdn
  FROM read_csv('E:/job/QuadraSense/SQM/1 Month/dump_subs_device_sim_info_20251227_20260125.csv',
     header=true, columns={'imei':'VARCHAR','imsi':'VARCHAR','msisdn':'VARCHAR'},
     strict_mode=false, ignore_errors=true, parallel=true)
) TO 'E:/job/QuadraSense/_sqm_discovery/pq/base.parquet'
  (FORMAT parquet, COMPRESSION zstd, ROW_GROUP_SIZE 2000000);
""")
print("base parquet in %.1fs" % (time.time()-t))
print(con.execute("SELECT count(*) FROM 'E:/job/QuadraSense/_sqm_discovery/pq/base.parquet'").fetchone())
