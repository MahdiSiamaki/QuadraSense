# ADR-012 — Explorer queries: an allow-listed model compiled to SQL, planned, and run within a budget

- **Status:** Accepted
- **Date:** 2026-09-30
- **Decided with the product owner:** a permission of its own (`explorer.query`, not for Viewer); 500 rows a
  page and 10,000 reachable; a budget of 100 million rows read and 30 seconds; synchronous, not jobs.

---

## Context

The Explorer lets a person compose queries over the relationship between numbers, SIMs, handsets and
models — "every Galaxy A01 with more than 10 SIMs", "SIMs seen on more than three handsets in a day" —
without writing SQL. Three facts shape how:

1. **The data is large and has three sort orders.** Current state is 453 million bindings, held three
   times — by number, by SIM, by handset (ADR-008, ADR-009). The event log is 1.8 billion rows in 233
   daily partitions. A filter on anything but the sort key reads everything: an IMSI on the
   number-ordered copy is 736 million rows; on the SIM-ordered copy, 360 thousand.
2. **The server is shared and small.** 5.2 GiB of memory for ClickHouse, shared with the imports.
3. **The results are personal data.** Masking is off by product decision, so the controls are
   permissions, audit and limits.

## Options considered

**A. Free SQL for trusted users.** Rejected. It cannot be made safe by review, it bypasses the per-kind
permissions every other page enforces, and one query can hold the server's memory.

**B. A generic query surface (IQueryable, OData).** Rejected for the reason `IDeviceAnalyticsStore` gives
already: a caller can express a scan of 700 million rows without anyone seeing it, and nothing in the
surface knows which of three copies to read.

**C. An allow-listed query model compiled to SQL — chosen.** The request is a tree of conditions over named
fields, with grouping, measures, a Having over the measures, sorting and a page. It is never SQL.

- `ExplorerCatalogue` lists every field, its type, the operators it takes and the permission it needs.
  A request that names anything else is refused.
- `ExplorerValidator` parses every value to its type (digits for identifiers, a real date for dates)
  and reports problems by path (`where.children[2].values[0]`) so the builder can show them in place.
- `ExplorerSqlCompiler` maps field names through a map written in code, and binds every value as a
  ClickHouse query parameter.

## How a query is read

The compiler looks at the conditions every row must satisfy — the top-level And, not negated — and
reads the copy whose sort key they constrain. It also adds the range that condition implies, which
never changes the answer and lets the index seek.

- A **number, SIM or handset** reads its own copy by key.
- A **TAC**, and a **brand or model**, become IMEI ranges on the handset-ordered copy. A brand or model is
  first turned into the TACs it names from the GSMA table. Measured, as rows the plan reads:

  | Filter | Rows the plan reads |
  |---|---:|
  | 5 TACs as ranges | 2.8 million |
  | 300 TACs as ranges | 26.7 million |
  | The same TACs as `tac IN (...)` on the stored column | **708 million**, because it is not the sort key |

  Past 300 TACs the ranges stop paying and the filter is applied without them (`MaxResolvedTacs`).
- **Events** need a date range, which prunes whole days before anything is read. Within the days:
  a number by key, a SIM or handset through its bloom index (the IMEI one is migration 021), and
  anything else by reading each day.

## Budget, measured before running

Every run is planned first with `EXPLAIN ESTIMATE`, which reads only the indexes and answers in about
0.3 s. A plan over the budget is refused, and its notes say what would narrow it. A plan within it runs
with the same budget as server-enforced limits:

- rows read, and seconds;
- result rows (throw, never truncate — a page cut short reads as the whole answer);
- memory and threads.

So an optimistic estimate is still stopped. Measured on the real data, 2026-09-30:

| Query | Access | Estimated | Read | Server time |
|---|---|---:|---:|---:|
| One number, default columns | KeyRead | 410,149 | 410,150 | 118–181 ms |
| One SIM / one handset (plan only) | KeyRead | 451,109 / 401,957 | — | — |
| How many SIMs has this number had | KeyRead | 32,768 | — | — |
| Galaxy A01 handsets with more than 10 SIMs | RangeRead | 7,422,501 | 7,504,422 | 4,604 ms |
| SIMs on more than 3 handsets in a day, 7 days, numbers 9121… | RangeRead | 622,592 | 622,592 | 299 ms |
| A number's events over 30 days (plan only) | KeyRead | 672,293 | — | — |
| Active handsets per device type (no key) | Scan | 737,056,021 | refused | — |

The estimates are within 1.1% of what was read.

## Security

- **Two layers of permission.** `explorer.query` admits the builder. On top of it, filtering on, showing
  or grouping by numbers, SIMs or handsets needs `lookup.subscriber`, `lookup.imsi` or `lookup.imei`.
  A query that asks for more than the caller may see is refused whole, naming what is missing.
  Counting distinct numbers names nobody and needs no lookup permission.
- **Masking.** `identifier.reveal` decides masking, on the server, as everywhere else.
- **Audit.** Every query and every refusal is audited with the dataset, the field names and the cost —
  never the values.
- **The server refuses writes.** Queries run on the read path, which the server treats as read-only
  (`readonly=2`) and cancels when the caller disconnects (Phase 0).
- **Concurrency.** At most four Explorer queries run at once across everybody, and two per person;
  past that the answer is "busy", not a queue.
- **Identifiers stay out of URLs.** POST throughout.

## Consequences

- **Synchronous only.** A query over the budget must be narrowed; there is no background job to hand it
  to. That was the product owner's choice for now; jobs, if they come, sit behind the same compiler and
  budget.
- **10,000 rows reachable by paging.** Beyond that, narrow the query. Export (audited, Phase 2) is the
  intended way out.
- **Counts are exact** (`uniqExact`), unlike the dashboard's estimated marts. This is affordable because
  a query that passes the budget reads a bounded part of the data.
- **"Not equal" and "not in" include unknown values.** "Model is not Galaxy A01" keeps handsets whose TAC
  GSMA does not know. The field descriptions say so.
- **A handset is an IMEI, not a physical device.** No dual-SIM pairing is applied anywhere in the
  Explorer; `docs/architecture/14-relationship-explorer.md` explains why pairing stays confined to the
  Relationships page.
- **"Active" means not yet removed by the feed**, and the catalogue says so where the field is offered.
