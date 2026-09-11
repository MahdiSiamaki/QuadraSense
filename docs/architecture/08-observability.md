# Observability

**Status:** Draft — Phase 1.

The operating question this must answer: *the daily import finished — did it do the right thing?* With
~8.26M events a day folding into a 108M-row state, nobody can eyeball that. The system has to tell you.

---

## 1. Structured logging

Every log line is JSON. No `Console.WriteLine`, no free-text-only messages.

Every line carries:

| Field | Purpose |
|---|---|
| `timestamp` | UTC, ISO-8601 |
| `level` | |
| `message` | stable, templated — never string-interpolated |
| `correlationId` | follows one request or one batch end to end |
| `requestId` | per HTTP request |
| `jobId` / `batchId` | per background job |
| `userId` | when acting for a user |
| `service` | `api` or `ingestion` |
| `durationMs` | on completion events |

**Identifiers are never logged.** No MSISDN, IMSI or IMEI appears in any log line, at any level. Since
masking in the UI is off (ADR: product-owner decision), keeping identifiers out of logs is the control that
is actually doing the work. Log counts, batch IDs and quarantine row IDs instead — a row's raw content is
retrievable from the quarantine table by authorised users, which is auditable; a log file is not.

Errors log the exception and stack trace server-side with the correlation ID; the client receives the
correlation ID and a human-readable message, never a stack trace.

---

## 2. Metrics

### Ingestion

| Metric | Type | Why |
|---|---|---|
| `ingest_batch_duration_seconds` | histogram | is the daily window holding? |
| `ingest_rows_total{outcome}` | counter | received / valid / invalid / inserted / updated / quarantined |
| `ingest_redundant_add_ratio` | gauge | **baseline 19.18%** — a sharp move means the source changed |
| `ingest_orphan_remove_ratio` | gauge | **baseline 1.77%** |
| `ingest_unknown_imei_ratio` | gauge | **baseline 6.97% / 2.96%** |
| `ingest_tac_match_ratio` | gauge | **baseline 92.8%** |
| `ingest_alternation_violation_total{kind}` | counter | `double_add` must stay at **zero** |
| `ingest_batch_rows` | gauge | baseline 6.6M–13.5M/day |
| `ingest_last_success_timestamp` | gauge | drives the "no data today" alert |

The baselines are not decoration. They come from measuring the real 804M rows, which means we can alert on
*deviation from known-good* rather than on arbitrary thresholds — the difference between an alert that gets
acted on and one that gets muted.

### API

Request rate, duration histogram (P50/P95/P99) by endpoint, error rate by class, in-flight requests, and
query duration by widget type. Export job queue depth and duration.

### Stores

Connection pool utilisation, slow-query count, disk usage and growth rate, replication lag if applicable.

---

## 3. Alerting

Alerts are rare and actionable. Everything else is a dashboard.

| Alert | Condition | Severity |
|---|---|---|
| No import today | `ingest_last_success_timestamp` > 26 h | **critical** |
| Batch held (missing `1.done`) | any | warning |
| Duplicate file submitted | any | info |
| **Double-add detected** | `double_add` counter > 0 | **critical** — feed semantics changed or ordering broke |
| Quality ratio drift | any baseline ratio moves > 5 points | warning |
| TAC match rate drop | < 90% | warning |
| Batch row count out of range | < 3M or > 20M | warning |
| Import failed | any | critical |
| API P95 over budget | sustained 15 min | warning |
| Disk > 80% | | warning |
| Auth failure spike | > 20/min from one source | warning |

The double-add alert is the sharpest instrument here. Across 8,062,257 measured transitions there were
**zero** double-adds. It is a genuine invariant of this feed, so a single occurrence is real signal — either
the source system changed its semantics or our file ordering is wrong, and both demand a human.

---

## 4. Tracing

OpenTelemetry spans on: HTTP request → authorisation → query build → store execution; and batch → file →
validate → load → fold → aggregate. Useful mainly for answering "which stage of the import is slow" without
guessing. Sampled for API traffic, always-on for ingestion jobs (there are only a few hundred a year).

---

## 5. Health endpoints

| Endpoint | Meaning |
|---|---|
| `/health/live` | process is up — never touches dependencies |
| `/health/ready` | dependencies reachable, migrations applied — safe to route traffic |
| `/health/startup` | slow initial checks complete |

Keeping `live` free of dependency checks is deliberate: a database blip should not cause the orchestrator to
kill an otherwise healthy process and turn a small problem into an outage.

---

## 6. Operational dashboards

Three, aimed at different questions:

1. **Import health** — last run, duration trend, rows by outcome, the quality ratios against their baselines,
   batch history with status.
2. **Data quality** — user-facing. Quarantine volume by rule, unknown-device rate, TAC coverage, top
   unmatched TACs, schema-change events.
3. **System** — API latency percentiles, error rate, store size and growth, job queue depth.

The first two matter more than the third here: this system's realistic failure mode is not "it crashed", it
is "it quietly imported the wrong thing".
