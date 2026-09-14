# Testing Strategy

**Status:** Draft — Phase 1.

The goal is not a coverage percentage. It is confidence in the handful of things that, if wrong, make the
whole product wrong. For this system that is overwhelmingly **the ingestion pipeline**: a dashboard bug shows
a wrong chart, but a pipeline bug silently corrupts 800M rows and nobody notices for months.

---

## 1. Where the risk actually is

| Area | Risk if wrong | Test investment |
|---|---|---|
| Delta fold (add/remove → state) | **Silent, total data corruption** | Highest |
| Ordering and sequence handling | Wrong state, undetectable | Highest |
| Idempotency / duplicate file | Double-applied day | Highest |
| Schema-change detection | Silent drift | High |
| TAC enrichment and versioning | Wrong manufacturer everywhere | High |
| Validation and quarantine | Good rows lost, bad rows admitted | High |
| Aggregation correctness | Wrong dashboard numbers | High |
| RBAC and audit | Unauthorised access to PII | High |
| API pagination/filtering | Annoying, visible, recoverable | Medium |
| UI components | Visible immediately | Low–Medium |

Effort follows this table, not an even spread across the codebase.

---

## 2. The reconciliation test — the one that matters most

Discovery produced independently measured facts about this dataset. The pipeline must reproduce them. This is
an integration test that runs against real engines with the real files, and it **gates Phase 4**.

| Assertion | Expected | Tolerance |
|---|---|---|
| Initial dump rows loaded | 125,939,523 | exact |
| Delta events loaded | 677,580,701 | exact |
| Distinct triples in initial dump | 125,939,523 (zero duplicates) | exact |
| Active bindings after replaying 82 days | ~108.0M | ±1% |
| Distinct active MSISDNs | ~73.3M | ±1% |
| TAC match rate on initial dump | 92.804% | ±0.01 |
| `imei = '000000'` rate, initial dump | 6.969% | ±0.01 |
| `imei = '000000'` rate, deltas | 2.958% | ±0.01 |
| Redundant adds counted | 19.18% | ±0.5 |
| Orphan removes counted | 1.77% | ±0.2 |
| Alternation violations | 0.383%, **all repeated removes** | ±0.05, zero double-adds |
| Quarantined rows, initial dump | 33 | exact |

The last one deserves emphasis: **zero double-adds is a structural property of the feed**, verified across
8,062,257 transitions. If a future run reports even one `add` following an `add`, either the source changed or
our ordering broke — and either way someone must look. It is a canary, not a statistic.

A fast variant runs on the 1/64 deterministic sample in CI; the full version runs nightly or on demand.

---

## 3. Pipeline failure modes (explicitly required by the brief)

Each is a test with a purpose-built fixture:

| Scenario | Expected behaviour |
|---|---|
| **Duplicate file** (same bytes) | Detected by SHA-256, recorded, **zero rows changed** |
| **Same file renamed** | Still detected — the hash is of content, not name |
| **Broken/truncated file** | Fails cleanly; no partial fold left applied |
| **Invalid schema** (renamed column) | Quarantined, operator notified, nothing ingested |
| **Added column** | Accepted with a warning |
| **Partial failure** (crash mid-batch) | State consistent on restart; batch resumable or cleanly re-runnable |
| **Reprocessing** a batch | Identical end state — the fold is deterministic |
| **Re-running the mart refresh** | Identical counts. **Regression test — this bug happened.** A refresh that only INSERTed doubled `agg_device_daily` to 251,879,046 against a real 125,939,523, silently, because a `SummingMergeTree` sums duplicates without complaint. Fixed structurally by partitioning each mart on `seq` and dropping the partition before insert. The test runs the refresh three times and asserts the total is unchanged. |
| **Out-of-order files** | Detected where possible; explicitly logged as an assumption |
| **Late data** | Applied in sequence order, counters reflect it |
| **Empty file** | Handled as a valid zero-row day, not an error |
| **Very large file** (13.5M rows, the observed max) | Completes within the ingest window, memory stays bounded |
| **Missing `1.done`** | Batch held, not ingested, operator alerted |
| **Disk full mid-load** | Fails safely, no corrupt state |
| **All rows invalid** | Whole batch quarantined, clear report |

The memory-bounded assertion matters: the pipeline must stream. A test asserts peak memory stays flat while
processing the largest file, so a future refactor that accidentally buffers a whole file fails the build.

---

## 4. Test levels

**Unit** — `Sqm.Domain`, no I/O, milliseconds. Validation rules, IMEI/TAC extraction with the `length = 14`
guard, identifier format rules, the state-transition function. Property-based tests for the fold: for any
sequence of add/remove events, the final state equals the last event's implication — the invariant discovery
proved.

**Application** — use cases with test doubles. Job orchestration, status transitions, error propagation,
correlation-ID flow.

**Integration** — real PostgreSQL and the real analytics engine in containers (Testcontainers), migrations
applied from scratch. Schema, constraints, the `INSERT`-only audit role, and the actual load paths. No mocked
database: the queries under test are the product.

**API** — contract tests per endpoint: authn, authz per role, validation errors, pagination boundaries,
filter allow-lists, error envelope shape, and that **no stack trace ever reaches the client**.

**Frontend** — component tests for the design system's four async states; feature tests for filter→URL→query
round-tripping.

**E2E** — a deliberately short list of critical paths only: log in → dashboard loads; upload a file → job
completes → data appears; upload a duplicate → rejected with a clear message; apply a filter → share the URL →
same view; export above threshold → background job → download; a Viewer cannot reach subscriber lookup.

---

## 4b. What exists today, and what it caught

The plan above is the target. This is the state of it.

| Project | Tests | What it covers |
|---|---:|---|
| `Sqm.Domain.Tests` | 62 | The binding fold and the identifier rules. Every case cites a discovery measurement. |
| `Sqm.Ingestion.Tests` | 22 | Row validation and schema-change classification. |
| `Sqm.Integration.Tests` | 32 | The import queue's guarantees, the authorisation rule, and endpoint coverage - against a real PostgreSQL. |
| `Sqm.Application.Tests` | 1 | Placeholder. |

**117 passing.**

### The integration tests earned their cost on the first run

All six failed, and none of the failures were in the tests. Each was a defect that would have
reached production and each is invisible to a mocked repository:

| Defect | How it would have shown up |
|---|---|
| Dapper cannot bind `DateOnly` at all | Every enqueue carrying a business date throws at runtime. |
| `attempt`, `max_attempts`, `priority` are `smallint`, the models say `int` | Dapper cannot match the record constructor; no job can ever be claimed. |
| Npgsql surfaces `timestamptz` as `DateTime`, the models use `DateTimeOffset` | "A parameterless default constructor is required" — a message that says nothing about the real mismatch. |

This is the argument for the rule in section 4 stated as a result rather than a principle: a fake
that returns what we expect tests our expectations. The behaviour under test here is
PostgreSQL's — `FOR UPDATE SKIP LOCKED`, partial unique indexes, transaction isolation — and a
mock cannot be wrong in the ways a database is wrong.

### Skipping is not failing

When no database is reachable the integration tests skip with the reason. A developer without
the container running should see "skipped: no database", not a wall of red that trains them to
stop reading test output.

### The reorder test

`SchemaFingerprintTests.Reordering_is_rejected_even_though_every_column_is_present` exists
because of a specific afternoon. Eleven TAC columns were renamed to placeholders; ClickHouse's
`CSVWithNames` matches by header name, so the load reported exactly the right row count and
those eleven columns silently received nothing. It surfaced weeks later, when an eSIM query
returned zero.

That is the shape of the worst bug this system can have: not a crash, but a number that is
wrong and looks fine. Tests that only assert "the import succeeded" would have passed
throughout.

### The check that only existed after it was needed

`--verify-marts` was written because a report of "the dashboard is fully current" was wrong. The
check that had been made was "does each mart have a partition for this delivery", and it passed
while a third of the refresh had failed: the device-class mart held one of three measures, the
capability mart one of three, and the dimension mart was missing `vendor` — which is the one the
Top Vendors widget reads.

The lesson generalises past this bug. **Existence is a much weaker property than completeness**,
and a verification that asserts the weaker one reads as if it asserted the stronger one. The
check now asserts both halves that matter:

- every slice the refresh is supposed to write is present, named individually
- the totals reconcile against the KPI mart's active-binding count

The second is the one with teeth. A missing mart shows an empty chart and somebody asks about
it; a mart that disagrees with the headline figure shows a plausible number and nobody does.

---

### The test that makes "enforced in the backend" checkable

The brief was explicit: do not merely hide permissions in the frontend. The difficulty with that
requirement is that the failure mode is an *absence* - somebody adds an endpoint and does not add
the authorisation call - and absences are exactly what code review misses.

`EndpointAuthorizationTests` enumerates the application's own `EndpointDataSource`, which is the
list the router actually serves from rather than one maintained by hand, and fails on any route
that is neither authorised nor on a four-entry allow-list where each entry carries the reason it
has to be anonymous. It also asserts that the fallback policy is non-null, which is what turns a
forgotten `RequireAuthorization` into a 401 rather than an open door.

Three more assertions sit alongside it: that every permission an endpoint names exists in the
catalogue, that the catalogue and the C# constants agree in both directions against the real
database, and that the four routes touching subscriber data name the permission that guards them.

### The authorisation rule is tested against PostgreSQL, as the application role

`AccessControlTests` connects as `sqm_app` - the least-privilege role - not as the owner.
Connecting as the owner would leave the append-only guarantee untested while appearing to pass;
as it is, the suite asserts that `UPDATE` and `DELETE` on both audit tables are refused with
SQLSTATE 42501.

The rule itself is written twice - as SQL for the request pipeline, as C# for the user detail
page's provenance - so one test asserts they agree for a user holding a role, a direct grant and a
direct deny at the same time. If they ever diverged, the screen would be describing a different
system from the one enforcing access.

On the first run the password policy rejected the fixture's own passwords: "TestPassword!Long12345"
for accounts named "Test something". The rule was right and the test data was wrong, which is the
better way round.

---

### Still missing

- **API contract tests.** The authorisation half is now covered; the shape-of-the-payload half is
  not, and waits on openapi-typescript generating the client types.
- **Frontend component tests.** The four async states and filter→URL round-tripping.
- **E2E.** The short critical-path list in section 4. The sign-in through deny-a-permission path
  was walked by hand in a browser and is written up in the commit; it is not automated.
- **The reconciliation test of section 2**, as an automated job rather than a manual query.
- **Upload security cases** from section 5: path traversal, oversized file, wrong content type.

---

## 5. Security tests

| | Status |
|---|---|
| Every endpoint rejects unauthenticated requests | **Done** - by route enumeration, not by a hand-kept list |
| Roles asserted against endpoints, negatives included | **Done** - the seeded roles are asserted to hold and to lack specific permissions; Viewer is asserted not to have `lookup.subscriber` |
| Audit role cannot `UPDATE` or `DELETE` | **Done** - asserted against the running database as that role |
| Lockout, timing equality, session revocation | **Done** |
| Injection: parameterisation, allow-listed columns | Partly - every statement is parameterised and sorts come from enums, but nothing asserts it |
| Upload: path traversal, oversized file, wrong content type, zip bomb | Not yet |
| No log line or URL ever contains an MSISDN, IMSI or IMEI | Not yet - the rule is followed and unenforced |

---

## 6. Performance tests

Not a one-off exercise — the budget in `01-overview.md` is asserted in CI against a representative dataset, so
a regression fails the build rather than being discovered in production.

Recorded per endpoint: P50, P95, P99 at the agreed concurrency. Tracked over time; a significant regression is
a build failure, not a discussion.

---

## 7. What is deliberately not tested

- Generated code (the OpenAPI client).
- Third-party library behaviour.
- Trivial getters, mappers with no logic.
- Exact visual appearance — no screenshot tests. They are brittle and would slow every legitimate design
  change; the design system's structure is tested instead.

---

## 8. CI

| Stage | Scope | Runs on |
|---|---|---|
| Lint + type check | all | every push |
| Unit | all | every push |
| Integration | real containers | every push |
| API contract | all endpoints | every push |
| Pipeline failure modes | fixtures | every push |
| **Reconciliation (sampled)** | 1/64 sample | every push |
| Security + dependency scan | all | every push |
| E2E | critical paths | pull request |
| **Reconciliation (full)** | all 804M rows | nightly / on demand |
| Performance budget | key endpoints | nightly |

A red build blocks merge. The full reconciliation is too slow for every push but too important to run rarely,
so it runs nightly against the real data.
