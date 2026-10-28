# QuadraSense

**PI — Primary SIM & Device Inventory.** Analytics platform for mobile device / SIM / subscriber
binding data, enriched with the GSMA TAC device database.

The four identifiers the name points at are MSISDN, IMSI, IMEI and ICCID/EID. The feed carries the
first three, and they are loaded and queryable. **ICCID is derived, not delivered:** the operator's
numbering builds it from the IMSI — `8998`, the IMSI's digits 4–7, `000`, its last eight — and every
IMSI in the feed has the MCC that lets the rule run backwards, so an ICCID can be searched as well as
shown. The UI and the exports label it derived. EID is not in the feed, and nothing here pretends
otherwise — see `docs/architecture/03-data-model.md` for what the source delivers and
`docs/architecture/18-explorer-modes.md` for the ICCID rule.

> **Status.** In use on the laptop against the full history, and deployed to its first server.
> Built: the import platform (upload, queue, worker, validation, GSMA TAC versioning, late days and
> revisions); identity (local sign-in, server-side sessions, permission-based RBAC, an append-only
> audit trail); the dashboard and its builder; the Explorer (look up a number, SIM, ICCID or handset,
> what it is bound to and its dated history, and a query builder); devices and their images; feed
> quality; risk signals. Every endpoint requires a permission, and a test enumerates the router to
> prove none escapes it.
>
> **Stage 1 of the deployment runs** on the department's VM as containers, with real data — the dump
> and the daily files to 2026-02-19. Stage 2 is MCI's OpenShift. See [Deploying](#deploying).

---

## What this system does

A mobile operator delivers a daily feed describing which **handset** each **SIM** and **phone
number** is currently bound to. This platform ingests that feed, enriches it with device metadata
from the GSMA TAC database, and answers:

- What is the active device population by manufacturer, model, OS and device type?
- How is it changing — which brands are growing, which are losing users, which models are new?
- Which subscribers changed device or swapped SIM, and how often?
- For one number, SIM or handset: what is it bound to now, what was it bound to, and when?
- Which SIMs and handsets show risk signals, measured against thresholds cut from the real data?
- How healthy is the incoming data, and how current is it?

## Scale (measured, not estimated)

On the laptop, which holds the whole history, 2026-10-26 - after re-importing the four days its bulk
load had cut short (02-03, 02-10, 02-15 and 03-24 had lost 27-61% of their rows, 14.6 million events
in all, removals among them). The last two came back after these figures were first measured that
morning: 7,219,972 more events since, and current state about 2 million active bindings lower:

| | |
|---|---:|
| Initial dump | **125,939,523** bindings (4.86 GiB, 2025-12-27 → 2026-01-25) |
| Daily change history | **1,961,277,736** events over **247 days** (2026-01-26 → 2026-10-10) |
| Days absent from the files received | **11** (7 in May, 3 in July, 1 in August) |
| Daily volume | 6.3M – 11.3M events a day (5th–95th percentile; median 7.7M) |
| Current state | **118,393,683** active bindings — 75.7M subscribers, 76.8M SIMs, 83.4M handsets (estimated) |
| Device reference data | 271,830 TACs (GSMA version v2026.10.04, activated 2026-10-05) |
| TAC enrichment coverage | **93.9%** of active bindings |

## The four things a newcomer should understand first

**1. The grain is a *binding*, not a subscriber.**
`(msisdn, imsi, imei)` — a phone number, a SIM and a handset. Zero duplicates across 125.9M rows,
so it is a genuine natural key. 51.3% of numbers were seen on more than one device, so "one row per
subscriber" is not a shape this data has. Every count in the product therefore states its unit:
Samsung is 52,704,438 **bindings**, 40,070,800 **subscribers** or 39,320,516 **handsets**, and all
three are correct answers to different questions.

**2. The feed has set semantics, and that is load-bearing.**
`add` and `remove` toggle a binding, and its final state depends only on its **last** event, not on
the path taken to it — verified over 8,062,257 transitions with **zero** repeated adds.

This is not a curiosity. It is what lets a daily import fold only its own day into current state
instead of replaying the whole event log. Measured: the full replay takes about **45 minutes**; one
day takes **seconds**. Without the proof, the 45-minute path would be the only correct one.

**3. The dates were recovered, and two earlier conclusions were wrong.**
The original 82 files carried no dates, which was the project's largest open risk. The source then
began supplying dated filenames, and all 82 old files matched dated ones by exact byte size with
**zero unmatched**. The inferred ordering turned out to be exactly right — but the *span* was wrong
(2026-01-26 → 2026-04-17, not 2026-02-23 → 2026-05-16), and the supposed gap did not exist. See
`docs/discovery/03-dated-daily-files.md`.

**4. Dashboards cannot be served from raw data, by either engine.**
The Phase 0 benchmark measured 3.5 s for a dashboard-shaped query against a 500 ms budget. The mart
layer is therefore **load-bearing architecture, not an optimisation** — see ADR-003.

## Running it

This section is the development setup on the laptop. Running it as containers on a server is
[Deploying](#deploying).

### 1. Infrastructure

```bash
docker compose -f infra/docker-compose.yml up -d
```

ClickHouse on `localhost:18123`, PostgreSQL on `localhost:15432`. Source directories are
bind-mounted read-only, so a bug in a job cannot damage the originals.

### 2. Configuration

```bash
cp .env.example .env
```

Every variable the stack reads is listed there. Nothing is committed with a credential in it.

### 3. Schema

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

### 4. Grants, and the first administrator

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

### 5. The application

```bash
dotnet run --project backend/src/Sqm.Api --launch-profile http   # API on :5202, import worker inside it
cd frontend && npm install && npm run dev                        # UI on :5173
```

**In development the import worker runs inside the API** (`Import:RunWorkerInProcess`, on by
default in Development). Do not also start `Sqm.Ingestion` as a worker: that makes two, and this
README used to say to. On 2026-09-27 two did run, and two late days were folded side by side. A
day of a source is now never claimed while another day of it is running, whichever worker holds
it, but one worker is what the design assumes and what its timings were measured on. To run the
worker as its own process, start the API with `Import__RunWorkerInProcess=false`:

```bash
dotnet run --project backend/src/Sqm.Ingestion  # the worker, only when the API does not host one
```

The worker checks the analytics schema at startup and refuses to run against the wrong partition
key, because day-level idempotency is implemented as a partition drop.

### 6. Batch operations

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

### 7. Tests

```bash
dotnet test backend/Sqm.slnx                   # 826: Domain 243, Ingestion 137, Application 105, Integration 341
cd frontend && npm test                        # 230, in 33 files
python tools/test_device_image_pipeline.py     # 46 checks, no network, no database
python tools/test_device_image_package.py      # 111 checks, no network, no database
```

Counted 2026-10-28. Integration tests run against a real PostgreSQL and ClickHouse and **skip with a
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

## Deploying

The system ships as containers (`deploy/compose/compose.yaml`, ADR-007) that run the way OpenShift
runs them: an arbitrary non-root UID in group 0, a read-only root filesystem, no capabilities, no
port below 1024. ClickHouse, PostgreSQL, the API, the worker on its own and the web front (the SPA in
unprivileged nginx), with the migrations and grants as jobs that run on every start and do nothing
when nothing changed.

| | Where | State |
|---|---|---|
| **Stage 1** | the department's VM, everything under `/opt/quadrasense` | running since 2026-10-24: the dump, GSMA version v2026.10.04, the daily files to 2026-02-19 |
| **Stage 2** | MCI's OpenShift — dev, uat, prd | planned |

On the server, a deployment is one command. It pulls the checkout's branch from GitHub, builds the
images there, tagged with the commit, and restarts what changed; a failed migration leaves the
running API and worker in place.

```bash
/opt/quadrasense/repo/deploy/compose/update.sh
```

`docs/operations/deployment/` has the rest: the images and the rules that keep them OpenShift-ready
(`01`), the VM's layout, first start, loading the data, carrying daily files over a VPN that drops,
and every session on it (`02`), the OpenShift plan (`03`), MCI's security red lines row by row
(`04`), and the department's GitLab pipeline — test, build with kaniko, deploy — with what it still
needs (`05`). The MCI documents behind `03` and `04` are internal; this repository cites them by
section and copies nothing identifying out of them.

### How a change reaches a server

1. **On a branch, through a pull request.** Work is done on a branch, merged into `main` on GitHub
   through a pull request, and the branch deleted: `main` is the only branch that lasts.
2. **The department's GitLab carries the same `main`.** Pushed over HTTPS from the development
   machine after each merge — the red lines let developers push and servers only pull, so no
   server pushes to another (`04`, §2-2).
3. **Servers pull.** `update.sh` on the VM today; the GitLab pipeline's deploy job when its runners
   and variables are in place (`05`, §8).

## Repository layout

```
docs/
  discovery/       Phase 0 - what the data actually is, measured
    01-data-profiling-report.md    full profiling results
    02-open-questions.md           questions + documented assumptions
    03-dated-daily-files.md        how the date risk was closed
    04-07                          handset changes from 2026-09-15, shifted and copied IMEIs, data-quality examples
  architecture/    how the system is built
    01-overview.md                 requirements, diagrams, risk register
    02-glossary.md                 binding, fold, mart, delivery - the vocabulary the code uses
    03-data-model.md               entities, tables, quality rules
    04-security-model.md           roles, controls, audit
    05-roadmap.md                  the phases and what each delivers
    07-testing-strategy.md         what is tested and why
    08-observability.md            how the system says whether an import did the right thing
    09-import-platform.md          the import platform, design and build
    11-clickhouse-memory.md        container memory: what went wrong and what the settings mean
    12-identity-and-access.md      how auth, RBAC and audit are built, and how to operate them
    13-dashboard-review.md         every dashboard figure checked against the store, and what was wrong
    14-relationship-explorer.md    number / SIM / handset links, and which one of them is inferred
    15-feed-quality.md             what the feed gets wrong, measured per day
    16-ui-design-system.md         tokens, components, and the conventions the layout review settled
    17-dashboard-builder.md        dashboards people build from widgets
    18-explorer-modes.md           the Explorer's Simple and Advanced modes, and the derived ICCID
  operations/
    dedicated-hardware.md          every setting chosen because of the laptop, to revisit on real hardware
    deployment/                    the containers, the department VM, MCI's OpenShift, red lines, CI/CD
  adr/             Architecture Decision Records
  research/        feature research
  backlog.md       suggestions not built yet, with their evidence and cost
backend/
  src/Sqm.Domain          the binding fold and identifier rules
  src/Sqm.Application     abstractions, query shapes, import contracts
  src/Sqm.Infrastructure  ClickHouse and PostgreSQL implementations
  src/Sqm.Api            minimal-API endpoints
  src/Sqm.Ingestion      the import worker, and the batch commands
  src/Sqm.Migrator       forward-only schema migrator for both stores
frontend/          Vue 3 SPA: dashboards, Import Center, Explorer, devices, risk, feed quality, users, roles, audit
db/
  analytics/       ClickHouse migrations, batch jobs and seeds
  operational/
    migrations/    PostgreSQL migrations
    grants/        the least-privilege role, and what makes the audit log append-only
    jobs/          the backfill of the pre-platform load
infra/             the development environment as a compose file
deploy/
  images/          the Dockerfiles: api, worker, migrator (one build) and web (SPA + nginx)
  compose/         the whole system as containers, run the way OpenShift runs them (ADR-007), and update.sh
  host/            one-time server setup: the deploy runner, Docker's data root
  ci/              the kaniko build the GitLab pipeline runs
.gitlab-ci.yml     the department's pipeline: test, build, deploy
tools/
  source_device_images.py        proposes device images for review; never writes a live image
  import_device_image_package.py stages a curated image package as review candidates
  device_image_sources.json      the source allowlist, and why no manufacturer source is in it
  test_device_image_*.py         checks on identity, rejection, scoring, normalisation and packages
  repair_tac_transfer.py         rebuilds a TAC export damaged in transit, refusing if lossy
  quality_examples.py            the data-quality examples in docs/discovery/07
  cloud/setup.sh                 installs the pinned .NET SDK in a cloud session
  ops/                           laptop upkeep: shrinking Docker's data disk
  profiling/       reproducible data-profiling scripts (DuckDB)
  benchmark/       storage-engine benchmark harness
```

## Architecture decisions

| ADR | Decision | Status |
|---|---|---|
| [ADR-001](docs/adr/ADR-001-backend-technology.md) | Backend: **.NET 10 / C#** | Accepted |
| [ADR-002](docs/adr/ADR-002-operational-database.md) | Operational store: **PostgreSQL 17** | Proposed |
| [ADR-003](docs/adr/ADR-003-analytics-store.md) | Analytics store: **ClickHouse** — 26–35× on dashboard shapes, 3.4× smaller | Accepted |
| [ADR-004](docs/adr/ADR-004-ingestion-strategy.md) | Ingestion: immutable event log, SHA-256 idempotency, TAC joined at query time | Accepted |
| [ADR-005](docs/adr/ADR-005-frontend-architecture.md) | Frontend: **Vue 3** + Vite + Tailwind + headless primitives | Accepted |
| [ADR-006](docs/adr/ADR-006-authentication-and-access-control.md) | Auth: **local accounts, server-side sessions, permission-based RBAC** | Accepted |
| [ADR-007](docs/adr/ADR-007-deployment.md) | Deployment: **containers that run as OpenShift runs them** (non-root, any UID, read-only); the worker on its own; compose on the VM, then OpenShift | Proposed; stage 1 running |
| [ADR-008](docs/adr/ADR-008-imsi-search.md) | IMSI search: a second table ordered by IMSI, and a skip index on the event log | Accepted |
| [ADR-009](docs/adr/ADR-009-device-module.md) | Devices: a model is a TAC, and an IMEI-ordered table makes it a range | Accepted |
| [ADR-010](docs/adr/ADR-010-device-images.md) | Device images: keyed by model, sourced from Commons under identity guards | Amended by ADR-011 |
| [ADR-011](docs/adr/ADR-011-device-image-sourcing.md) | Device images are proposed, scored from the pixels and reviewed - never auto-applied | Accepted |
| [ADR-012](docs/adr/ADR-012-explorer-query-engine.md) | Explorer queries: an allow-listed model compiled to SQL, planned, and run within a budget of rows and time | Accepted |
| [ADR-013](docs/adr/ADR-013-binding-history.md) | Timelines: a binding history, one row per binding per month, by number, SIM and IMEI | Accepted |
| [ADR-014](docs/adr/ADR-014-risk-signals.md) | Risk signals: levels, feed defects, SIM change; measures precomputed, verdicts judged when read, thresholds cut from the data | Accepted |
| [ADR-015](docs/adr/ADR-015-new-models-network-age-data-quality.md) | New models, network age and data-quality signals, from a per-model day table | Accepted |
| [ADR-016](docs/adr/ADR-016-dashboard-builder.md) | The dashboard builder: one JSONB definition per dashboard; each widget reads an endpoint that already exists, under its permission | Accepted |
| [ADR-017](docs/adr/ADR-017-filtered-dashboard-views.md) | Filtered dashboard views read per-model population marts, and abandoned reads are killed | Accepted |
| [ADR-018](docs/adr/ADR-018-resumable-uploads.md) | Uploads resume after a broken connection, with the tus protocol | Accepted |
| [ADR-019](docs/adr/ADR-019-planning-snapshot.md) | Network planning: each number classed once per delivery by an idle-time snapshot, from parsed radio profiles | Accepted |

Every one of these was decided against measurement on the real dataset, not on reputation. Where a
claim appears in these documents, the number behind it is there too.

## Reproducing the profiling

Requires Python 3.12+ and `duckdb`:

```bash
pip install duckdb
python tools/profiling/to_parquet_base.py
python tools/profiling/q.py tools/profiling/a1.py
```

Scripts read from `E:\job\QuadraSense\SQM` and `E:\job\QuadraSense\TAC` and write working Parquet
projections outside the repository, under `E:\job\QuadraSense\_sqm_discovery`.
