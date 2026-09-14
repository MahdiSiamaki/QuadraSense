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

## 6. The last piece: merges were the whole story

After the caches were sized, the limits were actually applied and `max_threads` was lowered,
batch jobs *still* lost the occasional statement to the `OvercommitTracker` — reporting 5.20 GiB
against an RSS of 1.3 GiB. That gap was left unexplained here for a while. It has an answer.

Sampling the server every four seconds during a run showed nothing wrong:

| tracked | RSS | merges | queries | query memory |
|---:|---:|---:|---:|---:|
| 790 MiB | 726 MiB | 0 | 2 | 94 MiB |
| 895 MiB | 757 MiB | 0 | 2 | 167 MiB |

And `system.query_log` confirmed it from the other direction: **no query in a full hour exceeded
500 MiB**, while `QueryMemoryLimitExceeded` counted in the thousands.

A query that never grows cannot exhaust a 5.2 GiB ceiling. Something else was reaching it
briefly enough to fall between four-second samples — and the earlier probe had already caught it
once, holding **3.99 GiB in a single merge**:

```
tracked   RSS      merges  merge_mem  queries  query_mem
4.41 GiB  1.24 GiB      1   3.99 GiB        2   509 MiB
```

A merge of the 25 GiB event log takes gigabytes for a few seconds. It pushes the server total to
the ceiling, the `OvercommitTracker` then has to stop *something*, and it picks the running
query — which is a 90 MiB mart aggregate that did nothing wrong. The error names the victim, not
the cause, which is why this took so long to see.

With `--pause-merges`, the same job ran **9 statements with 0 failures** where a third had been
failing before.

So the ordering of the whole investigation, from cause to symptom:

1. Cache ceilings of 18 GiB on a 6 GiB container inflated the tracker → non-deterministic kills.
2. The ratio workaround removed self-limiting entirely → thrashing under merge load.
3. Per-query limits silently not applied → aggregates ran unbounded.
4. `max_threads` multiplying hash-table memory → 1.9 GiB where 565 MiB would do.
5. **Merges spiking to 4 GiB** → the tracker hits the ceiling and an innocent query is stopped.

Only the last one survives all the other fixes, and it is the one that needs an operational
answer rather than a configuration one: on a node this size, a heavy batch job and a merge of a
25 GiB table cannot both run. On a production node with 32–64 GiB they can, and
`--pause-merges` should never be needed.

The batch jobs are still built to converge — every day and every mart statement is idempotent,
failures are reported with the exact range to re-run, and repeating a pass finishes the
stragglers. That safety net stays regardless, because it costs nothing and the alternative to
having it is finding out you needed it.

## Related

- `infra/clickhouse/memory.xml` — the configuration, with the same history in comments
- `docs/adr/ADR-003-analytics-store.md` — why ClickHouse, with the benchmark
- `docs/architecture/09-import-platform.md` §12 — the fold, and why it is incremental
