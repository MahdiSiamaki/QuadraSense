# ClickHouse memory: what went wrong, and what the settings mean

A container-limited ClickHouse behaves very differently from the defaults it ships with. This
records what that cost here, because every symptom looked like something else and each fix
uncovered the next one.

Everything below is measured on the development machine: a 6 GiB container inside a 7.8 GiB WSL
VM on a 15.7 GiB host, holding 1.05 billion events and a 295-million-row current-state table.

---

## 1. The root cause: cache limits sized for a dedicated server

ClickHouse's defaults assume it owns the machine:

| Setting | Default | On a 6 GiB container |
|---|---:|---|
| `mark_cache_size` | 5 GiB | 83% of the container |
| `index_mark_cache_size` | 5 GiB | 83% of the container |
| `uncompressed_cache_size` | 8 GiB | 133% of the container |

**18 GiB of cache ceilings on a 6 GiB container**, and all of it counted by the server's memory
tracker. That single fact produced every memory symptom this project chased for two phases.

It is why the tracker never matched reality: **5.40 GiB reported against 1.15 GiB resident** in
Phase 0, and **4.51 GiB against 2.15 GiB** during the mart backfill. Queries were killed by the
`OvercommitTracker` while the container had gigabytes genuinely free, because the tracker was
full of cache and the machine was not.

Sized to the container, the tracker reports something close to the truth and every other limit
starts meaning what it says.

## 2. The workaround that made it worse

The first response to non-deterministic query kills was
`max_server_memory_usage_to_ram_ratio = 4`. It worked, in the sense that the kills stopped.

It worked by telling the server it could use **four times the VM's RAM**, so it never
self-limited at all. After the daily-partitioning migration left ~1,000 parts to merge, the
server grew until it hit the container's cgroup limit and stopped responding entirely. Nothing
was killed — it thrashed, and `SELECT 1` timed out for minutes.

**A server that consumes everything and then stops answering is misconfigured, not unlucky.**
The ratio is replaced by an explicit `max_server_memory_usage` below the container limit, so
ClickHouse throttles itself rather than being throttled by the kernel.

The first value tried, 4.5 GiB, was too tight: per-query limits held, each aggregate stayed under
1 GiB, and the total still reached the ceiling once background merges ran alongside. 4.5 was
chosen while the caches could still claim 18 GiB; with them bounded, the headroom is real and
the ceiling sits at 5.2 GiB.

## 3. Per-query limits that were never applied

Three ways of attaching resource limits to a statement were tried, and **each silently did
nothing**:

| Mechanism | Result |
|---|---|
| `ClickHouseConnection.CustomSettings` | Not sent. |
| `set_max_memory_usage=…` in the connection string | Not sent. |
| A trailing `SETTINGS` clause in the SQL | Works from `clickhouse-client`; arrived at the server without it through the driver. |

They are now **URL query parameters** on the HTTP request — the mechanism the bulk insert
already used, and the only path here that was never in doubt. The server reads settings from the
query string before it parses anything, so no client library sits between the intent and the
effect.

### How to tell a limit is actually applied

This is the part worth keeping, because **a limit that is not applied looks exactly like a limit
that is generous**. Nothing fails; the numbers are just bigger than you asked for.

Two checks, in order of reliability:

```sql
-- 1. Watch what a query actually uses while it runs.
SELECT formatReadableSize(memory_usage), round(elapsed), substring(query, 1, 60)
FROM system.processes;

-- 2. After it finishes, confirm it spilled. External parts cannot appear
--    unless max_bytes_before_external_group_by was honoured.
SELECT ProfileEvents['ExternalAggregationWritePart'], formatReadableSize(memory_usage)
FROM system.query_log WHERE type = 'QueryFinish' ORDER BY event_time DESC LIMIT 1;
```

`system.query_log`'s `Settings` map is **not** a reliable check: it records settings changed
through the protocol, and a query-level `SETTINGS` clause does not appear there even when it is
working.

## 4. `max_threads` is a memory setting

ClickHouse builds one hash table per thread, so thread count multiplies what a `GROUP BY` needs.
Measured on one day's churn aggregate — `GROUP BY (data_date, msisdn)` with `groupUniqArrayIf`
over ~8 million rows:

| `max_threads` | Peak memory | Duration | Spilled parts |
|---:|---:|---:|---:|
| 3 | ~1.9 GiB | 3.9 s | 18 |
| **1** | **565 MiB** | **3.9 s** | **122** |

Three times the memory for no useful speed-up, on a job that runs once a day. On a
memory-constrained node, lowering `max_threads` is the cheapest lever there is, and the one least
likely to be reached for.

## 5. What the numbers mean now

`infra/clickhouse/memory.xml`:

| Setting | Value | Why |
|---|---:|---|
| `max_server_memory_usage` | 5.2 GiB | ~87% of the 6 GiB container, so the server throttles before the kernel does |
| `mark_cache_size` | 512 MiB | Marks are the in-memory primary index — what makes a point lookup read 278K of a billion rows in 12 ms |
| `index_mark_cache_size` | 256 MiB | |
| `uncompressed_cache_size` | 128 MiB | Off by per-query default; this is a bound, not a reservation |

Per statement, as URL parameters:

| Setting | Value |
|---|---:|
| `max_memory_usage` | 1.2 GiB |
| `max_bytes_before_external_group_by` | 300 MB |
| `max_threads` | 1 |

**In production**, scale all of these with the memory the container is actually given: caches at
roughly 10–15% of it, the server ceiling at 70–80%, and per-query limits low enough that two
concurrent jobs plus background merges still fit underneath.

## 6. What remains, honestly

Even with all of the above, the mart backfill still loses the occasional day to the
`OvercommitTracker` when background merges coincide with an aggregate. The tracker still reports
more than RSS during a run — 5.20 GiB against 1.39 GiB in one case — and that residual gap is
not explained.

The batch job is built to converge rather than to be perfect: **every day is idempotent** (drop
the partition, rebuild it), failures are reported with the exact range to re-run, and repeating
the pass finishes the stragglers. That is a legitimate strategy because of the idempotency, not
in spite of it.

This is an environment limit rather than a design one. The machine is running a billion-row
analytics store, an operational database, a browser and an IDE in 15.7 GiB. A production node
with 32–64 GiB would not meet any of this.

---

## Related

- `infra/clickhouse/memory.xml` — the configuration, with the same history in comments
- `docs/adr/ADR-003-analytics-store.md` — why ClickHouse, with the benchmark
- `docs/architecture/09-import-platform.md` §12 — the fold, and why it is incremental
