q("TAC JOIN coverage on BASE (row-weighted)", """
WITH b AS (SELECT CASE WHEN length(imei)=14 THEN substr(imei,1,8) END AS tac8 FROM base)
SELECT count(*) total_rows,
  count(*) FILTER (WHERE tac8 IS NULL) no_valid_imei,
  count(*) FILTER (WHERE tac8 IS NOT NULL AND t.tac IS NOT NULL) tac_matched,
  count(*) FILTER (WHERE tac8 IS NOT NULL AND t.tac IS NULL) tac_unmatched,
  round(100.0*count(*) FILTER (WHERE tac8 IS NOT NULL AND t.tac IS NOT NULL)/count(*),3) pct_matched
FROM b LEFT JOIN tac t ON t.tac = b.tac8""")
q("TAC JOIN coverage on BASE (distinct TAC)", """
WITH b AS (SELECT DISTINCT substr(imei,1,8) AS tac8 FROM base WHERE length(imei)=14)
SELECT count(*) distinct_tac_in_data,
   count(*) FILTER (WHERE t.tac IS NOT NULL) matched,
   count(*) FILTER (WHERE t.tac IS NULL) unmatched
FROM b LEFT JOIN tac t ON t.tac=b.tac8""")
q("Top unmatched TACs by row volume", """
WITH b AS (SELECT substr(imei,1,8) AS tac8 FROM base WHERE length(imei)=14)
SELECT b.tac8, count(*) n FROM b LEFT JOIN tac t ON t.tac=b.tac8
WHERE t.tac IS NULL GROUP BY 1 ORDER BY n DESC LIMIT 15""")
q("Top TACs overall (skew)", """
SELECT substr(imei,1,8) tac8, count(*) n, round(100.0*count(*)/125939523,4) pct
FROM base WHERE length(imei)=14 GROUP BY 1 ORDER BY n DESC LIMIT 15""")
q("TAC skew: how concentrated?", """
WITH c AS (SELECT substr(imei,1,8) tac8, count(*) n FROM base WHERE length(imei)=14 GROUP BY 1),
 r AS (SELECT n, row_number() OVER (ORDER BY n DESC) rn, sum(n) OVER () tot FROM c)
SELECT
 max(CASE WHEN rn=10 THEN 1 END) is_10,
 round(100.0*sum(n) FILTER (WHERE rn<=10)/max(tot),2) top10_pct,
 round(100.0*sum(n) FILTER (WHERE rn<=100)/max(tot),2) top100_pct,
 round(100.0*sum(n) FILTER (WHERE rn<=1000)/max(tot),2) top1000_pct,
 count(*) total_tacs FROM r""")
