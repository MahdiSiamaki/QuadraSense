# Feed quality — each day's file, measured against the ordinary days

**Status:** Built 2026-09-30 (Phase 0 of the Explorer / Risk Signals work).
**Read after:** `09-import-platform.md`.

---

## Why it exists

Validation checks rows. It cannot see a file whose every row is well-formed and whose distribution is
wrong, and that is what the operator sent for weeks:

| From | What | Ordinary days | Worst days |
|---|---|---:|---:|
| 2026-07-27 | SIMs carrying more than one phone number on the same day | 500–900 SIMs | 383,000 SIMs (Sep 21) |
| 2026-08-25/26 | IMEIs cut to eight digits | ~3,500 rows | 793,475 rows (10% of Aug 25) |
| 2026-09-15 | IMEIs with the first digit dropped and a 0 added | ~7,000 rows (0.1%) | 3.16 million rows (25% of Sep 22) |

Together they inflated the dashboard's SIM changes about 15-fold and handset changes about 5-fold, and
they were found because a chart looked wrong — weeks after the first file. Every one of them was in the
operator's raw file; none was introduced by the pipeline.

**This is data quality, not risk.** It describes the feed. A SIM with two numbers on one day is a fact
about rows here, and whether it says anything about a subscriber is a different question, answered
elsewhere and never from these tables alone.

## The shifted-IMEI signature, and how it was established

`35004012123456` arrives as `50040121234560`: the leading digit gone, a `0` where the check digit would
be. That is the 15-digit signalling form of an IMEI (14 digits plus a spare `0`) with the **last** 14
digits kept instead of the first. Measured on 20 September, before anything was built on it:

- 99.5% of the unknown-TAC IMEIs end in `0` (2,943,035 of 2,958,257). Real IMEIs spread evenly over 0–9.
- One digit put back in front turns **99.6%** of them into a real GSMA TAC, and for 99.4% exactly one
  digit does. By chance about 2.7% would (270,166 TACs in 10⁸, ten tries).
- The raw file for 20 September contains 15,842 IMEIs beginning `5004012`; the file for 10 September
  contains none.

The test used everywhere is that one: 14 digits, TAC unknown to GSMA, ending in `0`, and made a GSMA TAC
by one leading digit. The ~7,000 rows a day that pass it before 15 September are the part of ordinary
unknown IMEIs that pass by chance, and they are part of the reference.

## What is measured

Once per day, when the day is imported (`FeedQualityMonitor`, after the day-level marts), into two
ClickHouse tables — `db/analytics/migrations/020_feed_quality.sql`:

- `dq_daily` — one row per day: rows, distinct SIMs, unknown-device rows (`000000`), malformed IMEIs,
  unknown-TAC IMEIs, shifted IMEIs, multi-number SIMs and their rows, and the GSMA version the TAC counts
  were judged against. Counts, not rates, so the judgement can change without recomputing anything.
- `dq_multi_number_sim_day` — the SIMs behind the multi-number count, per day, so a later rule can set
  those rows aside rather than learn only that "some" were affected. Shifted IMEIs need no such table;
  they are recognisable from the IMEI itself.

Cost, measured on the real days under the import's own limits (one thread, 1.2 GB, spilling at 300 MB):
6–12 seconds a day.

## How a day is judged

Against the **reference days** — a configured window, by default 2026-01-26 to 2026-07-26, the stretch
before the first defect was noticed. For each check the reference days' **median** rate is what an
ordinary day looks like, and a day is flagged when its rate is above `Multiplier` × that median (default
5) **and** above `MinimumRate` (default 0.01%), the floor that keeps a check whose ordinary rate is all
but zero from flagging a stray row. `FeedQualityRules` in `Sqm.Domain` is the whole rule.

The finding is a sentence with its evidence — the rate, the count, the reference and the margin — so a
reader can judge it for themselves. This one is 20 September, as the rule writes it:

> 24.60% of rows (2,940,845) carry an IMEI that looks shifted by one digit: first digit dropped, a 0
> added at the end. Ordinary days are around 0.089% (median of 172 reference days); this is 277 times
> that.

Configuration, section `FeedQuality`: `ReferenceFrom`, `ReferenceTo`, `Multiplier`, `MinimumRate`. When
corrected files replace the defective days, widening the window is all it takes.

### Why the median — calibrated against the real history before it shipped

The counts were computed for all 233 days (read-only, the same SQL the import runs) and the rule applied
to them. The first version compared against the reference window's **99th percentile**, and it missed
most of what it exists for: multi-number SIMs were not flagged from 27 July to 15 August, and the
eight-digit IMEIs of 25–26 August not at all. The reference window turned out to hold episodes of its
own, and a high percentile lands exactly on them:

| Check | Ordinary (median) | Inside the reference window |
|---|---:|---|
| Shifted IMEIs | 0.089% of rows | nothing unusual (max 0.124%) |
| Multi-number SIMs | 0.011% of SIMs | 0.2–0.39% on 25 days, 14 April – 7 May |
| Malformed IMEIs | 0.028% of rows | 4.6–4.8% on 2–3 May |
| Unknown device (`000000`) | 0.005% of rows | 10–13% until late February |

A minority of odd days cannot move a median. What the median rule flags over all 233 days:

| Check | ×3 | **×5 (default)** | ×10 |
|---|---|---|---|
| Shifted IMEIs | 15–26 Sep | **15–26 Sep** | 15–26 Sep |
| Multi-number SIMs | as ×5, plus 8 scattered days Feb–May | **26 Mar; 14 Apr – 7 May; 28 Jul – 26 Sep** | 15–24 Apr; 26 Apr – 5 May; 29 Jul – 26 Sep |
| Malformed IMEIs | as ×5, plus 8 ordinary-looking days in September | **2–3 May; 25–26 Aug** | 2–3 May; 25–26 Aug |
| Unknown device | 26 Jan – 26 Feb; 2 Jul – 6 Aug | **26 Jan – 25 Feb; 3–27 Jul; 29 Jul – 2 Aug** | 26 Jan – 25 Feb; 14–15 Jul; 19–20 Jul; 29 Jul – 2 Aug |

At ×5 every known defect is flagged: shifted IMEIs from their first day, 15 September; eight-digit IMEIs on both of their days; multi-number SIMs from 28 July (27 July, at 2.3 times ordinary, is the one day it does not reach). Nothing flags that looks ordinary. Three
episodes nobody had noticed are flagged too — multi-number SIMs in April–May, malformed IMEIs on 2–3 May,
and the `000000` code on up to 13% of rows until late February, falling to almost none after. They are
facts about the files; whether each was a defect is a question for the operator.
## Where it shows

- **On the import.** Each flagged check is a warning note on the job's timeline; an ordinary day gets one
  info note. Never a failure: the data is what the operator sent, and refusing it would leave a gap the
  dashboard counts as a missing day.
- **On the dashboard.** Flagged days are shaded on both daily charts, with the reason in the tooltip, for
  anyone who holds `import.view`.
- **`GET /api/v1/quality/days?from=&to=`** — every measured day with its counts and findings, and the
  reference it was judged against. `import.view`; counts only, nobody named.

## Operating it

```bash
# once, after migration 020: measure the days imported before it existed
dotnet run --project backend/src/Sqm.Ingestion -- --refresh-quality

# after activating a new GSMA version: the TAC counts were judged against the old one
dotnet run --project backend/src/Sqm.Ingestion -- --refresh-quality --force
```

If the worker runs before migration 020 is applied, each import says so on its timeline and carries on;
nothing fails for want of the check.

## Limits

- **A feed-level signal.** It says a day looks wrong, not which subscriber is affected — except for the
  multi-number SIMs, which are listed.
- **A SIM with two numbers is not always a defect.** Number changes and recycling produce 500–900 a day;
  that is what the reference is for.
- **Nothing is repaired.** 99.4% of the shifted IMEIs could be restored, but the restoration would be an
  inference written into the data, and the SIM-to-number defect cannot be repaired from this side at all.
  The fix is corrected files from the operator; when they arrive they replace the days in place.
