import duckdb, sys
con = duckdb.connect()
con.execute("SET memory_limit='6GB'; SET threads=6;")
con.execute(r"""
CREATE VIEW tac AS SELECT * FROM read_csv('D:/TAC/DeviceDatabase_TAC1Sep2026.csv',
  header=true, all_varchar=true, sample_size=-1, strict_mode=false, ignore_errors=false);
""")
def q(label, sql):
    print("\n### " + label)
    try:
        r = con.execute(sql).fetchall(); cols=[d[0] for d in con.description]
        print(" | ".join(cols))
        for row in r[:40]: print(" | ".join("" if v is None else str(v) for v in row))
    except Exception as e: print("ERR", e)

q("row count", "SELECT count(*) AS rows FROM tac")
q("columns", "SELECT column_name, data_type FROM (DESCRIBE SELECT * FROM tac)")
q("tac key quality", """
SELECT count(*) rows, count(DISTINCT tac) distinct_tac,
       count(*)-count(DISTINCT tac) dup_tac_rows,
       sum(CASE WHEN tac IS NULL THEN 1 ELSE 0 END) null_tac,
       sum(CASE WHEN length(tac)<>8 THEN 1 ELSE 0 END) tac_len_not_8,
       sum(CASE WHEN tac NOT SIMILAR TO '[0-9]+' THEN 1 ELSE 0 END) tac_non_numeric
FROM tac""")
q("tac length dist", "SELECT length(tac) len, count(*) n FROM tac GROUP BY 1 ORDER BY 1")
q("duplicate tac examples", """
SELECT tac, count(*) n, string_agg(DISTINCT manufacturer, ' / ') mfrs, string_agg(DISTINCT modelName,' / ') models
FROM tac GROUP BY tac HAVING count(*)>1 ORDER BY n DESC LIMIT 10""")
q("cardinality of key dims", """
SELECT count(DISTINCT manufacturer) manufacturers, count(DISTINCT brandName) brands,
       count(DISTINCT modelName) models, count(DISTINCT marketingName) marketing_names,
       count(DISTINCT deviceType) device_types, count(DISTINCT operatingSystem) os,
       count(DISTINCT organisationId) orgs, count(DISTINCT oem) oems
FROM tac""")
q("deviceType dist", "SELECT deviceType, count(*) n FROM tac GROUP BY 1 ORDER BY n DESC LIMIT 25")
q("top manufacturers", "SELECT manufacturer, count(*) n FROM tac GROUP BY 1 ORDER BY n DESC LIMIT 20")
q("brandName 'Not Known' rate", """
SELECT sum(CASE WHEN brandName='Not Known' THEN 1 ELSE 0 END) brand_notknown,
       sum(CASE WHEN manufacturer='Not Known' THEN 1 ELSE 0 END) mfr_notknown,
       sum(CASE WHEN operatingSystem='Not Known' THEN 1 ELSE 0 END) os_notknown,
       sum(CASE WHEN deviceType='Not Known' THEN 1 ELSE 0 END) dtype_notknown,
       count(*) total FROM tac""")
q("null rate per column", """
SELECT 'nulls' AS k, * FROM (
 SELECT count(*) FILTER (WHERE tac IS NULL) tac, count(*) FILTER (WHERE manufacturer IS NULL) manufacturer,
        count(*) FILTER (WHERE modelName IS NULL) modelName, count(*) FILTER (WHERE brandName IS NULL) brandName,
        count(*) FILTER (WHERE deviceType IS NULL) deviceType, count(*) FILTER (WHERE allocationDate IS NULL) allocationDate,
        count(*) FILTER (WHERE operatingSystem IS NULL) operatingSystem, count(*) FILTER (WHERE bandDetails IS NULL) bandDetails
 FROM tac)""")
q("allocationDate range", """
SELECT min(allocationDate) min_raw, max(allocationDate) max_raw,
       min(try_strptime(allocationDate,'%d-%b-%Y')) min_parsed,
       max(try_strptime(allocationDate,'%d-%b-%Y')) max_parsed,
       count(*) FILTER (WHERE try_strptime(allocationDate,'%d-%b-%Y') IS NULL) unparsable
FROM tac""")
q("os dist", "SELECT operatingSystem, count(*) n FROM tac GROUP BY 1 ORDER BY n DESC LIMIT 15")
