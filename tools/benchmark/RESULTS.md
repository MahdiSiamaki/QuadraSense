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

## Query results

*(populated below when the query run completes)*

---

## Interpretation

*(populated when complete)*
