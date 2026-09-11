-- Q1 dashboard aggregate: top 20 manufacturers by active binding count
SELECT t.manufacturer, count() AS n
FROM sqm.binding_current AS b
INNER JOIN sqm.tac AS t ON t.tac = b.tac
WHERE b.tac != ''
GROUP BY t.manufacturer
ORDER BY n DESC
LIMIT 20;

-- Q2 churn aggregate: distribution of distinct IMEIs per MSISDN
SELECT least(d, 10) AS distinct_imei, count() AS n_msisdn
FROM (SELECT msisdn, uniqExact(imei) AS d FROM sqm.binding_current GROUP BY msisdn)
GROUP BY distinct_imei
ORDER BY distinct_imei;

-- Q3 point lookup: every binding for one MSISDN
SELECT b.msisdn, b.imsi, b.imei, t.manufacturer, t.marketingName, t.deviceType
FROM sqm.binding_current AS b
LEFT JOIN sqm.tac AS t ON t.tac = b.tac
WHERE b.msisdn = {msisdn:UInt64};

-- Q4 filtered drill-down: device-type split for one manufacturer
SELECT t.deviceType, count() AS n
FROM sqm.binding_current AS b
INNER JOIN sqm.tac AS t ON t.tac = b.tac
WHERE t.manufacturer = 'Samsung Korea'
GROUP BY t.deviceType
ORDER BY n DESC;
