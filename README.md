# Device Intelligence Platform

Analytics platform for mobile device / SIM / subscriber binding data, enriched with the GSMA TAC device
database.

> **Status: Phase 1 — Architecture.** No application code yet. Discovery is complete and the data is fully
> profiled; see `docs/discovery/`.

---

## What this system does

A mobile operator delivers a daily feed describing which **handset** each **SIM** and **phone number** is
currently bound to. This platform ingests that feed, enriches it with device metadata from the GSMA TAC
database, and answers questions like:

- What is the active device population by manufacturer, model, OS and device type?
- How is it changing — which brands are growing, which are losing users, and to whom?
- Which subscribers changed device or swapped SIM, and how often?
- What is the health of the incoming data?

## Scale (measured, not estimated)

| | |
|---|---:|
| Initial load | **125,939,523** bindings (4.86 GiB) |
| Change history available | **677,580,701** events (32.2 GiB, 82 days) |
| Daily volume | **~8.26M** events/day |
| Current active state | **~108M** bindings over **~73.3M** numbers |
| Growth | **~3.0B** events/year |
| Device reference data | 270,166 TACs |
| TAC enrichment coverage | **92.8%** |

## Repository layout

```
docs/
  discovery/     Phase 0 — what the data actually is, measured
    01-data-profiling-report.md    full profiling results
    02-open-questions.md           questions + documented assumptions
  architecture/  Phase 1 — how the system is built
    01-overview.md                 requirements, diagrams, risks
    03-data-model.md               entities, tables, quality rules
    04-security-model.md           roles, controls, audit
    05-roadmap.md                  phased delivery plan
  adr/           Architecture Decision Records
tools/
  profiling/     reproducible data-profiling scripts (DuckDB)
  benchmark/     storage-engine benchmark harness
```

## Architecture decisions

| ADR | Decision | Status |
|---|---|---|
| [ADR-001](docs/adr/ADR-001-backend-technology.md) | Backend: **.NET 10 / C#** | Accepted |
| ADR-002 | Operational (OLTP) store | Pending |
| ADR-003 | Analytics store | Pending benchmark |
| [ADR-004](docs/adr/ADR-004-ingestion-strategy.md) | Ingestion: immutable event log, TAC joined at query time | Accepted |
| [ADR-005](docs/adr/ADR-005-frontend-architecture.md) | Frontend: **Vue 3** + Vite + Tailwind + headless primitives | Accepted |
| ADR-006 | Authentication | Pending |
| ADR-007 | Deployment | Pending infrastructure decision |

## The three things a newcomer should understand first

**1. The grain is a *binding*, not a subscriber.**
`(msisdn, imsi, imei)` — a phone number, a SIM, and a handset. Zero duplicates across 125.9M rows, so it is a
genuine natural key. 51.3% of numbers were seen on more than one device, so "one row per subscriber" is not
a shape this data has.

**2. The feed is set-semantic and order-dependent.**
Daily files carry `add` / `remove` labels that toggle a binding. Verified over 8,062,257 transitions:
0.383% violations, **all of them repeated removes, zero repeated adds**. Applying files out of order produces
a wrong answer, so ingestion order is tracked explicitly.

**3. The feed has no dates.**
This is the project's largest open risk. File ordering was validated independently (the TAC allocation-date
frontier advances monotonically across all 82 files), but calendar dates cannot be recovered from the data.
Until the source supplies them, time axes are labelled **delivery sequence**, not date — the schema already
has a `data_date` column ready to backfill.

## Reproducing the profiling

Requires Python 3.12+ and `duckdb`:

```bash
pip install duckdb
python tools/profiling/to_parquet_base.py
python tools/profiling/q.py tools/profiling/a1.py
```

Scripts read from `D:\SQM` and `D:\TAC` and write working Parquet projections outside the repository.
