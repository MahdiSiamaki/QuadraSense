# Discovery addendum — shifted IMEIs: the rule, what is proven, and one example traced end to end

**Date:** 2026-10-19, on the data through 2026-10-10 (delivery 246).
**Status:** Documentation. Read-only queries; nothing in the data was changed or repaired, by the
product owner's rule. Corrects two statements in `docs/architecture/15-feed-quality.md`, marked
there.
**Read with:** `docs/architecture/15-feed-quality.md` (the monitor), `04-handset-changes-from-2026-09-15.md`
(what the shifted rows did to the change charts).

---

## The answer

| | Status | Evidence |
|---|---|---|
| From 2026-09-15 a large share of rows carry IMEIs of the shape *first digit dropped, `0` appended* | **Proven** | 3.4% of rows on 15 Sep, 19–25% from the 17th, **19.8% on 10 Oct** - still present |
| The digits restore to one handset: one leading digit makes a GSMA TAC | **Proven, for the shape** | 99.83% of 10 Oct's passes are restored by exactly one digit, 0.17% by two, none by more |
| The restored handset is, in most cases, **another subscriber's** | **Proven** | 87.9% of 10 Oct's restored `35…` IMEIs are held only by other numbers (1% sample); 87.5% on 24 Sep (`04-…`) |
| Before 15 Sep the same shape existed at ~5,000 rows a day | **Proven - and it was not chance** | restored by a leading `0` (`01…` TACs), and **91.5%** of them were the number's *own* handset |
| A restored TAC being valid shows the IMEI is shifted | **Not on its own** | 26–45% of unknown IMEIs that do *not* end in `0` are also made a GSMA TAC by one leading digit |
| The mechanism is the 15-digit signalling form of an IMEI with the last 14 digits kept | **Hypothesis** | it explains the shape exactly; nothing in the feed shows it |
| Why another subscriber's handset is attached to a number | **Unknown** | an operator-side question; nothing here can answer it |

Restoring the IMEIs remains ruled out (`15-…`, `04-…`): the digits restore, the binding does not.

## The rule, exactly

**Row level** - the feed-quality monitor, `ClickHouseIngestionStore.cs` (`dq_daily`), per day:

```sql
length(imei) = 14
AND endsWith(imei, '0')
AND imei != '00000000000000'
AND tac NOT IN (SELECT tac FROM sqm.tac)               -- tac = substring(imei, 1, 8), MATERIALIZED
AND <for some d in 0..9> concat(toString(d), substring(imei, 1, 7)) IN (SELECT tac FROM sqm.tac)
```

counted as rows (`shifted_imei_rows`). The risk measures use the same test on current state
(`ClickHouseIngestionStore.RiskSnapshot.cs`, `Shifted` / `Countable`), with one difference:
`match(imei, '^[0-9]{14}$')` instead of `length(imei) = 14`, so a 14-character value with a
non-digit is never "shifted" there. No such value passes the monitor's test either - a non-digit
IMEI cannot have a GSMA TAC after a digit is prefixed - so the two agree in practice.

The **restored IMEI** is `d` followed by the first 13 received digits: `d || substring(imei, 1, 13)`.

`sqm.tac` is the device catalogue of the **active GSMA version** at the time of the query. The
counts therefore depend on the version: `dq_daily.tac_version_id` records it (version 2,
v2026.09.16, until 1 Oct; 1595 from 2 Oct). v2026.10.04 adds 945 TACs to v2026.09.16's 270,885; a
TAC added later can move a row from "unknown" to "known", or make a restoration possible. After a
version change the days are re-measured with `--refresh-quality --force`.

**Day level** - `FeedQualityRules` (`Sqm.Domain`): rate = shifted rows / rows. A day is flagged when

```
rate > max(Multiplier × median(reference days), MinimumRate)
     = max(5 × 0.089%, 0.01%) = 0.445%
```

with the reference window 2026-01-26 – 2026-07-26 (172 days). Every day from 15 September is above
it; no day before is.

## The measurements

### 1. The control: a valid restored TAC is weak evidence on its own

Unknown-TAC 14-digit IMEIs of a day, split by their last digit, and how many one leading digit turns
into a GSMA TAC:

| Day | Not ending in 0 | of which restored | Ending in 0 | of which restored |
|---|---:|---:|---:|---:|
| 2026-07-01 | 12,240 | 3,216 (26.3%) | 7,046 | 5,678 (80.6%) |
| 2026-09-10 | 14,767 | 6,669 (45.2%) | 6,559 | 5,155 (78.6%) |
| 2026-10-10 | 14,996 | 6,580 (43.9%) | 2,083,981 | 2,082,072 (99.9%) |

Unknown IMEIs are not random digits - many are real handsets in TACs missing from the catalogue,
whose first seven digits sit in densely allocated ranges - so a prefixed digit finds a TAC for a
quarter to a half of them **whatever their last digit**. `15-…` put the chance rate at "about 2.7%"
(270,166 TACs in 10⁸, ten tries); the control shows that figure is far too low for real unknown
IMEIs. What separates the shifted rows is the combination with the appended `0` and, above all, the
restored IMEI being a handset the feed already knows (section 3).

### 2. Two shapes, not one

The passes of a day by their first two digits and the digit that restores them:

| Day | First digits | Restored by | Rows | IMEIs |
|---|---|---|---:|---:|
| 2026-09-10 | 13 / 12 / 14 / 15 | `0` | 2,547 / 885 / 774 / 680 | 2,446 / 852 / 750 / 649 |
| 2026-09-10 | 10 | `0` | 150 | **1** - one IMEI repeated |
| 2026-09-10 | 50–59, 69 | `3`, `8` | 8–14 each | |
| 2026-10-10 | 50 … 59 | `3` | 133,407 – 250,740 each | 81,177 – 151,257 each |
| 2026-10-10 | 15 / 14 | `0` | 5,871 / 5,398 | 3,979 / 3,464 |

Before 15 September the shape is almost only `01…` handsets (restored by `0`; TACs beginning `01`
are allocated, among others, to Apple). From 15 September `35…` handsets (restored by `3`) dominate,
and the `01…` shape continues beside them at a higher level.

### 3. Whose handset the restored IMEI is

For each shifted **add**, the restored IMEI's bindings in current state (`binding_by_imei`, ended
bindings included):

| Day, restored by | Adds | This number's own | Only other numbers' | Never seen |
|---|---:|---:|---:|---:|
| 2026-09-10, `0` | 2,430 | **2,224 (91.5%)** | 164 (6.7%) | 42 (1.7%) |
| 2026-10-10, `0` | 8,779 | 3,364 (38.3%) | **5,340 (60.8%)** | 75 (0.9%) |
| 2026-10-10, `3` (numbers with `cityHash64(msisdn) % 100 = 0`) | 11,639 | 1,399 (12.0%) | **10,236 (87.9%)** | 4 (0.03%) |

So the ordinary-day passes were **a standing re-encoding of the subscriber's own `01…` handset**, not
chance and not foreign handsets. From 15 September the same shape carries mostly other subscribers'
handsets, in both ranges.

"Holder" here is any binding in current state, at any time - not, as in `04-…`, a holder before
15 September. A handset that changed hands legitimately also counts as "another number's".

## One example, traced end to end

Chosen without looking at the outcome: of all shifted adds on 2026-10-10 restored by `3`, the one
with the lowest `cityHash64(msisdn, imei)`.

**The row.** Delivery 246, 2026-10-10: number **9147777303**, SIM **432113913349473**, IMEI
**50411861702270**, `add`.

**Its TAC.** `50411861` is not in the GSMA catalogue. Exactly one leading digit makes the first seven
digits a TAC: `3` → **35041186**, HMD Global Oy TA-1564. The restored IMEI is **35041186170227**.

**Whose it is.** Before 2026-09-15 the restored IMEI had one holder: **9936642206**, SIM
432113980338505, from 2026-01-26 to 2026-09-02 - and it is still active there. 9147777303 has never
held it.

| Number | IMSI | Active | Last change |
|---|---|---:|---|
| 9936642206 | 432113980338505 | 1 | 2026-09-02 |
| 9936642206 | 432112946270114 | 0 | 2026-09-27 |
| 9936642206 | 432112979735634 | 0 | 2026-10-02 |
| 9936642206 | 432112979847543 | 0 | 2026-09-27 |

**Where the received IMEI went.** The shifted form `50411861702270` was given to six numbers in 18
days, each for a day or two - including its owner:

| Day | Number | Label |
|---|---|---|
| 2026-09-23 / 24 | 9143129054 | add / remove |
| 2026-09-26 / 27 | 9142626527 | add / remove |
| 2026-09-29 / 10-01 | 9147238538 | add / remove |
| 2026-09-29 – 10-04 | 9936642206 (the owner) | add, remove, add, add, remove, remove |
| 2026-10-01 / 02 | 9145974015 | add / remove |
| 2026-10-08 / 10 | 9142626527 | add / remove |
| 2026-10-10 | 9147777303 | add |

**The receiving number.** 9147777303's rows on the days around it: 2026-10-08 add `86854506011768`,
2026-10-09 remove it, 2026-10-10 add the shifted IMEI. Its active handsets of the ordinary shape are
`86032107934261` (listed by the initial dump, never confirmed by a daily file since) and
`86752204389054` (confirmed 2026-08-15). Its current state holds nine IMEIs of the shifted shape -
`51714110329930`, `52879323967410`, `52879326885740`, `56266116528760`, `56406549253610`,
`56938144933130`, `57273449593780`, `58084106963870` and this one - the eight earlier ones all ended,
with last changes between 2026-09-22 and 2026-10-07: the add-and-remove churn `04-…` describes.

What is **proven** for this row: the received IMEI has the shifted shape, restores uniquely to a GSMA
handset, and that handset belonged to another number before the defect began. What is **not**: why
the operator's file attached it to 9147777303, and whether 9147777303 ever used it - nothing in the
feed suggests it did.

### The SQL

All read-only, as the API's user (`docker exec sqm-clickhouse clickhouse-client --user sqm_app`):

```sql
-- the example: the lowest-hash shifted add of the day, restored by 3
SELECT seq, data_date, msisdn, imsi, imei, label
FROM sqm.binding_event
WHERE data_date = '2026-10-10' AND label = 'add'
  AND match(imei, '^[0-9]{14}$') AND endsWith(imei, '0')
  AND substring(imei, 1, 8) NOT IN (SELECT tac FROM sqm.tac)
  AND concat('3', substring(imei, 1, 7)) IN (SELECT tac FROM sqm.tac)
ORDER BY cityHash64(msisdn, imei)
LIMIT 1;

-- which leading digit restores it, and to what
SELECT d, concat(toString(d), substring('50411861702270', 1, 7)) AS tac, t.marketingName, t.manufacturer,
       concat(toString(d), substring('50411861702270', 1, 13)) AS restored_imei
FROM (SELECT arrayJoin(range(10)) AS d) AS l
INNER JOIN sqm.tac AS t ON t.tac = concat(toString(l.d), substring('50411861702270', 1, 7));

-- the restored IMEI's holders before the defect (served by the bloom index of migration 021)
SELECT min(data_date), max(data_date), msisdn, imsi
FROM sqm.binding_event
WHERE imei = '35041186170227' AND data_date < '2026-09-15'
GROUP BY msisdn, imsi;

-- every number the received IMEI was given to
SELECT data_date, msisdn, label FROM sqm.binding_event
WHERE imei = '50411861702270' ORDER BY data_date, label, msisdn;

-- whose handset the restored IMEI is, for a whole day (section 3); LEAD = 0 or 3
WITH passes AS (
    SELECT msisdn, concat('3', substring(imei, 1, 13)) AS restored
    FROM sqm.binding_event
    WHERE data_date = '2026-10-10' AND label = 'add'
      AND match(imei, '^[0-9]{14}$') AND endsWith(imei, '0')
      AND substring(imei, 1, 8) NOT IN (SELECT tac FROM sqm.tac)
      AND concat('3', substring(imei, 1, 7)) IN (SELECT tac FROM sqm.tac)
      AND cityHash64(msisdn) % 100 = 0)
SELECT count() AS adds,
       countIf(h.n = 0) AS never_seen,
       countIf(h.n > 0 AND has(h.holders, p.msisdn)) AS own,
       countIf(h.n > 0 AND NOT has(h.holders, p.msisdn)) AS only_others
FROM passes AS p
LEFT JOIN (SELECT imei, count() AS n, groupUniqArray(msisdn) AS holders
           FROM sqm.binding_by_imei FINAL
           WHERE imei IN (SELECT restored FROM passes) GROUP BY imei) AS h ON h.imei = p.restored;
```

The control (section 1) and the shape table (section 2) are the same `WHERE` without the restoring
condition, grouped by `endsWith(imei, '0')`, and by `substring(imei, 1, 2)` with the list of
restoring digits.

## False detections, missed ones, and how far to trust a figure

**A pass is a shape, not a verdict.** It says the IMEI looks shifted. Whether the handset is foreign
is a second measurement (section 3), and before 15 September the answer was "no" for 91.5%.

**Would pass and is not a shift.** Real IMEIs ending in `0` in a TAC missing from the catalogue whose
first seven digits, prefixed, happen to be a TAC. The control bounds how often a prefixed digit finds
a TAC (26–45% of such IMEIs); how many real handsets end in `0` in uncatalogued TACs is a few
thousand rows a day at most (all unknown-TAC IMEIs ending in `0` were 6,559–7,046 rows on the
ordinary days measured). On 2026-10-10 that is at most 0.3–0.4% of the 2.08 million rows flagged. A
pass whose restored IMEI the feed has never seen - 1.7%, 0.9% and 0.03% in the three groups of
section 3 - is the part that cannot be confirmed either way.

**Would be a shift and does not pass.**

- a shifted IMEI whose truncated first eight digits happen to be a real TAC: the test requires the
  received TAC to be unknown, so it never sees these;
- a restoration that needs a TAC the active catalogue does not hold yet;
- any other corruption: the rule detects this one shape and nothing else - the eight-digit IMEIs of
  25–26 August, for instance, are counted as malformed instead.

**Ambiguous restoration.** 0.17% of 10 October's passes restore by two digits; which handset they are
cannot be decided from the digits.

**Limits of the data.** One file a day, no time of day: an add and a remove on the same day cannot be
ordered. Ownership is inferred from bindings, which record what the operator sent, not who used a
handset; a phone that changed hands appears as "another number's".

**Version.** Every count depends on the GSMA version in `sqm.tac` at the time; the example and
sections 1–3 were measured against v2026.10.04, the active version on 2026-10-19.

## What it means

- **Nothing is repaired.** The fix is corrected files from the operator.
- **The risk measures exclude the shape** (`Countable`), both ranges, so neither the foreign `35…`
  handsets nor the re-encoded `01…` ones are counted as SIMs on an IMEI.
- **Lookups and the Explorer show the rows as sent**: 9147777303 shows its nine shifted IMEIs.
- **For the operator, one question more than `04-…` asks:** the `01…` re-encoding existed before
  15 September at ~5,000 rows a day, on the subscriber's own handset. Is it the same export step, and
  what changed on 15 September to make it carry other subscribers' handsets?
