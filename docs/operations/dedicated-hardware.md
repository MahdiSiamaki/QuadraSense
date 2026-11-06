# Moving to dedicated hardware — every compromise the laptop forced

**Status:** living document, started 2026-10-20 at the product owner's request. Quadrasense runs on
the owner's laptop today and will move to dedicated hardware. Every setting, workaround or design
choice made *because of the laptop* is listed here, with what to do on the new machine and how to
check it. **Any change made for the laptop adds its row here in the same commit.**

**Read with:** `docs/architecture/11-clickhouse-memory.md` (why each memory setting is what it is),
`infra/clickhouse/memory.xml`, `infra/docker-compose.yml`, ADR-017 (marts that exist partly because
of the merge cap).

---

## The laptop today

| | Value | Source |
|---|---|---|
| Host RAM | 15.7 GiB | `11-clickhouse-memory.md` |
| Docker Desktop's WSL VM | 7.8 GiB | same |
| ClickHouse container | 6 GiB; server ceiling 5.2 GiB | `memory.xml` |
| Docker data (ClickHouse + PostgreSQL) | `D:` - a partition of the internal NVMe, 282 GiB, 265 GiB used on 2026-11-05 after compaction and a reprocessed day | owner's setup |
| Operator files and uploaded originals | `E:` - an external **USB hard disk**, removable | `docker-compose.yml`, `ImportStorage:RootPath` |
| Data | ~1.05 billion events; `binding_current` 817 million row versions for 120 million bindings (2026-10-19) | ADR-017 |
| Import worker | inside the API process (Development default) | `Program.cs`, `Import:RunWorkerInProcess` |

Every performance figure in `docs/` was measured on this machine. They are evidence for the
decisions taken here, not targets for the new one: **re-measure after the move**.

## The department VM is not yet the dedicated hardware (2026-10-22)

The first move is to a department VM (`docs/operations/deployment/02-department-vm.md`): 8 vCPU,
15 GiB, 200 GB. It gives ClickHouse **8 GiB against the laptop's 6** - a third more, not the several
times more that sections 1-4 below assume. So on the VM:

| Row | On the VM | Why |
|---|---|---|
| §1 server ceiling | 80% of the container, read from the cgroup (`deploy/compose/clickhouse/memory.xml`) - 6.4 GiB; caches 768 / 256 / 128 MiB | ClickHouse 25.8 reads the container's limit: measured 2026-10-22, a 2 GiB container logs "Available RAM: 2.00 GiB". The ceiling follows `CLICKHOUSE_MEMORY` with no second number to change |
| §2 merge cap, pool | **unchanged**: 2 GiB, pool 4 | a third more memory does not pay for a 4 GiB merge beside the queries; raise it only on hardware that has the RAM, one step at a time |
| §3, §4 per-statement limits | unchanged | same |
| §5 storage | **worse than the laptop**: 200 GB in all, 41.9 GB on `/var` where Docker keeps volumes by default, against 182.5 GiB of ClickHouse and 40.6 GiB of originals today | a 1 TB data disk is the first request to the department (`02-…` §1) |
| §6 runtime | Linux, Docker Engine, no WSL cap; the worker in its own container (ADR-007); separate secrets per ClickHouse user (`deploy/compose/clickhouse/users.xml`); standard internal ports | closed on the VM by `deploy/compose/` |
| §6 system logs | the same five off; **TTL of 30 days on `query_log` and `part_log`** (`deploy/compose/clickhouse/system-logs.xml`) | a server that runs for years would otherwise keep every query |

The acceptance checks of `02-…` §7 are the measurements to record here once it runs.

## What to change

### 1. ClickHouse server memory and caches

| Laptop | Why | On dedicated hardware | Check |
|---|---|---|---|
| `max_server_memory_usage` 5.2 GiB; `mark_cache_size` 512 MiB, `index_mark_cache_size` 256 MiB, `uncompressed_cache_size` 128 MiB (`memory.xml`) | ClickHouse's defaults (18 GiB of cache ceilings) inflated the memory tracker on a 6 GiB container and queries were killed at random (`11-…` §1–2) | Scale with the container: caches at roughly 10–15% of its memory, the server ceiling at 70–80% (`11-…` §5) | `SELECT metric, formatReadableSize(value) FROM system.asynchronous_metrics WHERE metric LIKE '%Memory%'` against RSS: close to each other |

### 2. Merge size and the background pool - the largest single change

| Laptop | Why | On dedicated hardware | Check |
|---|---|---|---|
| `max_bytes_to_merge_at_max_space_in_pool` **2 GiB**; `background_pool_size` 4, concurrency ratio 1 (`memory.xml`) | A single merge of a multi-GiB part held ~4 GiB; at startup the server merged itself out of memory three times in four minutes (`11-…` §6–7) | Raise the cap with the RAM (the default is 150 GiB) and the pool with the cores | `SELECT count() FROM system.parts WHERE table = 'binding_current' AND active` falls from 18; `count()` of active bindings with `FINAL` falls from 74 s |

**What it costs today.** Large tables stop merging at 2 GiB parts, so `binding_current` holds 6.8
versions per binding and every `FINAL` read collapses them: 74 s for a count, 100 s for a filtered
KPI row, and the daily mart refresh pays it in its one read (316 s of 499 s for delivery 247; until
2026-11-05 it paid it 14 times, 63 minutes for delivery 246). ADR-017
rejected raising the cap or running `OPTIMIZE … FINAL` **only** because this machine cannot afford
the merge. On dedicated hardware:

1. raise the cap and let the backlog merge (watch `system.merges`);
2. re-measure `FINAL` on `binding_current`;
3. then decide whether the population marts of ADR-017 are still needed for speed (they also give
   one estimator and one served delivery to every filtered view - keep them unless they cost more
   than they save).

### 3. Per-statement limits of the import and the mart refresh

| Laptop | Why | On dedicated hardware | Check |
|---|---|---|---|
| Day-level folds and the refresh: `max_threads` 1, `max_memory_usage` 1.2 GiB, spill at 300 MB (`ClickHouseIngestionStore.cs`, the bounded execution); day import `max_memory_usage` 3 GB | `max_threads` multiplies hash-table memory - 1.9 GiB where 565 MiB would do (`11-…` §4) | More threads and memory, kept low enough that two jobs and the background merges still fit together. The memory cap is a setting since 2026-10-25, `ClickHouse:BoundedMemoryBytes` (`SQM_CH_BOUNDED_MEMORY_BYTES` in compose): the department VM runs 2.5 GB, because two section-1c statements need more than 1.2 there (next row but one) | Refresh duration and peak per statement from `system.query_log` |
| `agg_tac_population` written in 8,192-row blocks (`refresh_marts.sql` §1b) | Its uniq() states held at once failed twice at the 1.2 GiB limit (ADR-017) | Can return to the default block size once the per-query limit is several GiB | The 1b statement's `memory_usage` in `query_log` |
| Section 1c peaked at 873 MiB - 73% of the 1.2 GiB limit (ADR-017) | the limit | **Rewritten 2026-10-25; peaks at 507-583 MiB on the laptop, 628-702 MiB on the VM.** It joined the per-model states to `sqm.tac`, and ClickHouse 25.8's planner swapped that join on the VM's data - the states became the hash table: 1.18-1.42 GiB on the dump-only delivery, 2.96-3.41 GiB after five days, failing every rebuild. (Corrected: this row first said the join's sides made no difference - swapping off did bring it to 1.16 GiB, which still failed under the 1.12 GiB limit.) Each model's value is now looked up by TAC and no join carries states; every statement's output identical on both machines' data | same |
| `agg_tac_population` merged by the refresh, and by table settings: `merge_max_block_size` 1024 and vertical merges (migration 036, `11-clickhouse-memory.md` §8) | Reads of a partition in several parts passed the API's 476 MiB cap, and a default merge of 972 MiB of states took 3.5 GiB | With memory to spare the settings can go, and the refresh's merge may not pay for itself: re-measure the reads without it | `system.part_log` `peak_memory_usage`; `memory_usage` of the reads |
| The whole refresh: 499 s for delivery 247, 316 s of it the one read of `binding_current` (1b) at one thread and 39 s the merge. It took 50-68 minutes before 2026-11-05 (`docs/research/2026-11-worker-capacity.md`) | one thread | A second thread took the same read from 315 s to 144 s on the laptop's disk, for 2.4 GiB, and did little on the VM's: re-measure on the real disk | `MartRefresh` timeline |
| Planning lifecycle (N4): each number's spells and successors computed in blocks of 1,024 numbers, and the spells written to a table of their own first (`ClickHouseIngestionStore.Planning.cs`, `SpellEventsSql`) | Nested lambdas copy the arrays they capture per element: 1.2 GiB in default blocks against the 1.12 GiB statement limit, 92 MiB in blocks of 1,024; grouping by (number, IMEI) and by number in one statement held every number's arrays (1.12 GiB) - measured on one real chunk, 2026-10-27 | Larger blocks once the per-statement limit is several GiB; the separate spell table stays - it is what lets the grouping stream | The lifecycle chunk's `memory_usage` in `system.query_log` |

### 4. Limits on the API's own queries

| Laptop | On dedicated hardware |
|---|---|
| Explorer: `QueryThreads` 2, `QueryMemoryBytes` 1.5 GB, `MaxConcurrentQueries` 4, `BudgetRows` 100 M, `BudgetSeconds` 30 (`ExplorerOptions`) | Raise with cores and RAM; the budget then admits wider questions |
| Lookups and searches: `max_threads` 1, `max_memory_usage` 500 MB (`ClickHouseJsonQuery`) | Usually fine as they are - point reads; raise only if measured |
| Planning's per-model views (bands, satellite, IoT) merge uniq() states under that same 500 MB: 380-434 MiB at their peak, measured 2026-10-27, so each statement looks up profiles instead of joining and reads by the mart's key (`19-network-planning.md` §5) | More memory per query - or give these reads a budget of their own - before the catalogue or the network grows past that margin |
| API's ClickHouse client timeout 180 s (`Program.cs`); `QueryTimeoutSeconds` 120 | Lower, once `FINAL` is fast: a long wait is a symptom, not a need |

### 5. Storage

| Laptop | Why | On dedicated hardware |
|---|---|---|
| Uploaded originals and the operator's source files on a removable USB **hard disk** (`E:`) | `D:` could not hold the Docker disk and the files; ClickHouse on `E:` was measured unusable (system.parts query 117 s against 0.08 s) | Durable, always-mounted storage for originals, with backup. Retention is full history (~144 GB/year, CLAUDE.md), so size for years |
| The API starts with `E:` unplugged: the worker holds, an upload is told the store cannot be reached, and event 3101 is logged. Until 2026-11-06 it refused to start, and took the dashboards with it | removable drive | Moot on fixed storage, but a wrong path no longer stops the start: a deployment should read that warning |
| The queue holds while `E:` is away (`ImportWorker`, `IsReachableAsync`), because a missing directory used to be classified as "the original is gone" and fail the day for good. 2026-10-26: the laptop's power source changed at 11:15 UTC, `E:` vanished, and import 2478 (03-24) failed at 11:17 with `DirectoryNotFoundException`; the file was intact once the disk was back, and `--reprocess` queued it again as 2479. Sleep then took Docker down mid-rebuild of the dashboard (22 of 31 statements). **Again on 2026-11-03:** `E:` left at 12:08:14 UTC, the power source changed 12 s later and the laptop went to sleep 20 s after that, which reads as being unplugged to move it, not as a disk fault. It woke at 12:39 without the disk, import 3573 (10-24) failed at 12:55 and the days behind it waited, and the battery ran out at about 14:06 with the dashboard rebuild unfinished. `--reprocess` queued 3573 again as 3587 once the disk was back, and all seven days landed on 2026-11-05 | removable drive, a laptop that sleeps | Harmless on fixed storage: it never fires, and a file missing from a reachable store still fails for good. Built 2026-11-05 at the owner's request, replacing the 2026-10-26 decision to keep the classification and `--reprocess` by hand (`09-import-platform.md` D15). The laptop still wants power while imports run: the disk leaving no longer fails a day, but sleep still takes Docker down |
| Docker data in a WSL `.vhdx` on `D:`, compacted by hand (`tools/ops/compact-docker-disk.ps1`). On 2026-11-05 `D:` had 15 GiB free, with the disk file at 249.9 GiB and 227 GiB in use inside it, so a compaction can return up to about 23 GiB; the data grows about 0.8 GiB a day. Compacted that day: 256.6 to 234.1 GiB in 16 minutes, 30.8 GiB free. `fstrim` alone took 2 s and does not shrink the file, but it stops it growing: under a risk and planning run `D:` was losing 5.6 GiB an hour, and after one trim it lost nothing in the next 90 minutes. Reprocessing one old day had taken 13 GiB back within the hour. ClickHouse's own free-space figure (730 GiB) is the virtual disk's view, not the host's | Docker Desktop on Windows | A Linux host with Docker Engine (or native services) and the data on its own volume; no compaction chore |

### 6. The runtime around it

| Laptop | On dedicated hardware |
|---|---|
| Docker Desktop on Windows, inside a WSL VM capped at 7.8 GiB; stale-socket start failures | Linux host; no VM cap |
| The import worker runs inside the API process (`Import:RunWorkerInProcess`, on in Development) | Separate worker process, restarted and sized on its own - **decided in ADR-007** (2026-10-22): the `worker` container, one replica |
| PostgreSQL on port 15432 because another project holds 5432; ClickHouse HTTP on 18123 | Standard internal ports, TLS between services |
| `sqm_app` and `sqm_ingest` share one development password (`sqm-app-user.xml`) | Separate secrets, from the environment - the file already says so |
| System-log TTLs and flush settings tuned to stop thousands of tiny parts (`system-logs.xml`, `11-…` §7) | Keep, re-tune retention to the disk |
| Idle work in at most two lanes (`ImportWorker:MaxIdleLanes`), the only number measured, on this machine (ADR-021) | The formula follows the memory and the cores by itself. Raise the cap against measurement, and consider several chunks of one run at once |

### 7. Stalls on the laptop's Docker port

Two full integration runs on 2026-11-06 lost 4 and 1 tests, at 30 to 49 s each. The one reason captured is
`connection forcibly closed`: its request never reached `query_log` for 30 s while ClickHouse answered from
inside the container, and the server's `http_receive_timeout` is 30 s. So the wait looks like Docker Desktop's
port forwarding on Windows, not the code or the database. A third run was green, and each test passed alone.
Both failed runs had a sampler calling `wsl.exe` once a minute beside them, and neither green run did, which
is a correlation and proves nothing: the stall that was examined did not start on a tick.

**On the new machine:** the containers talk on their own network, so this should not exist. Until then, run
the suite with nothing beside it, and read a failure of 30 s or more here before reading it as a fault.

## Order of work on the new machine

1. Provision; restore the two data volumes and the originals; confirm row counts against the old
   machine.
2. Scale `memory.xml` (sections 1 and 2) to the new container; restart; let merges run.
3. Re-measure `FINAL` on `binding_current` and the filtered KPI's raw path.
4. Raise the per-statement limits (sections 3 and 4) one at a time, measuring each.
5. Run a full mart refresh and record its duration.
6. Run `dotnet test backend/Sqm.slnx` with the stack up - the real-data tests carry the timing
   bounds - and `tools/benchmark`.
7. Update this document: what changed, what was measured, which rows are closed.
