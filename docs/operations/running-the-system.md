# Running the system

**Status:** the development setup on the laptop, every batch command, and how the tests behave. Moved
out of `README.md` on 2026-11-06, which keeps the short version. Running it as containers on a server is
[the deployment docs](deployment/).

## 1. Infrastructure

```bash
docker compose -f infra/docker-compose.yml up -d
```

ClickHouse on `localhost:18123`, PostgreSQL on `localhost:15432`. Source directories are
bind-mounted read-only, so a bug in a job cannot damage the originals.

## 2. Configuration

```bash
cp .env.example .env
```

Every variable the stack reads is listed there. Nothing is committed with a credential in it.

## 3. Schema

```bash
dotnet run --project backend/src/Sqm.Migrator -- --target postgres   --dir db/operational/migrations
dotnet run --project backend/src/Sqm.Migrator -- --target clickhouse --dir db/analytics/migrations
```

Forward-only. The runner refuses to start if an already-applied migration's checksum has changed —
an edited migration is a different migration, and the database it ran against no longer matches the
repository.

**The seeds are not migrations, and nothing applies them.** Once, on a new database:

```bash
docker exec -i sqm-clickhouse clickhouse-client --multiquery < db/analytics/seed/002_vendor_map_seed.sql
```

Without it the vendor map is empty and every vendor appears under each of its raw GSMA spellings —
Samsung as six vendors, as it did on the first server. Once only: the map is edited in the product
afterwards, and a second run would put the seed's rows back over those edits.

**When the migrator refuses.** Find out whether the edit changed the schema, not only the text.
Compare `SHOW CREATE TABLE` for every object the migration creates with the file. If they differ, the
fix is a new migration that brings the database to the file. If they are identical, the edit was
comments or layout, and a person with access to the database records the file's checksum as applied —
a deliberate, reviewed act, because it is the migration history that is being changed:

```sql
INSERT INTO sqm.schema_migration (version, name, checksum, applied_at, duration_ms)
VALUES ('<version>', '<name>', '<sha-256 of the file, CRLF normalised to LF>', now64(3), 0);
```

The table keeps the latest row per version, so the old checksum stays in its history.

Done once so far, on the development database: `019_device_module` was edited after it ran on
2026-09-15 (applied 18:25 UTC, committed 18:57 UTC). On 2026-09-30 the live `binding_by_imei`,
`mv_binding_by_imei` and `agg_device_model` were compared with the file and are identical — columns,
types, engine, sort key, partitioning and the view's query. On 2026-10-01, with the product owner's
authorisation, the file's checksum `e66cac95…` was recorded over the original `f4ee6c0f…`, and the
migrator then applied 020–022. A fresh environment runs the file as it stands and never meets this.

## 4. Grants, and the first administrator

```bash
psql -U sqm -d sqm -v app_password="a-strong-password" \
     -f db/operational/grants/003_least_privilege.sql
```

Creates the `sqm_app` role the application connects as. It holds INSERT and SELECT on the audit
tables and nothing else, which is what makes the audit trail append-only in fact rather than by
convention — `UPDATE` and `DELETE` on it are refused by PostgreSQL.

A schema added after that gets its own grants file, run once after the migration that creates it.
There are three so far; all are safe to re-run:

```bash
psql -U sqm -d sqm -f db/operational/grants/005_device_catalog.sql   # after migration 005
psql -U sqm -d sqm -f db/operational/grants/013_explorer.sql         # after migration 013
psql -U sqm -d sqm -f db/operational/grants/016_dashboards.sql       # after migration 016
```

Without them `sqm_app` has no privileges on the new schema at all.

```bash
SQM_BOOTSTRAP_PASSWORD='...' \
dotnet run --project backend/src/Sqm.Migrator -- \
  --create-admin --username admin --display-name "System Administrator"
```

Refuses to run if any active user can already manage users and roles. The password is read from
stdin or the environment, never from an argument — arguments appear in shell history and in the
process list. Everyone else is created from the Users page.

## 5. The application

```bash
dotnet run --project backend/src/Sqm.Api --launch-profile http   # API on :5202, import worker inside it
cd frontend && npm install && npm run dev                        # UI on :5173
```

**In development the import worker runs inside the API** (`Import:RunWorkerInProcess`, on by
default in Development). Do not also start `Sqm.Ingestion` as a worker: that makes two, and the
README used to say to. On 2026-09-27 two did run, and two late days were folded side by side. A
day of a source is now never claimed while another day of it is running, whichever worker holds
it, but one worker is what the design assumes and what its timings were measured on. To run the
worker as its own process, start the API with `Import__RunWorkerInProcess=false`:

```bash
dotnet run --project backend/src/Sqm.Ingestion  # the worker, only when the API does not host one
```

The worker checks the analytics schema at startup and refuses to run against the wrong partition
key, because day-level idempotency is implemented as a partition drop.

## 6. Batch operations

The worker doubles as the batch entry point, so the historical jobs and the daily import run the
same code rather than two implementations that can drift.

**Getting data in from a shell.** For what cannot go through the Import Center's upload: the
initial dump, and files that cannot cross the network in one piece. Each ends in the Import Center's
history like an upload, with its original in the file store.

```bash
cd backend/src/Sqm.Ingestion

# Load the operator's initial dump into an empty store - once per environment, before the first
# daily file - then its bindings in the history and its device models.
dotnet run -- --load-dump <dump_subs_device_sim_info_*.csv[.gz]>

# Record a dump loaded before --load-dump recorded it. Refuses unless the file holds exactly as
# many rows as binding_snapshot.
dotnet run -- --record-dump <dump_subs_device_sim_info_*.csv[.gz]>

# Hand a file already on this machine to the Import Center, as an upload would. A .gz is
# decompressed as it is stored; the queue and the processing are the upload's.
dotnet run -- --import-file <daily_subs_device_sim_info_YYYY-MM-DD.csv[.gz]> [--source SQM|TAC]

# "Import again" from the shell: queue a finished import's file as its day's next revision.
# Refuses when the original is no longer in the file store.
dotnet run -- --reprocess <job id>
```

**Rebuilding what is derived.** Each is resumable or skips work already done unless `--force`.

```bash
# Rebuild the per-day marts. Skips days already built; --force redoes them all.
dotnet run -- --refresh-marts [--from 2026-01-26] [--to 2026-06-14] [--force]

# Rebuild the dashboard marts for one delivery. Defaults to the newest.
dotnet run -- --refresh-dashboard [--seq 133] [--pause-merges]

# Check a delivery's marts are complete, not merely present.
dotnet run -- --verify-marts [--seq 133]

# Populate the IMSI- and IMEI-ordered copies of current state. Run once after migrations 018 and 019.
dotnet run -- --backfill-imsi [--truncate]
dotnet run -- --backfill-imei [--truncate]

# Fill the binding history (timelines). Run once after migration 022, while nothing imports.
# Resumable: months that already reconcile are skipped; --force rebuilds them all.
dotnet run -- --backfill-history [--from 2026-01-26] [--to 2026-09-26] [--force]

# Measure feed quality for days imported before it existed, or again after a new GSMA version.
dotnet run -- --refresh-quality [--from d] [--to d] [--force]

# Write each day's SIM changes for the risk pages. Run once after migration 023; the import keeps
# it current. --force rewrites days already written (needed after --refresh-quality --force).
dotnet run -- --refresh-risk-days [--from 2026-01-26] [--to 2026-09-26] [--force]

# Write the per-model day counts (new models, network age) and the dump's models. Run once after
# migration 025; --force again when the first GSMA version arrives on a store loaded without one.
dotnet run -- --refresh-tac-days [--from d] [--to d] [--force]

# Build the risk measures now (migration 024), instead of a chunk per idle moment of the worker.
# Stop the worker first, or the two build the same run. --force retries a failed run.
dotnet run -- --refresh-risk [--force]
```

**Risk signals are judged only once thresholds are set.** The worker keeps the measures current on
its own; the `Risk` configuration section holds the thresholds, and until one is set the risk pages
say "not calibrated" rather than judge on invented numbers. How each threshold is cut, and the curves
it is cut from, are in `docs/adr/ADR-014-risk-signals.md`.

**`--backfill-history` is what timelines wait for.** The history holds every binding's dated
events, one row per binding per month, by number, by SIM and by IMEI - so one entity's whole
timeline is a key read, 34 ms for a handset with 218 bindings where the event log took 65 s for
the most-shared one. Until every day of the event log is in it, the timeline API answers 503
rather than show a history with days missing. After that the daily import keeps it current: a new
day is added, a corrected or retried one rebuilds its month. See
`docs/adr/ADR-013-binding-history.md`.

**`--verify-marts` is the one worth knowing about.** The refresh writes its statements across
several marts, so a partly-failed run leaves every mart holding rows for the delivery while several
slices are missing. It checks every expected slice and reconciles the totals against the KPI
mart's active-binding count — because a mart that disagrees with the headline figure is worse
than a missing one, it will be believed.

The dashboard only ever reads a delivery listed in `sqm.mart_ready`, which the refresh writes
**after** every statement succeeds. During a rebuild it serves the previous complete delivery and
says so on the freshness card.

`--pause-merges` is for an undersized node. The mart statements still collide with a single merge
of the event log, which can hold 4 GiB. Merges are always resumed afterwards, including when the
job fails. Each statement's memory is capped by `ClickHouse:BoundedMemoryBytes` — 1.2 GB on the
laptop, 2.5 GB on the first server. See `docs/architecture/11-clickhouse-memory.md`.

**`--backfill-imsi` has to be run again after any rebuild of `binding_current` that is not an
INSERT.** The IMSI-ordered table is kept in step by a materialized view, and a materialized view
fires on INSERT and on nothing else — an `EXCHANGE TABLES` swap, which migration 015 used on the
event log, bypasses it entirely and leaves IMSI search describing the old data. The command
reconciles row counts and active counts against the source before reporting success, and exits
non-zero if they differ. That check earned itself on its first run: every chunk reported success
and the copy was short by five rows, because the chunk loop stopped at ten digits and five MSISDNs
in the data have twelve, thirteen and fifteen. See `docs/adr/ADR-008-imsi-search.md`.

**`--backfill-imei` is the same command for the other re-ordered copy**, and the same rule applies
to it. `sqm.binding_by_imei` is what makes a device model a contiguous range rather than a scan of
every binding, and it is kept in step by a materialized view with the same one weakness. The first
run copied 295,013,916 rows in 629 s with zero chunk failures, compacted 301 parts to 5, and
reconciled to the row. See `docs/adr/ADR-009-device-module.md`.

## 7. Tests

```bash
dotnet test backend/Sqm.slnx                   # 888: Domain 243, Ingestion 144, Application 105, Integration 396
cd frontend && npm test                        # 265, in 37 files
python tools/test_device_image_pipeline.py     # 46 checks, no network, no database
python tools/test_device_image_package.py      # 111 checks, no network, no database
```

Counted 2026-11-06. Integration tests run against a real PostgreSQL and ClickHouse and **skip with a
reason** when none is reachable, rather than failing — so a green run without the stack proves only
the tests that do not need it. They connect as `sqm_app`, not as the owner: connecting as the owner
would leave the append-only guarantee untested while appearing to pass. A system proxy that
intercepts `localhost` makes them skip too; `NO_PROXY=localhost,127.0.0.1` lets them through.

They have earned it repeatedly. Three bugs a mocked repository could not have found: Dapper cannot
bind `DateOnly` at all, `smallint` columns do not match `int` parameters, and Npgsql surfaces
`timestamptz` as `DateTime` where the models use `DateTimeOffset`. Three more from the identity
work: Dapper strips underscores when mapping to properties but not when matching a constructor,
Npgsql reports a `text[]` column as `System.Array`, and a non-nullable `int` is a *required* query
parameter to a minimal API.

## Reproducing the profiling

Requires Python 3.12+ and `duckdb`:

```bash
pip install duckdb
python tools/profiling/to_parquet_base.py
python tools/profiling/q.py tools/profiling/a1.py
```

Scripts read from `E:\job\Quadrasense\SQM` and `E:\job\Quadrasense\TAC` and write working Parquet
projections outside the repository, under `E:\job\Quadrasense\_sqm_discovery`.
