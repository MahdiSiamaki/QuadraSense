# ADR-001 — Backend technology: .NET 10 / C#

- **Status:** Accepted
- **Date:** 2026-09-12
- **Deciders:** Architecture, with the maintaining team's stated expertise

---

## Context

The backend has two quite different jobs:

1. **Ingestion orchestration** — discover batch folders, validate the `1.config` / `1.done` protocol, hash
   files for idempotency, stream-validate rows, drive the load, track job state, refresh aggregates.
   Measured workload: an 804M-row backfill, then 8.26M events/day.
2. **API serving** — authn/authz, query construction, pagination, filtering, export orchestration.

A crucial measurement shapes this decision. In profiling, CSV → columnar load ran at **5.55M rows/s** when
the *database engine* read the file directly, versus **138K rows/s** when rows were pushed through a client
protocol. The conclusion is that **the heavy data movement must happen inside the storage engine, not in
application code**. The backend issues `INSERT … SELECT FROM file(…)`-shaped commands and supervises them; it
does not stream 800M rows through itself.

That materially lowers the bar for the backend runtime. It does not need to be a data-processing powerhouse.
It needs to be good at long-running job supervision, correct concurrency, clean API construction, and being
maintainable by the team that will own it.

The maintaining team's stated strengths are **.NET / C#**, Vue.js, JavaScript, MS SQL Server, MongoDB, Kafka,
Elasticsearch, and Redis. The team explicitly deferred the final choice, but "Maintainability" and "Developer
Experience" were named requirements, and a stack the team cannot confidently operate is a liability
regardless of its benchmark scores.

## Options considered

### A. .NET 10 / C# — **chosen**

- **Pros:** the team's strongest language. Genuinely first-rate for this shape of work: `async`/`await` and
  `IAsyncEnumerable` suit streaming validation and long-running jobs; `System.Threading.Channels` gives clean
  backpressure; hosted services (`BackgroundService`) are a natural fit for the ingestion worker; minimal APIs
  plus built-in OpenAPI cover the API surface; performance is within noise of Go for I/O-bound work. Already
  installed here (.NET 10.0.400). Single self-contained publish for on-prem deployment. Strong static typing
  catches the class of bug that matters most here — identifier type confusion.
- **Cons:** heavier memory footprint than Go. Licensing/vendor perception in some environments (the runtime
  itself is MIT-licensed and cross-platform, so this is perception rather than substance).

### B. Go

- **Pros:** smallest memory footprint, single static binary, excellent concurrency, lowest operational burden.
- **Cons:** the team does not list it. For a system whose hot path is "supervise the database", Go's
  advantages are largely theoretical here, while the cost of an unfamiliar language is immediate and real.

### C. Python / FastAPI

- **Pros:** best data ecosystem (DuckDB, Arrow, Polars) — attractive for the validation layer.
- **Cons:** not a listed team strength. The data-ecosystem advantage is largely neutralised by the decision
  to push work into the engine. Weaker typing guarantees on a codebase where confusing `imei` (text) with an
  integer would silently corrupt results.

### D. TypeScript / NestJS

- **Pros:** one language shared with the Vue frontend; end-to-end type sharing.
- **Cons:** weakest of the four for long-running CPU-adjacent job supervision. The type-sharing benefit is
  achievable anyway by generating a TypeScript client from OpenAPI.

### E. Java / Kotlin (Spring Boot)

- **Pros:** mature, excellent performance, strong ecosystem. Java 26 is installed.
- **Cons:** not a listed team strength; heaviest footprint and slowest startup of the options.

## Decision

**.NET 10 with C#**, structured as two deployable units from one solution:

- `Sqm.Api` — the HTTP API (minimal APIs, OpenAPI-generated contract).
- `Sqm.Ingestion` — a worker service running the pipeline as hosted background jobs.
- Shared class libraries: `Sqm.Domain` (entities, validation rules, no I/O), `Sqm.Data` (persistence),
  `Sqm.Contracts` (DTOs).

Business logic lives in `Sqm.Domain` and application services, never in route handlers.

## Rationale

1. **It is the team's strongest stack**, and the requirement list names Maintainability and Developer
   Experience explicitly.
2. **It is objectively well-suited**, not merely a concession. For an I/O-bound job supervisor and API,
   .NET is in the top tier on every axis that matters here.
3. **The measurement removed the main reason to prefer something else.** Because the engine does the bulk
   data movement, no candidate's raw throughput advantage would show up in production.
4. **Strong typing is a genuine correctness control** for this data: `imei` must be text and `msisdn`/`imsi`
   must be 64-bit integers, and the compiler enforcing that is worth real money on a 3B-row dataset.

## Consequences

- **Positive:** the team can maintain, debug and extend the system from day one. Self-contained publish makes
  on-prem deployment a file copy plus a service registration. First-class OpenAPI generation gives the
  frontend a typed client for free.
- **Negative:** higher baseline memory than Go — irrelevant at this scale, but noted.
- **Neutral:** if the analytics engine chosen in ADR-003 is one the team does not already know, the learning
  cost lands there rather than in the application language. That is the right place for it to land, because
  the engine is operated through SQL and configuration rather than written against daily.
- **Follow-up:** pin the .NET SDK version in `global.json` so builds are reproducible.
