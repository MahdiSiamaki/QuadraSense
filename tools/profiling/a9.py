q("TAC: manufacturer normalization problem (Samsung/Motorola variants)", """
SELECT manufacturer, count(*) n FROM tac
WHERE lower(manufacturer) LIKE '%samsung%' OR lower(manufacturer) LIKE '%motorola%'
   OR lower(manufacturer) LIKE '%nokia%' GROUP BY 1 ORDER BY n DESC LIMIT 18""")
q("TAC: brandName vs manufacturer distinctness", """
SELECT count(DISTINCT manufacturer) mfr, count(DISTINCT brandName) brand,
  count(DISTINCT lower(trim(manufacturer))) mfr_normalized,
  count(DISTINCT lower(trim(brandName))) brand_normalized FROM tac""")
q("TAC: whitespace/case dirt in deviceType & os", """
SELECT 'deviceType' col, count(DISTINCT deviceType) raw, count(DISTINCT lower(trim(deviceType))) norm FROM tac
UNION ALL SELECT 'operatingSystem', count(DISTINCT operatingSystem), count(DISTINCT lower(trim(operatingSystem))) FROM tac
UNION ALL SELECT 'brandName', count(DISTINCT brandName), count(DISTINCT lower(trim(brandName))) FROM tac""")
q("TAC enrichment payoff: top manufacturers by ACTIVE BASE rows", """
SELECT t.manufacturer, t.deviceType, count(*) n,
  round(100.0*count(*)/125939523,3) pct
FROM base b JOIN tac t ON t.tac = substr(b.imei,1,8)
WHERE length(b.imei)=14 GROUP BY 1,2 ORDER BY n DESC LIMIT 15""")
