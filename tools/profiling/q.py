import duckdb, sys, time
con = duckdb.connect('D:/_sqm_discovery/disc.duckdb')
con.execute("SET memory_limit='10GB'; SET threads=8; SET temp_directory='D:/_sqm_discovery/tmp'; SET preserve_insertion_order=false;")
con.execute("CREATE OR REPLACE VIEW base AS SELECT * FROM 'D:/_sqm_discovery/pq/base.parquet'")
con.execute("CREATE OR REPLACE VIEW delta AS SELECT * FROM 'D:/_sqm_discovery/pq/delta.parquet'")
con.execute(r"""CREATE OR REPLACE VIEW tac AS SELECT * FROM read_csv('D:/TAC/DeviceDatabase_TAC1Sep2026.csv',
  header=true, all_varchar=true, sample_size=-1, strict_mode=false)""")
def q(label, sql):
    print("\n### " + label, flush=True); t=time.time()
    try:
        r = con.execute(sql).fetchall(); cols=[d[0] for d in con.description]
        print(" | ".join(cols))
        for row in r[:60]: print(" | ".join("" if v is None else str(v) for v in row))
        print("  [%.1fs]" % (time.time()-t))
    except Exception as e: print("ERR", e)
import __main__
if len(sys.argv)>1: exec(open(sys.argv[1]).read())
