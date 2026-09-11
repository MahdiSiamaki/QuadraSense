# ADR-003 — Analytics store: ClickHouse

- **Status:** Accepted (sizing to be re-validated on production hardware)
- **Date:** 2026-09-12
- **Evidence:** `tools/benchmark/RESULTS.md` — measured on the real dataset, not synthetic

---

## Context

The analytics store must serve three confirmed workloads over a dataset that grows by ~3.0B events/year:

| Workload | Shape | Current volume |
|---|---|---|
| Device population analytics | aggregate + dimension join | ~108M active bindings |
| Churn / SIM-swap / migration | full-history scans, high-cardinality group-by | 804M events, +3.0B/year |
| Subscriber lookup | point lookup by key | ~227M distinct bindings |

Daily write: 8.26M events. Retention: indefinite (confirmed).

## Options considered

### Measured head-to-head — PostgreSQL 17 vs ClickHouse 25.8

Identical data, identical hardware, identical client-protocol load path, PostgreSQL tuned generously
(`fsync=off`, `synchronous_commit=off`, unlogged staging, 6 parallel workers). Full method in
`tools/benchmark/RESULTS.md`.

| Measure | PostgreSQL 17 | ClickHouse 25.8 | Ratio |
|---|---:|---:|---:|
| Load 125.9M rows | 829 s (staging only) | **195 s** (+60 s optimise) | **~3.2×** |
| …to a *queryable, sorted, typed* table | + **> 2 h** transform (single-threaded) | **included in the load** | **~12×** |
| On disk | 8.92 GiB (unsorted text, no index) | **2.60 GiB** (sorted, typed) | **3.4×** |
| TAC dimension | 28.3 s / 159 MB | **3.25 s / 39.96 MiB** | 8.7× / 4.0× |
| **Q1 dashboard aggregate** | 87.5–146.7 s | **3.6–4.0 s** | **~22×** |
| Q2 high-cardinality churn | 187.9 s | **52.1–61.8 s** | **3.6×** |
| Q4 drill-down | *(see results)* | 2.3–5.7 s | |

The decisive number is **Q1: 90 s versus 4 s** on the single most common query shape in the product — and
PostgreSQL's figure is against a *staging* table, having not yet paid the 2-hour cost of proper modelling.

### A. ClickHouse — **chosen**

- **Pros:** fastest on every measured shape; 3.4× less storage; sorts and types during load rather than in a
  separate hours-long step; `MergeTree` handles 3B rows/year on a single node comfortably; materialised views
  give incrementally-maintained rollups; `ReplacingMergeTree` models the binding state directly; scale-out
  exists if ever needed, without a rewrite.
- **Cons:** **new to the team** — the one real cost of this decision. No enforced foreign keys, eventual
  deduplication semantics in `ReplacingMergeTree` (requires `FINAL` or explicit aggregation), and weaker
  single-row update support. None of these matter for an append-mostly analytics store; all of them would
  matter for operational data, which is why ADR-002 keeps a separate store.

### B. PostgreSQL for everything

- **Pros:** one engine; ADR-002 already uses it; enforced integrity; team can transfer SQL Server skills.
- **Cons:** measured at **~90 s for the primary dashboard query** — 180× over the 500 ms budget. It could be
  rescued with aggressive pre-aggregation, but that forecloses ad-hoc drill-down and churn analysis, which are
  confirmed requirements. The >2 h single-threaded transform is also a poor fit for a nightly window.
- **Rejected on measurement.**

### C. DuckDB over Parquet

- **Pros:** fastest raw numbers measured anywhere in this project (5.55M rows/s load; 2.3 s aggregation over
  117M rows). Excellent compression. No server to operate.
- **Cons:** embedded, single-writer, with no built-in multi-user concurrency, authentication, or resource
  governance. Serving many concurrent dashboard users would mean building a query-coordination layer
  ourselves. **Rejected as the serving engine** — but retained as the **profiling and verification tool**,
  which is what `tools/profiling/` already is.

### D. Microsoft SQL Server with Clustered Columnstore

- **Pros:** **on the team's skill list.** Columnstore with batch-mode execution is genuinely competitive for
  aggregation; excellent .NET integration; if licences exist, marginal cost is low.
- **Cons:** Express edition caps at **10 GB per database** — unusable here, so this requires Standard or
  Enterprise, priced per core. At 3B rows/year on a multi-core analytics server that is a substantial and
  recurring cost for capability ClickHouse provides at zero licence cost. Columnstore also requires more
  careful maintenance (rowgroup quality, index rebuilds) than `MergeTree`.
- **Not benchmarked** — honestly noted. It was excluded on licensing and the 10 GB Express ceiling rather
  than on measured performance. If licences already exist and the team prefers it, it deserves a benchmark
  before Phase 3 begins; the data model in `03-data-model.md` would port with modest change.

### E. Elasticsearch / OpenSearch

On the team's list, but wrong for this shape: poor at high-cardinality group-by over billions of rows, and
significant storage amplification. **Rejected.**

### F. Apache Druid / Pinot

Built for real-time ingestion and sub-second slice-and-dice, at the cost of substantial operational
complexity (multiple coordinator/broker/historical roles). We ingest once daily. **Rejected as
over-engineering** — the brief explicitly warns against this.

## Decision

**ClickHouse 25.8** as the analytics store, alongside PostgreSQL as the operational store (ADR-002).

- `binding_event` — `MergeTree`, partitioned by sequence bucket, ordered by `(msisdn, imsi, imei, seq)`.
- `binding_current` — `ReplacingMergeTree` ordered by `(msisdn, imsi, imei)`.
- `tac_model` — small dimension, joined at query time (ADR-004).
- Daily marts — incrementally maintained materialised views.

## A finding that changes the design: pre-aggregation is mandatory, not an optimisation

Even ClickHouse takes **3.6–4.0 s** for the top-manufacturer aggregate and **52 s** for the churn
distribution over 126M rows. Against a 500 ms P95 budget, **neither engine can serve dashboards from raw
data.**

This is the most useful thing the benchmark revealed. It means the mart layer in `03-data-model.md` is not a
later optimisation to be added if things feel slow — it is load-bearing from day one, and Phase 3 must
deliver it alongside the importer. The engine choice determines whether *ad-hoc drill-down beneath* the
marts is possible at all: at 4 s ClickHouse makes exploratory queries viable; at 90 s PostgreSQL does not.

## Consequences

- **Positive:** every measured workload is met or made feasible; 3.4× less storage; no separate transform
  step in the nightly window; headroom for years of growth on one node; no licence cost.
- **Negative:** a technology the team must learn. This is a genuine cost and it is deliberately concentrated
  here — ADR-001 and ADR-005 both chose the team's existing strengths precisely so the learning budget could
  be spent on this one component, which is operated through SQL and configuration rather than written against
  daily.
- **Negative:** two stores to operate and back up. Justified: the alternative is serving one workload badly.
- **Risk:** benchmarked on a 6-core laptop. Production sizing must be re-validated — recorded as RK5.
- **Reversible?** Partially. The data model is portable and the event log can be replayed into any engine from
  the source files. Query code would need rewriting. Revisit only if measurement, not preference, demands it.
