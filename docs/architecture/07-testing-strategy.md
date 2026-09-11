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

## 5. Security tests

- Every endpoint asserted to reject unauthenticated requests.
- Each role asserted against every endpoint, including the negatives (Viewer must be denied lookup).
- Upload: path traversal, oversized file, wrong content type, zip bomb.
- Injection: parameterisation verified; filters rejecting non-allow-listed column names.
- Audit: every audited action asserted to write exactly one entry, and the audit role asserted to be unable
  to `UPDATE` or `DELETE`.
- Assertion that no log line and no URL ever contains an MSISDN, IMSI or IMEI.

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
