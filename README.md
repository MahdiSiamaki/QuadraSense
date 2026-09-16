# QuadraSense

**PI — Primary SIM & Device Inventory.** Analytics platform for mobile device / SIM / subscriber
binding data, enriched with the GSMA TAC device database.

The four identifiers the name points at are MSISDN, IMSI, IMEI and ICCID/EID. Three of them are
loaded and queryable today; ICCID and EID are not yet in the feed, and nothing here pretends
otherwise — see `docs/architecture/03-data-model.md` for what the source actually delivers.

> **Status: Phase 3–4.** The data platform is loaded and the application runs against it. The
> import platform — upload, queue, worker, validation, TAC versioning — is built and tested, and
> so is identity: local sign-in, server-side sessions, permission-based RBAC and an append-only
> audit trail. Every endpoint requires a permission, and a test enumerates the router to prove
> none escapes it.

---

## What this system does

A mobile operator delivers a daily feed describing which **handset** each **SIM** and **phone
number** is currently bound to. This platform ingests that feed, enriches it with device metadata
from the GSMA TAC database, and answers:

- What is the active device population by manufacturer, model, OS and device type?
- How is it changing — which brands are growing, which are losing users?
- Which subscribers changed device or swapped SIM, and how often?
- How healthy is the incoming data, and how current is it?

## Scale (measured, not estimated)

| | |
|---|---:|
| Initial dump | **125,939,523** bindings (4.86 GiB, 2025-12-27 → 2026-01-25) |
| Daily change history | **1,051,743,000** events over **133 days** (2026-01-26 → 2026-06-14) |
| Days expected but never delivered | **7** (all within one window in May) |
| Daily volume | ~7.4M – 9.6M events/day |
| Device reference data | 270,885 TACs (version 2, activated 2026-09-16) |
| TAC enrichment coverage | **92.8%** of active bindings |

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

### 4. Grants, and the first administrator

```bash
psql -U sqm -d sqm -v app_password="a-strong-password" \
     -f db/operational/grants/003_least_privilege.sql
```

Creates the `sqm_app` role the application connects as. It holds INSERT and SELECT on the audit
tables and nothing else, which is what makes the audit trail append-only in fact rather than by
convention — `UPDATE` and `DELETE` on it are refused by PostgreSQL.

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
dotnet run --project backend/src/Sqm.Api        # API on :8080
dotnet run --project backend/src/Sqm.Ingestion  # import worker
cd frontend && npm install && npm run dev       # UI on :5173
```

The worker checks the analytics schema at startup and refuses to run against the wrong partition
key, because day-level idempotency is implemented as a partition drop.

### 6. Batch operations

The worker doubles as the batch entry point, so the historical jobs and the daily import run the
same code rather than two implementations that can drift.

```bash
cd backend/src/Sqm.Ingestion

# Rebuild the per-day marts. Skips days already built; --force redoes them all.
dotnet run -- --refresh-marts [--from 2026-01-26] [--to 2026-06-14] [--force]

# Rebuild the dashboard marts for one delivery. Defaults to the newest.
dotnet run -- --refresh-dashboard [--seq 133] [--pause-merges]

# Check a delivery's marts are complete, not merely present.
dotnet run -- --verify-marts [--seq 133]

# Populate the IMSI-ordered copy of current state. Run once after migration 018.
dotnet run -- --backfill-imsi [--truncate]
```

**`--verify-marts` is the one worth knowing about.** The refresh writes fifteen INSERTs across
six marts, so a partly-failed run leaves every mart holding rows for the delivery while several
slices are missing. It checks every expected slice and reconciles the totals against the KPI
mart's active-binding count — because a mart that disagrees with the headline figure is worse
than a missing one, it will be believed.

The dashboard only ever reads a delivery listed in `sqm.mart_ready`, which the refresh writes
**after** every statement succeeds. During a rebuild it serves the previous complete delivery and
says so on the freshness card.

`--pause-merges` is for an undersized node. The mart statements use 34–73 MiB each and still
collide with a single merge of the 25 GiB event log, which can hold 4 GiB. Merges are always
resumed afterwards, including when the job fails. See
`docs/architecture/11-clickhouse-memory.md`.

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
295 million rows, and it is kept in step by a materialized view with the same one weakness. The
first run copied 295,013,916 rows in 629 s with zero chunk failures, compacted 301 parts to 5, and
reconciled to the row. See `docs/adr/ADR-009-device-module.md`.

### 7. Tests

```bash
dotnet test backend/Sqm.slnx
```

205 tests. Integration tests run against a real PostgreSQL and **skip with a reason** when none is
reachable, rather than failing. They connect as `sqm_app`, not as the owner: connecting as the
owner would leave the append-only guarantee untested while appearing to pass.

They have earned it repeatedly. Three bugs a mocked repository could not have found: Dapper cannot
bind `DateOnly` at all, `smallint` columns do not match `int` parameters, and Npgsql surfaces
`timestamptz` as `DateTime` where the models use `DateTimeOffset`. Three more from the identity
work: Dapper strips underscores when mapping to properties but not when matching a constructor,
Npgsql reports a `text[]` column as `System.Array`, and a non-nullable `int` is a *required* query
parameter to a minimal API.

## Repository layout

```
docs/
  discovery/       Phase 0 — what the data actually is, measured
    01-data-profiling-report.md    full profiling results
    02-open-questions.md           questions + documented assumptions
    03-dated-daily-files.md        how the date risk was closed
  architecture/    Phase 1 — how the system is built
    01-overview.md                 requirements, diagrams, risk register
    02-glossary.md                 binding, fold, mart, delivery - the vocabulary the code uses
    03-data-model.md               entities, tables, quality rules
    04-security-model.md           roles, controls, audit
    07-testing-strategy.md         what is tested and why
    09-import-platform.md          the import platform, design and build
    11-clickhouse-memory.md        container memory: what went wrong and what the settings mean
    12-identity-and-access.md      how auth, RBAC and audit are built, and how to operate them
    13-dashboard-review.md         every dashboard figure checked against the store, and what was wrong
  adr/             Architecture Decision Records
backend/
  src/Sqm.Domain          the binding fold and identifier rules
  src/Sqm.Application     abstractions, query shapes, import contracts
  src/Sqm.Infrastructure  ClickHouse and PostgreSQL implementations
  src/Sqm.Api            minimal-API endpoints
  src/Sqm.Ingestion      the import worker
  src/Sqm.Migrator       forward-only schema migrator for both stores
frontend/          Vue 3 SPA: dashboard, Import Center, lookup, users, roles, audit
db/
  analytics/       ClickHouse migrations and batch jobs
  operational/
    migrations/    PostgreSQL migrations
    grants/        the least-privilege role, and what makes the audit log append-only
    jobs/          the backfill of the pre-platform load
infra/             the development environment as a compose file
tools/
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
| ADR-007 | Deployment | Pending infrastructure decision |
| [ADR-008](docs/adr/ADR-008-imsi-search.md) | IMSI search: a second table ordered by IMSI, and a skip index on the event log | Accepted |
| [ADR-009](docs/adr/ADR-009-device-module.md) | Devices: a model is a TAC, and an IMEI-ordered table makes it a range | Accepted |

Every one of these was decided against measurement on the real dataset, not on reputation. Where a
claim appears in these documents, the number behind it is there too.

## Reproducing the profiling

Requires Python 3.12+ and `duckdb`:

```bash
pip install duckdb
python tools/profiling/to_parquet_base.py
python tools/profiling/q.py tools/profiling/a1.py
```

Scripts read from `D:\SQM` and `D:\TAC` and write working Parquet projections outside the
repository.
