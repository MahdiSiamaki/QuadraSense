# ADR-014: Risk signals - levels, measures and thresholds

- **Status:** Accepted for the design; **thresholds pending** the product owner's review capacity per list
- **Date:** 2026-10-02
- **Decided by:** the product owner (levels, feed-defect handling, SIM-change definition, no score,
  versioned configuration with a review-capacity cut, who may see it - all on 2026-10-02)

## Context

Phase 4 asks for risk signals: handsets shared by many SIMs, SIMs moving across many handsets,
numbers changing SIM repeatedly - each explained, never a verdict. Three facts shaped every choice
below.

1. **The data has no calls, traffic, location or time of day.** Only adds and removes of
   (number, SIM, IMEI) bindings, a file a day. Anything the system says is about observed changes.
2. **The feed has known defects that look exactly like the signals.** From 2026-07-27 one SIM is
   listed under several numbers on the same day; from 2026-09-15 IMEIs arrive shifted by a digit;
   on 08-25/26 IMEIs were cut to eight digits (docs/architecture/15-feed-quality.md). Each one
   inflates "SIMs per IMEI" or "IMEIs per SIM" without anybody doing anything.
3. **Population passes are minutes, not seconds.** One 30-day pass over the SIM copy of the binding
   history took 190-255 s even split eight ways (2026-10-02), against the Explorer's 30 s budget.

## Decision

### Levels (owner's decision 1)

| Level | Meaning | Who assigns it |
|---|---|---|
| Observation | A counted fact | the system |
| Anomaly | Out of line, but not evidence about a subscriber: an all-time count, or a crossing in a window that overlaps flagged feed days | the system |
| Risk signal | A threshold crossed in a clean window, on clean evidence | the system |
| Suspected pattern | Risk signals of two different families on linked entities | the system |
| Confirmed fraud | A person's verdict | never the system; Phase 4 records none |

One function decides every level - `Sqm.Domain.Risk.RiskRules.Assess` - and writes the reason with
it. A level states the quality of the evidence, not how bad something is. "Ever" and "not removed"
counts stop at Anomaly: they accumulate nine months of ordinary churn (of SIMs not removed from
exactly two IMEIs, 83.7% sit on two different models). **No numeric score** (decision 4).

### Feed defects (decision 2)

- **Rows set aside.** An add is not counted when its IMEI is not countable - not 14 digits, fourteen
  zeros, or the shifted shape (the feed-quality test, unchanged) - or when the feed-quality monitor
  listed its SIM under several numbers that day (`dq_multi_number_sim_day`). Raw counts sit beside
  clean ones, so the set-aside is visible.
- **Families capped.** Row screens are not proven complete, so a crossing whose window overlaps days
  flagged for a check relevant to the rule is shown as an Anomaly, never a Risk signal: IMEI rules
  by shifted IMEIs and multi-number SIMs, SIM rules by those and malformed IMEIs, number rules by
  multi-number SIMs. Multi-number SIMs are flagged on every day from 2026-07-28, so with the feed as it is
  **every window that ends today is capped**: until corrected files for 2026-07-27..09-26 arrive
  (the owner is asking the operator), lists show anomalies, not risk signals.
- **Not assessable.** Above `MaxDefectShare` of an entity's adds set aside, it is listed in the
  data-quality view only, in neutral colours, under "Not behaviour".

### SIM change (decision 3)

The dashboard's same-day definition: a number that lost one SIM and gained another on the same
day. Kept as rows per day (analytics migration 023, `risk_sim_change_day`); "24 h" is the latest
data day and "48 h" the last two. A change whose old SIM is removed on another day is missed by
design.

### Measures precomputed, verdicts judged when read

Analytics migration 024 stores counts per SIM and per IMEI (30-, 20- and 7-day windows, and all
time per IMEI), built by the import worker one key-range chunk per idle moment and published only
when every chunk is written, no key appears twice and nothing it read has changed. The API filters
and sorts those rows and judges each returned row with the configured rule set and the flagged days.
A threshold change needs no recompute. Rows are stored only from a floor up (6 for windowed counts,
21 SIMs ever or 6 not removed per IMEI); a threshold below its floor is refused.

### Thresholds (decision 5)

**No threshold has a default.** Each is cut from the clean reference window at the value where the
list holds the number of entities analysts can review - the owner gives that capacity per list, from
the curves below. Until then the risk endpoints answer 503 "not calibrated" and nothing is judged.
The configuration section `Risk` carries the thresholds; its 8-character version hash is quoted in
every reason and every audit entry. A list request may carry its own threshold, from the floor up;
it is audited and never saved.

### Who sees it (decision 6)

`risk.view` (operational migration 014), for Analyst and Administrator only. A list also needs the
lookup permission of the unit it names; `identifier.reveal` decides masking; export needs
`data.export`. Identifiers travel only in POST bodies; audits record rule, view and thresholds,
never an identifier. A top-level "Risk signals" page after Explorer, and a risk section in the
entity panel.

## Measured on the real data, 2026-10-02

### M1 - Reference window

`REF = 2026-06-02..07-01`: all 30 days present, none flagged at ×3 or ×5 on any check. Eleven days
of the 244 are missing from the feed (05-08, 05-09, 05-11, 05-12, 05-13, 05-17, 05-18, 07-04, 07-05,
07-08, 08-16); every window says how many of its days have data. The recent window compared with it
is `2026-08-28..09-26`, the 30 days to the latest data.

### M2, M3, M4 - The curves to cut from

Entities over each candidate threshold, clean counts. Reference is the window above; recent is the
30 days to 2026-09-26 after the screens.

**HighDeviceCount30 - SIMs by IMEIs added in 30 days**

| more than | 10 | 20 | 30 | 50 | 75 | 100 | 150 | 200 | 300 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| reference | 234,165 | 63,112 | 28,107 | 9,162 | 3,455 | 1,772 | 736 | 446 | 188 |
| recent | 257,389 | 52,662 | 21,743 | 7,194 | 2,951 | 1,637 | 691 | 374 | 178 |

**RapidDeviceChange7 - SIMs by IMEIs added in 7 days**

| more than | 5 | 10 | 15 | 20 | 30 | 50 | 75 | 100 | 150 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| reference | 74,321 | 17,426 | 7,129 | 3,657 | 1,362 | 474 | 228 | 141 | 93 |
| recent | 149,178 | 23,149 | 8,366 | 4,289 | 1,752 | 585 | 275 | 147 | 82 |

**Randomisation20 - SIMs by IMEIs and TACs added in 20 days, both crossed**

| more than (IMEIs / TACs) | 20 / 10 | 20 / 15 | 30 / 20 | 50 / 25 | 50 / 40 | 100 / 50 |
|---|---:|---:|---:|---:|---:|---:|
| reference | 28,404 | 28,031 | 11,260 | 3,173 | 2,970 | 458 |
| recent | 28,219 | 27,651 | 10,537 | 3,184 | 3,044 | 519 |

**SharedImeiSims30 - IMEIs by SIMs added in 30 days**

| more than | 10 | 20 | 30 | 50 | 75 | 100 | 150 | 200 | 300 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| reference | 177,312 | 49,321 | 22,695 | 8,430 | 3,801 | 2,204 | 1,133 | 771 | 471 |
| recent | 291,528 | 58,587 | 23,686 | 8,374 | 3,967 | 2,447 | 1,346 | 847 | 541 |

**SharedImeiNumbers30 - IMEIs by numbers added in 30 days.** Within 0.5% of SharedImeiSims30 at every
threshold (reference: 49,308 over 20, 2,202 over 100): on this feed a number and a SIM almost always
arrive together, so the two rules list nearly the same handsets.

**RepeatedSimChange7 - numbers by days with a SIM change in 7 days** (reference 06-25..07-01, recent
09-20..09-26)

| more than | 1 | 2 | 3 | 4 |
|---|---:|---:|---:|---:|
| reference | 550 | 49 | 7 | 1 |
| recent | 19,876 | 1,556 | 171 | 9 |

### M4 - SIM changes per day, and the parity check

`risk_sim_change_day` was written for all 233 days (47 minutes, 0 failed) and matches the
dashboard's `agg_sim_change_daily` on every day: **0 of 233 days differ, 0 duplicate rows**. Of
2,495,857 changes, 1,504,018 (60%) are set aside as multi-number days. On ordinary days 1-3% are set
aside; from 2026-08-04 to 09-05 and from 09-16 on it is 60-80%. There is also a smaller episode
from 2026-04-14 to 05-05 (20-59% set aside), before the documented defect - noted, not explained.

### M5 - Residual inflation after the screens (partial)

The screens bring the recent tails back to the reference above about 15 (SIM side, over 20: 52,662
recent against 63,112 reference; raw would be 152,288). Below that, recent is still higher - IMEIs
by SIMs over 10: 291,528 against 177,312 - and numbers changing SIM on more than one day of a week
are 36 times the reference after the screens. **The family caps stay**, as decided: the residual is
real, and a cut from the reference would list feed defects as behaviour. `MaxDefectShare` is still
unset (no entity is excluded by share) until the per-entity share distribution is measured.

### M7, M8 - Cost and size of a snapshot run

The first run on the real data (run 1790938349551, as of 2026-09-26) was built by
`--refresh-risk` with nothing else running, on the NVMe data disk, at the import's limits (one
thread, 1.2 GB, spilling at 300 MB), 24 chunks per table. It was published after **40.2 minutes**.
From `system.query_log`:

| Table | Statements | Total | Mean | Slowest | Peak memory | Rows read | Spilled parts |
|---|---:|---:|---:|---:|---:|---:|---:|
| risk_sim_window | 24 | 1,070 s | 44.6 s | 59.1 s | 823 MiB | 197,544,952 | 2,199 |
| risk_imei_window | 24 | 453 s | 18.9 s | 24.3 s | 415 MiB | 283,914,890 | 630 |
| risk_imei_lifetime | 24 | 880 s | 36.7 s | 55.7 s | 567 MiB | 718,152,408 | 2,762 |

Every statement stayed far below the 1,500 s statement limit, and the spill counts show the limits
arrived. The SIM statement's peak is the one to watch: 823 MiB against a 1.2 GB cap. If the data
grows, raise `Risk:Compute:Chunks` before anything else.

| Table | Rows | On disk |
|---|---:|---:|
| risk_sim_window | 2,442,607 | 117 MB |
| risk_imei_window | 1,241,642 | 33 MB |
| risk_imei_lifetime | 1,741,839 | 35 MB |

About 185 MB a run. Two published runs are kept.

**The stored measures are the calibration's, value for value.** For every value at or above the
floor of 6, the published histograms of `imeis_30`, `imeis_30_raw`, `imeis_7`, `imeis_7_raw`,
`max_imeis_one_day_30`, `sims_30`, `sims_30_raw`, `sims_7`, `numbers_30`, `max_sims_one_day_30` and the
(`imeis_20`, `tacs_20`) pairs equal the M2/M3 recent-window histograms: **0 differing values in 11
comparisons**. A threshold cut from those curves means the same thing when a list applies it.

**All-time curves**, which have no reference window (they are counts as of the run):

| more than | 20 | 30 | 50 | 100 | 200 | 500 | 1,000 |
|---|---:|---:|---:|---:|---:|---:|---:|
| SharedImeiSimsEver: IMEIs by SIMs ever | 1,734,339 | 819,362 | 310,284 | 80,588 | 20,248 | 3,305 | 1,016 |

| more than | 5 | 10 | 20 | 50 | 100 | 200 |
|---|---:|---:|---:|---:|---:|---:|
| SharedImeiSimsNotRemoved: IMEIs by SIMs not yet removed, dated | 84,849 | 15,139 | 3,598 | 850 | 304 | 106 |

### M9 - Read latency

Through the API's own reader, on run 1790938349551, every list at its storage floor - the largest it
can be, up to 1,734,339 entities - and on its deepest reachable page (rows 9,501-10,000), warm,
`Sqm.Integration.Tests.RiskRealDataTests`:

| List | Entities at the floor | Page 1 | Page 20 |
|---|---:|---:|---:|
| HighDeviceCount30, risk / data quality | 1,241,044 / 1,201,563 | 896 / 739 ms | 1,086 / 724 ms |
| RapidDeviceChange7 | 149,178 / 728,126 | 365 / 460 ms | 364 / 525 ms |
| Randomisation20 | 768,131 | 440 ms | 693 ms |
| SharedImeiSims30 | 1,076,843 / 153,500 | 206 / 125 ms | 448 / 201 ms |
| SharedImeiNumbers30 | 1,079,392 | 165 ms | 382 ms |
| SharedImeiSimsEver | 1,734,339 | 176 ms | 400 ms |
| SharedImeiSimsNotRemoved | 84,849 | 119 ms | 118 ms |
| RepeatedSimChange7, risk / data quality | 187,231 / 442,551 | 132 / 115 ms | 121 / 144 ms |

The overview's counts for all eight rules: 1.1 s. One entity: 11-36 ms. Its bound entities for the
pattern: 214 ms for the SIM added to the most IMEIs in the window (4,107 of them, none with stored
measures - checked directly). At calibrated thresholds every list is smaller than these.

## Not measured yet

M6 (serial structure of the top SIMs), M10 (day profile; the day-anomaly strip is not built), M11
(feed toggling on a clean window), M12 (precision of the shifted-IMEI exclusion), and the
per-entity share set aside that `MaxDefectShare` would be cut from (part of M5). None blocks the
lists; each is named here so it is not mistaken for done.

## Consequences

- Nothing is listed as a risk until the owner gives a review capacity per list and thresholds are
  set from the curves above, with this ADR updated to record the cut.
- With the current feed, every list is capped at Anomaly. Corrected files for 2026-07-27..09-26 are
  the way out; the capacity cut does not change that.
- A late or corrected day, a GSMA activation or a floor change makes the published measures stale,
  labelled on the page, until the worker's next idle run publishes new ones.
