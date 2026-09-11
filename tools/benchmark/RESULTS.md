# Storage Engine Benchmark — Results

**Date:** 2026-09-12
**Data:** the real dataset — `dump_subs_device_sim_info_20251227_20260125.csv` (125,939,523 rows, 4.86 GiB)
and `DeviceDatabase_TAC1Sep2026.csv` (270,166 rows).

## Hardware and method

| | |
|---|---|
| CPU | Intel i5-11400H — 6 cores / 12 threads |
| RAM | 15.7 GB total (Docker VM: 8.18 GB) |
| Disk | NVMe SSD |
| PostgreSQL | 17-alpine, `--memory=4g --cpus=6` |
| ClickHouse | 25.8.33.6, `--memory=3g --cpus=6` |

**Fairness controls:**

- Both engines' **data directories are native Docker volumes**, never Windows bind mounts. An earlier run
  placed PostgreSQL's data directory on a bind mount; those numbers were discarded and the run repeated.
  (The measured effect turned out to be modest — 910 s vs 829 s, about 9% — but the correction was necessary
  to make the comparison defensible.)
- **Source CSVs are bind-mounted read-only, identically for both.**
- Both load **through the client protocol** — PostgreSQL `\copy`, ClickHouse `cat … | clickhouse-client`.
  Neither engine is given a server-side direct-file-read advantage the other lacks.
- PostgreSQL is **tuned, not default**: `shared_buffers=1536MB`, `work_mem=256MB`,
  `maintenance_work_mem=1GB`, `max_parallel_workers_per_gather=6`, `random_page_cost=1.1`,
  `effective_cache_size=4GB`, and — generously — `fsync=off` with `synchronous_commit=off` and an
  `UNLOGGED` staging table. This is the most favourable durability configuration possible; a production
  PostgreSQL would be slower.
- Identical column types: `msisdn`/`imsi` as 64-bit integers, `imei` as text (required — `000000` and other
  short values carry significant leading zeros).

**Caveat:** this is a development laptop. Absolute numbers will not match the production server. The purpose
is relative behaviour on *this* data and *these* query shapes, and to surface any disqualifying weakness.

---

## L1 — Bulk load

| Measure | PostgreSQL 17 | ClickHouse 25.8 | Ratio |
|---|---:|---:|---:|
| Load 125,939,523 rows | **829.4 s** | **~174 s** | **4.8×** |
| Throughput | 151,845 rows/s | **725,256 rows/s** | **4.8×** |
| Load TAC (270,166 rows) | 28.3 s | **3.25 s** | **8.7×** |
| Base table on disk | **9,131 MB** | *(see below)* | |
| TAC table on disk | 159 MB | **39.96 MiB** | **4.0×** |

For reference, the same CSV → Parquet+zstd with DuckDB took **22.7 s** (5.55M rows/s) producing **2.13 GiB**.

### The hidden cost: getting PostgreSQL into a *queryable* shape

The figures above are only the raw COPY into a staging table. To reach a properly modelled table —
typed columns, a derived `tac` column, and a primary key on `(msisdn, imsi, imei)` — PostgreSQL additionally
needs a transform and index build. Measured rate of that transform: **~76 MB/min**, single-threaded
(PostgreSQL does not parallelise `INSERT … SELECT`), projecting to **over 2 hours** before the primary-key
build even starts. The run was abandoned at that projection.

ClickHouse does the equivalent work — parse, type, derive `tac`, and sort by the `ORDER BY` key — **as part
of the load itself**. There is no separate transform step.

This is arguably the most important operational finding in the whole benchmark: it is the difference between
a nightly pipeline that fits in its window and one that does not.

---

### Final load figures

| Measure | PostgreSQL 17 | ClickHouse 25.8 | Ratio |
|---|---:|---:|---:|
| Load 125,939,523 rows | **829.4 s** | **195 s** | **4.3×** |
| …plus sort/merge to final form | *(transform: >2 h, abandoned)* | **+60 s** (`OPTIMIZE FINAL`) | |
| **Total to a queryable, sorted, typed table** | **> 2 h** | **255 s** | **~28×** |
| Throughput | 151,845 rows/s | **645,844 rows/s** | 4.3× |
| Base table on disk | **8.92 GiB** (unsorted text, no index) | **2.60 GiB** (sorted, typed) | **3.4×** |
| TAC load / size | 28.3 s / 159 MB | **3.25 s / 39.96 MiB** | 8.7× / 4.0× |

---

## Query results

Client-side wall clock (3 runs each). **ClickHouse figures also reported server-side** from
`system.query_log`, because `docker exec` plus `clickhouse-client` startup adds roughly 400 ms — negligible
for multi-second queries but completely dominant for a point lookup.

| Query | PostgreSQL (median) | ClickHouse (client) | ClickHouse (**server**) | Rows read (CH) | Ratio (server) |
|---|---:|---:|---:|---:|---:|
| **Q1** dashboard aggregate | **90,449 ms** | 4,014 ms | **3,481 ms** | 126.2M | **26×** |
| **Q4** drill-down | **90,821 ms** | 3,182 ms | **2,587 ms** | 126.1M | **35×** |
| **Q2** churn distribution | **187,876 ms** | 53,409 ms | **52,459 ms** | 125.9M | **3.6×** |
| **Q3** point lookup | 370 ms | 429 ms | **12 ms** | **278K** | see below |

Both engines returned identical results on every query (e.g. Samsung Korea 52,696,268; Xiaomi 28,090,873),
which is the correctness check that makes the timings meaningful.

### Q3 deserves a note

ClickHouse answered the point lookup in a **median of 12 ms (min 9 ms)** by reading **278,360 of
125,939,523 rows** — its sparse primary index on `(msisdn, imsi, imei)` skipped 99.8% of the table. This
matters because point lookup is the workload column stores are traditionally *bad* at, and it was the main
reason to suspect ClickHouse might not be able to serve all three workloads. It can.

**PostgreSQL competes on this query once indexed, and that is the honest finding** — 370 ms client-side,
which is largely the same `docker exec` overhead ClickHouse pays. Point lookup is not where PostgreSQL
loses.

What it costs to get there is the real difference:

| | PostgreSQL | ClickHouse |
|---|---:|---:|
| Index build on 125.9M rows | **400 s** | **0 s** |
| Extra storage for the index | **2,936 MB** | **0 MB** |

ClickHouse pays nothing because the ordering that makes aggregates fast — `(msisdn, imsi, imei)` — is the
same ordering that makes the lookup fast. PostgreSQL needs a separate 2.9 GB B-tree, rebuilt on every
restore, on top of the 8.92 GB table. So the comparison on Q3 is not "faster" but "already paid for".

---

## Interpretation

### 1. ClickHouse wins every measured workload

26–35× on the dashboard query shapes, 3.6× on the heavy churn aggregate, 3.4× less storage, and ~28× faster
to reach a queryable state. PostgreSQL was given every advantage — `fsync=off`, unlogged staging, 6 parallel
workers, a generous buffer pool — and was still measured against a *staging* table it had not yet paid the
2-hour cost of properly modelling.

### 2. The most important finding is not about the winner

**Neither engine can serve dashboards from raw data.** Against a 500 ms P95 budget:

- ClickHouse's best dashboard query is **3.5 s** — 7× over budget.
- The churn distribution is **52 s** — 100× over budget.
- PostgreSQL is 90 s and 188 s respectively.

So the **pre-aggregation / mart layer is mandatory from day one**, not an optimisation to add later if things
feel slow. This single result reshapes Phase 3: the importer and the aggregate refresh must be built
together, because the product does not work without both.

What the engine choice actually buys is **what happens when a user drills past the pre-aggregates**. At 3.5 s
ClickHouse makes ad-hoc exploration viable; at 90 s PostgreSQL does not. Given that churn analysis and
subscriber lookup are confirmed requirements, that difference is the decision.

### 3. Q2 is the shape to watch

The churn distribution (group by 79M distinct MSISDNs) is slow on both engines because it is genuinely hard —
high cardinality, no filter, no index can help. It must be served from a pre-computed daily rollup rather than
computed on demand. Worth designing deliberately rather than discovering in production.

### 4. What this benchmark does *not* establish

- **Concurrency.** Every measurement is single-user. Behaviour under 10–50 concurrent dashboard users is
  unmeasured and depends on the confirmed user count.
- **Production hardware.** A 6-core laptop with 8 GiB available to Docker is not a server. Absolute numbers
  will change; the relative ordering is unlikely to.
- **MS SQL Server with Clustered Columnstore**, which was excluded on licensing grounds (Express caps at
  10 GB) rather than measured. If licences already exist, it deserves a run before Phase 3.
- **The event-log workload.** Only the 126M-row current state was benchmarked, not the 678M-row event
  history or the 3B-row/year projection.
