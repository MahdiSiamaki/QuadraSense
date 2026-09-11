# Benchmark query set

The same five workloads are run against ClickHouse and PostgreSQL, on identical data loaded from the
identical source CSV files. Each query is run 3 times; the reported figure is the **median**, with the
cold (first) run reported separately where it differs materially.

## L1 — Bulk load (initial dump)

Load all 125,939,523 rows of `dump_subs_device_sim_info_20251227_20260125.csv` into the current-state table,
plus the 270,166-row TAC dimension. Measures the initial-load path.

**Metric:** wall-clock seconds, rows/second, resulting on-disk size.

## Q1 — Dashboard aggregate (the most common read)

Top 20 manufacturers by active binding count, joined to the TAC dimension.

Representative of essentially every dashboard widget: a group-by over the full current state with a
dimension join. This is the query that must stay fast under concurrency.

## Q2 — High-cardinality churn aggregate

Distribution of distinct IMEIs per MSISDN — a group-by over 126M rows producing ~79M groups, then a
second aggregation over that.

This is the hardest shape in the workload: very high cardinality, no filter, no index can help. It is the
foundation of the device-change and churn analytics the user asked for.

## Q3 — Point lookup (subscriber-level)

All bindings for one specific MSISDN, enriched with manufacturer and model.

Representative of the subscriber-lookup workload. Must be single-digit milliseconds — this is where a
row store traditionally beats a column store, and the honest test of whether one engine can serve all
three workloads.

## Q4 — Filtered drill-down

Active binding count by device type for a single manufacturer.

Representative of dashboard drill-down after a user clicks a bar. Tests predicate pushdown through the
dimension join.

## Q5 — Daily merge (the write path)

Apply one full day of deltas (7,095,661 rows: 3,268,729 `add`, 3,826,932 `remove`) to the current-state
table, idempotently.

This is the nightly pipeline step. It must complete well inside the daily window and must not degrade
read latency while it runs.

---

## Fairness notes

- Both engines receive the same column types: `msisdn`/`imsi` as 64-bit integers, `imei` as text
  (required — `000000` and other short values have significant leading zeros), `tac` as text.
- Both engines get an index/ordering appropriate to their design: ClickHouse `ORDER BY (msisdn, imsi, imei)`,
  PostgreSQL a matching B-tree primary key. Neither is handicapped.
- PostgreSQL is given a tuned configuration (raised `work_mem`, `maintenance_work_mem`, `shared_buffers`,
  `max_parallel_workers_per_gather`) rather than defaults, so the comparison is against a competently
  configured instance, not a straw man.
- Both run in Docker with the same memory ceiling, one at a time, on the same NVMe volume.
- **Both database data directories are native Docker volumes, never Windows bind mounts.** This matters a
  great deal on Docker Desktop with the WSL2 backend: a bind-mounted Windows path is reached through a
  filesystem bridge that is dramatically slower than a native volume for the small random writes a database
  performs. An early run of this benchmark placed PostgreSQL's data directory on a Windows bind mount, which
  handicapped it badly and invalidated those numbers; they were discarded and the run repeated. Only the
  **source CSV files** are bind-mounted, read-only, identically for both engines.

## Hardware caveat

This benchmark runs on a development laptop: Intel i5-11400H (6 cores / 12 threads), 15.7 GB RAM,
NVMe SSD. **Absolute numbers will not match the production server.** The purpose is to establish the
*relative* behaviour of the engines on this specific data and these specific query shapes, and to find
any disqualifying weakness. Final sizing must be re-validated once the production hardware is known.
