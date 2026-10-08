# Discovery addendum — why handset changes rose from 15 September 2026

**Date:** 2026-10-08, on the data through 2026-10-06.
**Status:** Cause established and measured. The defect is in the operator's files and is **still
present on 6 October**; nothing in the pipeline introduced it and nothing here repairs it.
**Read with:** `docs/architecture/15-feed-quality.md`, which first described the shifted IMEIs on
data through 26 September. This report finds what they are, and corrects one conclusion there.

---

## The answer

From 15 September the operator's daily file attaches to a large number of subscribers **a handset
that belongs to somebody else**, written with its first digit dropped and a `0` added at the end
(`35xxxxxxxxxxxx` arrives as `5xxxxxxxxxxxx0`). Each such binding lives about one day: it is added,
removed the next day, and another foreign handset is added. Because the dashboard counts a handset
change whenever a number has one IMEI removed and a different one added on the same day, every day
of this churn is counted as subscribers changing phones.

Measured against the ordinary level (median of 7–14 September: **174,350 a day**; the median of
1 July – 14 September is 190,357), the 21 days from 16 September to 6 October carry **12.2 million
excess handset changes**:

| Cause | Share of the excess | What it is |
|---|---:|---|
| Foreign handsets in the shifted form | **85.2%** | changes in which a shifted IMEI is removed or added |
| SIMs carrying several numbers | 4.1% | the separate defect running since 27 July (`15-feed-quality.md`) |
| A SIM change on the same number | 0.9% | |
| Everything else | 9.7% | a one-day wave of foreign handsets *not* shifted on 16 September, then 40–75 thousand a day more of ordinary-looking changes (see below) |

The series peaked at 979,335 on 22 September (5.1 times the July–September median), and on
2–6 October still ran at 596–678 thousand a day (median 643,665, 3.4 times).

## What a handset change is, here

`agg_device_change_daily` (`db/analytics/jobs/refresh_change_marts.sql`, section 4): a number counts
on day D when, in that day's rows, it has at least one `remove` and at least one `add` whose IMEI is
not among the IMEIs removed that day. The rows are the operator's own (`msisdn, imsi, imei, label`);
the pipeline synthesises none. The daily totals in this report were recomputed from the rows by an
independent query and match the mart exactly on all 36 days (1 September – 6 October).

## The evidence

### 1. The shape: only `35…` handsets, always shifted the same way

On 24 September, **2,995,929 of the ~3.02 million shifted rows (99.2%)** begin with `5` and become a
GSMA TAC when a `3` is put back in front: handsets whose TAC begins `35`. Handsets whose TAC begins
`86` (most Chinese brands) appear shifted on 36 rows. The share of 14-digit IMEIs beginning `5` was
0.01–0.02% before, and 3.36% on 15 September, 11.42% on the 16th, 19.34% on the 17th, 21.6% on the
18th — while those beginning `3` fell from about 60% to 41%. The pattern is in the raw digits; no
GSMA lookup is needed to see it.

### 2. It is not the subscriber's own handset

For a 1% sample of the numbers given a shifted IMEI on 24 September (14,909 pairs), the IMEI with
its `3` put back was, before 15 September:

| | Pairs | Share |
|---|---:|---:|
| held **only by other numbers** | 13,045 | **87.5%** |
| this number's own handset | 1,728 | 11.6% |
| never seen | 136 | 0.9% |

An independent sample on 17 September gave 78% other numbers. The real owners keep their own
binding. In a 0.5% sample of the shifted IMEIs of 24 September (by IMEI), 7,883 of the 8,415 restored
handsets that had ever had a holder were active with one on 14 September; of those 13,165 holder
bindings, 12,814 (97.3%) were still active on 24 September, while the shifted copy sat on another
number.

### 3. Added alongside, gone the next day, replaced by another

- Of the numbers that received a shifted IMEI on 24 September (5% sample, 60,900), **77% removed no
  real IMEI that day**: the foreign handset is added next to the subscriber's own, not in its place.
  29% removed a shifted IMEI the same day, and 30% had received one the day before.
- Of the shifted bindings added 15–29 September (5% sample), **78% were removed the next day**
  (753,896 of 971,235); for real bindings added in the same days it is 54% (1,843,386 of 3,383,803).
- Across 15 September – 6 October one number collected many different foreign handsets: in the 5%
  sample, 120,871 numbers one, 45,905 two, 65,701 three to five, 32,703 six to ten and 14,610 more
  than ten (at most 325). On any single day a shifted IMEI is attached to one or two numbers (median
  1, 99th percentile 4, at most 143 on 24 September); over the three weeks it travels across about
  seven numbers on average.

This is why the inflation does not fade after the first days. A one-time re-encoding would have
produced one spike; a foreign handset that leaves after a day and is replaced by another produces a
handset change every day. On 24 September the 659,261 changes touching a shifted IMEI split into
356,263 shifted→shifted (yesterday's foreign handset out, a new one in), 156,800 real→shifted and
146,198 shifted→real.

### 4. The files doubled

Rows per day went from 6.1–6.4 million (7–14 September) to 10.5–13.0 million (16–28 September) and
9.7–10.4 million in October. Distinct numbers in a day's file went from about 5.5 million to about
9 million, and rows per number from 1.11 to about 1.34. On 24 September the file carried 1,510,486
shifted adds and 1,506,021 shifted removes; real adds were 4.29 million and real removes 5.00 million,
against about 3.0 and 3.1 million on 10 September.

### 5. The remaining tenth

Changes with no shifted IMEI, no multi-number SIM and no SIM change ran at 145–182 thousand a day
before 15 September, **334,229 on 16 September**, and 182–243 thousand afterwards. Classifying the
IMEIs newly added in them (5% sample):

| Day | New IMEIs | The number's own earlier handset | **Another number's handset** | Never seen | Removed within two days |
|---|---:|---:|---:|---:|---:|
| 10 Sep (ordinary) | 10,860 | 23% | 68% | 9% | 77% |
| 16 Sep | 34,577 | 8% | **88%** | 4% | **92%** |
| 24 Sep | 14,968 | 23% | 70% | 7% | 80% |
| 4 Oct | 14,017 | 27% | 65% | 8% | 79% |

So 16 September — the first full day — also carried a wave of foreign handsets that were *not*
shifted. After it, these changes have the make-up of an ordinary day, about 30–40% more of them,
which the larger files (more numbers per day) would produce; this report does not separate the two.

**An observation, not a finding:** even on an ordinary day, two thirds of the handsets newly added
in a handset change belong to another number and three quarters are gone within two days. Whether
that is how people really use phones, or a smaller standing form of the same behaviour in the feed,
cannot be decided from these files. It is a question for the operator.

## What was ruled out

- **The import.** Every day from 15 September has exactly one effective import, its rows equal to
  the file's, with no duplicate rows (no binding carries both an add and a remove in a day). 25
  September failed once and was imported again in full on 4–5 October; 7 and 22 September were
  imported after their neighbours. The change marts are built from each day's rows alone, so order
  does not matter.
- **The GSMA version.** The shift starts on 15 September, before v2026.09.16 was activated (16
  September 19:36 UTC), and it shows in the leading digits without any GSMA table. This report
  judges every day against that one version, so a later version cannot move a day between groups.
- **Dual-SIM handsets.** Changes between the two IMEIs of one handset (serial ±1 in the same TAC, or
  TAC +100 with the same serial) were 12,354 on 17 September against 10,181 on 10 September.
- **Day of the week.** Before and after alike, Friday and Saturday are lowest; the rise is on every
  weekday.

## What it means for the product

- **The dashboard's handset and SIM change charts are inflated** about 3.5–5 times from 16 September.
  The days are already shaded as feed-quality days, with the reason in the tooltip.
- **Risk measures** count only well-formed IMEIs and exclude the shifted shape
  (`ClickHouseIngestionStore.RiskSnapshot.cs`, `Countable`), so the foreign shifted handsets do not
  reach them. The unshifted foreign wave of 16 September does.
- **Timelines and lookups show these bindings as the operator sent them**: a number will show
  shifted IMEIs for a day at a time.
- **Restoring the shifted IMEIs would be wrong.** Putting the `3` back gives a real handset, but in
  87.5% of cases somebody else's: a repair would attach other subscribers' phones to these numbers.
  `15-feed-quality.md` said 99.4% "could be restored"; that is true of the digits and is now marked
  superseded there.

## Questions for the operator

1. From 15 September, rows carry IMEIs of handsets that belong to other subscribers, in a form with
   the first digit dropped and a `0` appended, for TACs beginning `35` only — about 1.0–1.6 million
   adds a day, removed the next day. What changed in the export on 15 September?
2. On 16 September the same happened with IMEIs in their normal form. Was that the same change?
3. Can corrected files be sent for 15 September onwards? They replace the days in place (`replace by
   date`), and every mart and chart follows.
4. On ordinary days, about two thirds of newly added handsets in a handset change belong to another
   number and are gone within two days. Is that expected?

## Method, and how to reproduce it

All queries were read-only, against the ClickHouse store (`docker exec sqm-clickhouse
clickhouse-client`), with the shifted test the feed-quality monitor uses: 14 digits, TAC not in the
GSMA version, ending `0`, and made a GSMA TAC by one leading digit. Every day is judged against
version 2, v2026.09.16, including 5 and 6 October, after v2026.10.04 became the active version (5
October 05:21 UTC); one version for all days keeps them comparable. The samples behind "whose
handset" (section 2, the 1% table), the mechanism (section 3, first point) and the remaining tenth
(section 5) use the same version. The lifecycle, spread, owner and dual-SIM figures were measured with
the version active at the time, v2026.10.04, which adds 945 TACs to v2026.09.16's 270,885; the two were
not compared figure by figure. Samples are deterministic: by number, `cityHash64(msisdn) % 20 = 0` (5%) or
`% 100 = 0` (1%); the owner figure is by IMEI, `cityHash64(imei) % 200 = 0` (0.5%). Each figure says
which. The per-day table below was computed from all rows, one query per
day, and matches `agg_device_change_daily` on every day. Its categories are exclusive and add up to
the day's total: touching a shifted IMEI; otherwise on a multi-number SIM; otherwise with a SIM change;
otherwise the rest.

## Every day, 1 September – 6 October

| Day | Handset changes | Touching a shifted IMEI | of which real→shifted | shifted→real | shifted→shifted | Multi-number SIM | With a SIM change | The rest |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 2026-09-01 | 209,884 | 484 | 220 | 237 | 27 | 22,930 | 5,890 | 180,580 |
| 2026-09-02 | 209,928 | 449 | 202 | 226 | 21 | 21,196 | 6,044 | 182,239 |
| 2026-09-03 | 206,008 | 461 | 210 | 229 | 22 | 20,688 | 5,690 | 179,169 |
| 2026-09-04 | 197,549 | 395 | 178 | 200 | 17 | 20,699 | 4,362 | 172,093 |
| 2026-09-05 | 184,500 | 352 | 159 | 167 | 26 | 16,412 | 4,988 | 162,748 |
| 2026-09-06 | 188,402 | 452 | 202 | 225 | 25 | 4,937 | 4,467 | 178,546 |
| 2026-09-07 | 173,894 | 388 | 181 | 182 | 25 | 901 | 3,636 | 168,969 |
| 2026-09-08 | 176,531 | 431 | 203 | 193 | 35 | 1,166 | 4,582 | 170,352 |
| 2026-09-09 | 174,807 | 421 | 191 | 203 | 27 | 1,958 | 5,255 | 167,173 |
| 2026-09-10 | 172,721 | 437 | 222 | 193 | 22 | 2,544 | 4,462 | 165,278 |
| 2026-09-11 | 151,283 | 371 | 198 | 159 | 14 | 2,890 | 2,590 | 145,432 |
| 2026-09-12 | 158,125 | 409 | 199 | 194 | 16 | 3,292 | 3,963 | 150,461 |
| 2026-09-13 | 183,958 | 406 | 186 | 202 | 18 | 4,225 | 3,751 | 175,576 |
| 2026-09-14 | 187,243 | 684 | 208 | 458 | 18 | 3,268 | 4,172 | 179,119 |
| 2026-09-15 | 188,073 | 4,677 | 4,450 | 209 | 18 | 1,997 | 4,840 | 176,559 |
| 2026-09-16 | 497,502 | 136,075 | 122,958 | 6,332 | 6,785 | 20,170 | 7,028 | 334,229 |
| 2026-09-17 | 797,329 | 525,314 | 187,990 | 97,989 | 239,335 | 33,843 | 9,791 | 228,381 |
| 2026-09-18 | 682,375 | 460,538 | 98,784 | 113,481 | 248,273 | 31,471 | 8,293 | 182,073 |
| 2026-09-19 | 729,582 | 500,350 | 125,131 | 103,644 | 271,575 | 34,329 | 10,269 | 184,634 |
| 2026-09-20 | 899,375 | 624,719 | 136,353 | 139,530 | 348,836 | 41,299 | 10,940 | 222,417 |
| 2026-09-21 | 965,670 | 669,294 | 157,546 | 140,490 | 371,258 | 44,132 | 12,407 | 239,837 |
| 2026-09-22 | 979,335 | 686,915 | 148,957 | 150,187 | 387,771 | 38,742 | 10,384 | 243,294 |
| 2026-09-23 | 968,969 | 678,782 | 154,045 | 146,811 | 377,926 | 37,850 | 11,049 | 241,288 |
| 2026-09-24 | 935,987 | 659,261 | 156,800 | 146,198 | 356,263 | 35,118 | 10,519 | 231,089 |
| 2026-09-25 | 759,723 | 514,067 | 107,740 | 140,729 | 265,598 | 29,866 | 9,057 | 206,733 |
| 2026-09-26 | 719,685 | 473,737 | 119,742 | 107,403 | 246,592 | 30,926 | 11,182 | 203,840 |
| 2026-09-27 | 836,454 | 555,038 | 127,981 | 120,321 | 306,736 | 36,792 | 11,668 | 232,956 |
| 2026-09-28 | 739,215 | 464,922 | 108,054 | 110,069 | 246,799 | 35,943 | 11,626 | 226,724 |
| 2026-09-29 | 775,643 | 521,739 | 119,642 | 100,846 | 301,251 | 26,117 | 9,203 | 218,584 |
| 2026-09-30 | 720,844 | 459,363 | 90,941 | 111,013 | 257,409 | 18,034 | 9,145 | 234,302 |
| 2026-10-01 | 686,848 | 438,852 | 99,907 | 95,599 | 243,346 | 15,258 | 10,280 | 222,458 |
| 2026-10-02 | 596,409 | 379,961 | 79,860 | 88,699 | 211,402 | 10,635 | 4,455 | 201,358 |
| 2026-10-03 | 608,731 | 394,420 | 91,808 | 80,769 | 221,843 | 10,434 | 5,488 | 198,389 |
| 2026-10-04 | 677,817 | 445,125 | 92,126 | 94,235 | 258,764 | 9,290 | 5,593 | 217,809 |
| 2026-10-05 | 643,665 | 412,605 | 85,762 | 90,022 | 236,821 | 8,834 | 5,392 | 216,834 |
| 2026-10-06 | 655,940 | 417,660 | 91,342 | 88,058 | 238,260 | 10,254 | 6,192 | 221,834 |
