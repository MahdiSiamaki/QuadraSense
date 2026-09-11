# Repository Structure

**Status:** Draft — Phase 1, to be created in Phase 2.

---

## Layout

```
/
├── README.md
├── .gitignore                     excludes .env, build output, data files
├── .env.example                   every variable named, no real values
├── docker-compose.yml             full local stack, one command
├── global.json                    pinned .NET SDK version
├── Directory.Build.props          shared C# settings: nullable, warnings-as-errors
│
├── docs/
│   ├── discovery/                 Phase 0 findings
│   ├── architecture/              this directory
│   ├── adr/                       decision records
│   ├── operations/                runbook, backup/restore, troubleshooting
│   └── development/               local setup, migrations, testing
│
├── backend/
│   ├── Sqm.sln
│   ├── src/
│   │   ├── Sqm.Domain/            entities, value objects, validation rules — no I/O
│   │   ├── Sqm.Application/       use cases, orchestration, interfaces
│   │   ├── Sqm.Infrastructure/    persistence, file system, external adapters
│   │   ├── Sqm.Contracts/         DTOs shared by API and worker
│   │   ├── Sqm.Api/               HTTP API — thin: bind, authorise, delegate
│   │   └── Sqm.Ingestion/         worker service running the pipeline
│   └── tests/
│       ├── Sqm.Domain.Tests/      fast, no I/O
│       ├── Sqm.Application.Tests/ use cases with test doubles
│       ├── Sqm.Integration.Tests/ real containers via Testcontainers
│       └── Sqm.Pipeline.Tests/    the reconciliation and failure-mode suite
│
├── frontend/
│   ├── src/
│   │   ├── design-system/         tokens, primitives, the four async states
│   │   ├── features/              one folder per feature, co-located
│   │   │   ├── dashboard/
│   │   │   ├── upload/
│   │   │   ├── imports/
│   │   │   ├── tac/
│   │   │   ├── quality/
│   │   │   ├── lookup/
│   │   │   └── admin/
│   │   ├── shared/                cross-feature components, hooks, utils
│   │   ├── api/                   generated OpenAPI client — never hand-edited
│   │   └── router/
│   └── tests/
│
├── db/
│   ├── operational/migrations/    versioned, forward-only
│   └── analytics/migrations/      versioned, forward-only
│
├── tools/
│   ├── profiling/                 Phase 0 scripts (kept — they are the DQ baseline)
│   └── benchmark/                 storage-engine benchmark harness
│
└── .github/workflows/ (or .gitlab-ci.yml)
```

## Principles

**Dependency direction is one-way.** `Domain` depends on nothing. `Application` depends on `Domain`.
`Infrastructure` and `Api` depend inward. This is enforced by an architecture test that fails the build if a
project references outward — a convention nobody remembers is not a boundary.

**Features are vertical slices on the frontend.** A feature folder holds its own components, queries, types
and tests. Anything genuinely shared is promoted to `shared/` deliberately, not by accident. This keeps the
dashboard feature from quietly growing tendrils into the upload feature.

**The generated API client is never hand-edited.** It is produced from the .NET OpenAPI document at build
time. If the frontend needs a different shape, the contract changes, not the generated file.

**Migrations are forward-only and version-controlled.** No manual production schema change, ever. Both stores
have their own migration history because they change for different reasons at different times.

**The profiling scripts stay in the repository.** They are not throwaway: they define the measured baseline
that the Phase 3 reconciliation test asserts against. When a future import produces an unexpected number,
re-running them is how the question gets settled.

## Local development

```bash
git clone <repo> && cd <repo>
cp .env.example .env
docker compose up
```

One command must bring up both stores, the API, the worker and the UI, with migrations applied and a small
seeded dataset. The target from `05-roadmap.md` is a new developer productive in under 30 minutes.

Full-scale data is **not** in the repository and never will be — the sample fixtures are small, synthetic,
and contain no real identifiers.

## What is deliberately not here

- **No shared "Common" or "Utils" dumping ground.** These become dependency magnets. Shared code lives in a
  named project with a stated purpose.
- **No generated files committed** — the OpenAPI client, build output and lock-file-derived artefacts are
  produced, not stored. Lock files themselves *are* committed.
- **No data files.** `.gitignore` excludes CSV and Parquet outright, so a 5 GB dump cannot be committed by
  accident.
