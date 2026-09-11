con.execute("SET memory_limit='12GB'")
# Deterministic ~1.5% sample of msisdn; full event history replay in file order
con.execute("""
CREATE OR REPLACE TABLE seq AS
SELECT batch, fname, dense_rank() OVER (ORDER BY batch, fname) AS day_seq
FROM (SELECT DISTINCT batch, fname FROM delta)""")
q("ordered file sequence (first 8 / last 6)", """
SELECT day_seq, batch, fname FROM seq WHERE day_seq<=8 OR day_seq>=77 ORDER BY day_seq""")
con.execute("""
CREATE OR REPLACE TABLE ev AS
SELECT d.msisdn, d.imsi, d.imei, d.label, s.day_seq
FROM delta d JOIN seq s USING (batch, fname)
WHERE hash(d.msisdn) % 64 = 0""")
con.execute("""
CREATE OR REPLACE TABLE bs AS
SELECT msisdn,imsi,imei FROM base WHERE hash(msisdn) % 64 = 0""")
q("sample sizes", "SELECT (SELECT count(*) FROM ev) sample_events, (SELECT count(*) FROM bs) sample_base_rows")
q("REPLAY: anomalies when starting from BASE as active-set", """
WITH e AS (
  SELECT msisdn,imsi,imei,label,day_seq,
    row_number() OVER (PARTITION BY msisdn,imsi,imei ORDER BY day_seq) rn
  FROM ev),
st AS (
  SELECT e.*, (b.msisdn IS NOT NULL) AS in_base,
    sum(CASE WHEN label='add' THEN 1 ELSE -1 END) OVER (PARTITION BY e.msisdn,e.imsi,e.imei ORDER BY day_seq ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS net_before
  FROM e LEFT JOIN bs b USING (msisdn,imsi,imei))
SELECT label,
  count(*) n,
  count(*) FILTER (WHERE coalesce(net_before,0) + CASE WHEN in_base THEN 1 ELSE 0 END = 0 AND label='remove') orphan_remove,
  count(*) FILTER (WHERE coalesce(net_before,0) + CASE WHEN in_base THEN 1 ELSE 0 END >= 1 AND label='add') dup_add
FROM st GROUP BY label""")
q("REPLAY: net balance per triple (adds - removes) over full history", """
WITH n AS (SELECT msisdn,imsi,imei,
   sum(CASE WHEN label='add' THEN 1 ELSE -1 END) net, count(*) ev_count
   FROM ev GROUP BY 1,2,3)
SELECT net, count(*) n_triples FROM n GROUP BY 1 ORDER BY net""")
