# ADR-004 — Data ingestion strategy: immutable event log with derived state, TAC joined at query time

- **Status:** Accepted
- **Date:** 2026-09-12

---

## Context

Profiling established four facts that together determine the ingestion design:

1. **The feed is set-semantic.** `add` and `remove` toggle a `(msisdn, imsi, imei)` binding. Verified by an
   alternation test over 8,062,257 transitions: **0.383% violations, and every one of them a repeated
   `remove` — zero repeated `add`**. Net balance per binding never exceeds +1.
2. **The feed is order-dependent.** The same binding is added and removed repeatedly across days
   (within one batch, 14.8M quads appear in 2 files, 7.5M in 3, 3.9M in 4). Applying files out of order
   produces a wrong state.
3. **The feed is undated.** No timestamp exists in any delta file. Ordering by `(batch timestamp, filename)`
   was validated independently via the TAC allocation-date frontier, which advances monotonically across all
   82 files in that order.
4. **The feed contains systematic, benign anomalies.** 19.18% redundant adds and 1.77% orphan removes,
   concentrated at the boundary after the initial dump — consistent with a missing window of deltas.

There is also a delivery protocol the source already provides and which nothing should ignore: `1.config`
(column-order contract), `start.txt` / `finish.txt`, and **`1.done`** (completion sentinel). One of the six
observed batches lacks `1.done`.

## Options considered

### Storage of change data

**A. Store only current state, apply deltas destructively.**
Smallest footprint. Rejected: churn analysis and SIM-swap detection are confirmed in scope and both need
history. Reprocessing would also be impossible — a mistake could not be undone without a full re-import from
files.

**B. Immutable event log, current state derived from it — chosen.**
`binding_event` is append-only and is the system of record; `binding_current` is a fold over it. Costs roughly
2× storage. Buys: full churn analytics, safe reprocessing, and the ability to rebuild every derived structure
from the log. At ~50–60 GB/year compressed, the cost is not material and retention was confirmed as indefinite.

**C. Full snapshot per day.**
~108M rows/day × 365 = 39B rows/year. Trivially simple queries, absurd storage. Rejected.

### Idempotency

**A. Trust the source not to resend — rejected outright.** The data has no date, so a resent file is
undetectable from content alone. Without a guard, a redelivered day would silently corrupt the state.

**B. Content hash (SHA-256) as the idempotency key — chosen.** Computed while streaming, before any row is
applied. A byte-identical file is recorded and refused. Cheap, exact, and requires nothing from the source.

**C. Row-level deduplication.** Would need a uniqueness check across 800M+ rows on every load. Far more
expensive, and it would incorrectly suppress legitimate repeated events — the same binding genuinely *is*
added and removed repeatedly across days.

### TAC enrichment timing

**A. Enrich at ingest, store the denormalised result.**
Fastest dashboards. Rejected: a TAC correction would require reprocessing hundreds of millions of rows, and
the TAC file is updated periodically.

**B. Store only `tac`, join at query time, keep every TAC version — chosen.**
The dimension is 270,166 rows — small enough to sit in memory in any engine, making the join nearly free.
Corrections take effect everywhere immediately. Historical reproducibility is preserved because every TAC
version is retained and a query can pin one.

**C. Enrich at ingest but keep a version reference for reprocessing.**
Combines the storage cost of A with the complexity of B. Rejected.

## Decision

### Pipeline

```
discover batch → 1.done gate → read 1.config → SHA-256 → schema check
  → stream validate → quarantine invalid → append binding_event
  → fold into binding_current → refresh aggregates → record counters
```

Every stage records counts. A batch that fails part-way leaves `binding_event` consistent because the append
is transactional per file and each file is tagged with its `batch_id`.

### Ordering and sequence

Each ingested file receives a monotonic `seq`. `import_batch` carries a **nullable `data_date`**, to be
backfilled when real dates are obtained, at which point every chart switches to a calendar axis with no schema
change. Until then, the UI labels the axis *delivery sequence*, not date.

### Anomaly handling

| Situation | Action | Measured rate |
|---|---|---:|
| `add` for an already-active binding | no-op, counted | 19.18% |
| `remove` for an inactive binding | no-op, counted | 1.77% |
| `imei = '000000'` | keep, tag *Unknown device* | 6.97% / 2.96% |
| `length(imei) <> 14` | keep, flag, skip TAC join | 0.02% |
| malformed IMSI / MSISDN | quarantine with rule and raw line | 33 rows |
| `label` outside {add, remove} | reject the file | 0 observed |
| duplicate content hash | refuse the batch | — |
| missing `1.done` | hold the batch, alert | 1 of 6 batches |

The two large rates are **properties of the feed, not defects**. They are counted and displayed on the data
quality dashboard rather than "cleaned" — suppressing them would hide a genuine change in source behaviour.

### Schema evolution

The `1.config` file is the authoritative column contract, not the CSV header. Detected changes are graded:

| Change | Severity | Action |
|---|---|---|
| New column appended | low | accept, warn, store in an overflow column |
| Column reordered | medium | accept if `1.config` declares it; otherwise quarantine |
| Column renamed | high | quarantine, require operator confirmation |
| Column type changed | high | quarantine |
| Required column missing | critical | reject |

No schema change is ever silent.

### Reprocessing

Because `binding_event` is immutable and every row carries `batch_id`, a batch can be withdrawn by deleting
its events and re-folding `binding_current` from `seq = 0`. A full rebuild from the source files is the
ultimate fallback, and the measured load throughput makes it practical rather than theoretical.

## Consequences

- **Positive:** reprocessing is safe; churn and SIM-swap analytics are possible at all; TAC corrections are
  instant and cost no reprocessing; a resent file cannot corrupt state; no schema drift goes unnoticed.
- **Negative:** ~2× storage versus current-state-only. Accepted — indefinite retention was confirmed and the
  absolute figures are small.
- **Negative:** a query-time join is required for every enriched query. Mitigated by the dimension's tiny size
  and by pre-aggregated marts for dashboards.
- **Risk:** the ordering assumption, while independently validated, remains an assumption. If a future
  delivery arrives out of order and the source still supplies no dates, the system cannot detect it. This is
  recorded as RK1 and is the strongest argument for obtaining real dates from the source team.
