# ADR-017: Filtered dashboard views read population marts, and abandoned reads are killed

- **Status:** Accepted
- **Date:** 2026-10-19
- **Decided by:** the product owner, twice, on the measurements in this record: the per-model mart
  over the two alternatives below, and then - once it measured 2.5-3.4 s for a broad filter - a
  finished row per filter value on top of it.

## Context

`http://localhost:5173/?deviceType=Smartphone` hung. Reproduced on the real stack, 2026-10-19:

- The KPI row's `GET /api/v1/dashboard/kpi?deviceType=Smartphone` ran **182.9 s** and failed with
  **500**: the API's ClickHouse client gave up at its 180 s `HttpClient.Timeout`.
- The browser's query cache retried it at once (`retry: 1`), so the card loaded for six minutes.
- ClickHouse finished the abandoned query anyway: it was still running 199 s in, beside the retry.
- Every widget that honours the lens (KPI, KPI summary, enrichment, top N, distribution) took the
  same path, so a dashboard of them started several such reads together, each slowing the others.

The frontend was not at fault: the lens reaches the widgets through the URL, the query keys hash
identically for the same lens, and nothing refetched in a loop.

The read was the raw-table fallback chosen in analytics migration 009: the marts hold finished
distinct counts that cannot be added across models, so a filter went to `binding_current FINAL`.
That was ~6 s when it was chosen. It is not the query that changed but the table:

| `binding_current`, 2026-10-19 | |
|---|---:|
| live (active) bindings | 120,354,456 |
| stored row versions | 817,326,809 in 18 parts |
| `count()` with the Smartphone filter, `FINAL` | 74 s |
| the same without `FINAL` (wrong: counts every version) | 5 s |
| the full filtered KPI, `FINAL`, alone | 100 s |

`binding_current` is a ReplacingMergeTree: every daily fold inserts a new version of each binding it
touched, and merges collapse them. Merges are capped at 2 GiB on this node
(`infra/clickhouse/memory.xml`, a memory decision recorded in `11-clickhouse-memory.md`): the table
was expected to settle at about four parts. Since the feed doubled in size in September
(`docs/discovery/04-…`) it holds 6.8 versions per binding in 18 parts, and `FINAL` collapses all of
them on every read, slower every day.

## Decision

### 1. A per-model population mart with mergeable distinct counts

Every lens filter - vendor, manufacturer, device type, OS, TAC, unknown devices - is a property of the
model (TAC). `agg_tac_population` (analytics migration 027) holds, per delivery and per TAC, the
bindings, the unknown-device and malformed-IMEI split, and **uniq() states** for subscribers, SIMs and
handsets. A filtered view is the matching models' rows: bindings added, states merged (`uniqMerge`),
which is uniq() over the union - the raw path's own estimator, so a subscriber with two models is
still one and the figures keep their meaning and their ~0.5% error.

It is built in `refresh_marts.sql` section 1b, in the pass that already grouped `binding_current` by
TAC for `agg_device_model`; `agg_device_model` is now finalised from the states
(`finalizeAggregation(uniqState(x))` is `uniq(x)`), so the refresh still reads the table once for
both. Built for delivery 246 at the refresh's limits (one thread, 1.2 GiB, spilling at 300 MB):

| The 1b pass, same table, same limits | Duration | Peak |
|---|---:|---:|
| as before: finished `uniq()` per model | 682.6 s | 304 MiB |
| keeping `uniqState()` per model, default insert blocks | failed twice | 1.20 GiB |
| keeping `uniqState()`, inserted in blocks of 8,192 rows | **841.9 s** | 368 MiB |

The default insert gathers up to ~1M rows into one block, so all 158,002 models' states were held at
once; in small blocks they are written as they come. **Keeping the states costs about 160 s, +23%, on
this pass**, and the states take 1,005 MiB on disk - they are hash sets and do not compress.

The figures were checked against the raw path's: Smartphone is 93,397,209 bindings, 62,608,920
subscribers and 66,647,027 handsets either way, exactly, and `agg_device_model` finalised from the
states equals the one it replaced, model for model.

### 2. One filter on its own is a finished row

Merging states costs what they weigh. Measured on delivery 246, the KPI row:

| Filter | Models | Merging states |
|---|---:|---:|
| one TAC | 1 | 0.19 s |
| Modem | 1,971 | 2.5 s |
| vendor Samsung | 14,581 | 2.7 s |
| OS Android | 29,980 | 3.2 s |
| Smartphone | 35,069 | 3.4 s |

A type's models are spread through the TAC order, so even 1,971 models touch most blocks of states.
Against a 500 ms dashboard budget, the product owner chose a second mart: `agg_filter_population`
(migration 028) holds, per delivery, one finished row per value of each filter - device type, OS,
manufacturer, vendor - and one for "unknown devices excluded", merged from the states at refresh,
never from `binding_current`. Each key is the filter's own comparison (`FilterBuilder.ForCurrentState`:
exact, trimmed, `trim(operatingSystem)`, the vendor as `coalesce(vendor_canonical, manufacturer)`), so
a value the filter treats as one is one row. `MartRouting.SingleFilter` decides when a row answers;
a combination, a TAC or a number prefix never does.

Built in `refresh_marts.sql` section 1c: 92 s in all (device type 28.2 s, OS 20.2 s, manufacturer
21.1 s, vendor 12.9 s, unknown devices 8.2 s), peak 873 MiB - under the 1.2 GiB limit, at 73% of it.

### 3. Routing

| Filter | Read | Measured, delivery 246 |
|---|---|---:|
| none | the KPI and device marts, as before | 88 ms (KPI) |
| one device type, OS, manufacturer or vendor; or unknown devices excluded | its row in `agg_filter_population` | **65 ms** (store), 82–185 ms (browser) |
| a combination, or one TAC | the matching models' states, chosen first | **2.6 s** (Smartphone + Samsung) |
| a number prefix (no widget sends one) | `binding_current FINAL`, as before | not measured with a prefix; this read took 100 s for a device type |
| breakdowns counted by bindings | the per-model rows, summed | 0.2–0.75 s |
| breakdowns counted by subscribers or handsets, filtered | the per-model states, merged per group | 2.4 s alone, 3–12 s with three together |

"Chosen first": the store picks the models from the small columns (`MatchingModels`) before any state
is read. Filtering on the joined catalogue made ClickHouse read every model's states and discard the
rest - a filter matching nothing took 2.0 s, and 0.03 s like this. The KPI reads the models table with
no catalogue joined beside the states: with the joins there, ClickHouse took the filtered models for
the small side and built a hash table of their states, and Smartphone + Samsung took 8.9 s (35.6 s
under load) instead of 2.5 s. The breakdowns need the catalogue for their grouping key, so they keep
the joins with `query_plan_join_swap_table = 'false'`.

Each mart serves the delivery the marts serve (`mart_ready`), the same one as the unfiltered figures
beside it, and is used only once it holds that delivery; until then the next path down answers.

### 4. A query the API stops waiting for is killed

`AbandonedQueryCanceller`, a handler on the API's ClickHouse client, names every query
(`query_id`) and, when the request is cancelled - the browser left, the lens changed, the client
timeout fired - sends `KILL QUERY` for it. Measured on the real server
(`AbandonedQueryCancellerTests`): with it, a query abandoned after 2 s is gone within 5 s; without it,
the same query was still running.

### 5. A timeout is a 504, and is not retried

`StoreTimeoutExceptionHandler` answers a store timeout with 504 "The data store did not answer in
time" instead of 500, and the frontend never retries a 504 or a 4xx (`lib/queryClient.ts`): a 504 is
the same slow query again, and a refused request is audited, so a retry wrote the denial twice.

### 6. Refresh sends each query once

The dashboard's Refresh bumps a token the KPI cards answer by refetching, and also invalidates the
queries. Vue Query runs an invalidation's refetch on the next tick and, by default, cancels what it
finds in flight: every Refresh sent the filtered `/kpi` twice, the first aborted 8 ms in. The
invalidation now joins a request in flight (`cancelRefetch: false`, `composables/refresh.ts`).

## Rejected

- **Raise the merge cap, or `OPTIMIZE TABLE binding_current FINAL`.** It would collapse the versions,
  but it is a merge of a ~20 GiB table on a 6 GiB container - the operation `11-clickhouse-memory.md`
  records taking the server down - and versions accumulate again with every day.
- **Only stop the pile-up** (kill abandoned reads, no retry, a clear timeout). Necessary, and done,
  but a filtered view would still take 100-180 s or fail.
- **Cache answers in the API** per filter and delivery. Repeats would be instant, but the first view
  of every filter would still take the merge, and a restart empties it. Offered with option 2;
  not chosen.
- **A smaller estimator** (`uniqHLL12`, `uniqCombined(12)`) to shrink the states. Filtered figures
  would come from a different estimator than the unfiltered ones beside them, with ~1.6% error
  instead of ~0.5%.
- **`cancel_http_readonly_queries_on_client_close`** in the API's ClickHouse profile. Measured: it
  did nothing - a query whose client was killed ran on to its 120 s limit. ClickHouse notices a
  closed connection only when it writes to it, and an aggregate writes nothing until it ends.
- **`send_progress_in_http_headers`.** It does make ClickHouse notice (the query ended with
  NETWORK_ERROR as the client left), but it commits the 200 status before the query finishes; an
  error after that arrives inside a 200 body (measured: `X-ClickHouse-Exception-Code: 395` with
  `HTTP/1.1 200 OK`), which the driver would read as data.

## Consequences

- **The refresh is about 250 s longer** for these two marts (+160 s in section 1b, +92 s in 1c). The
  2026-10-15 refresh of delivery 246 took 63 minutes in all.
- **Breakdowns counted by subscribers or handsets, under a filter, still merge states**: 2.4 s alone,
  up to 12 s with three such widgets together. Finished rows per (filter value, breakdown key) pair
  would answer them; not built, because it is a further refresh cost the owner has not chosen.
- **Section 1c's peak is 73% of the refresh's memory limit.** It grows with the number of vendors
  and manufacturers; watch it if those double.
- **The catalogue is read at refresh**, as for every other mart: a GSMA version activated mid-day
  shows in single-filter rows from the next refresh.
- Both marts are dropped and rebuilt per delivery, and checked for duplicates with the others
  (`MartRefresh.UniqueWithinDelivery`).
- `binding_current` still grows in versions. Every other `FINAL` reader (the mart refresh itself, the
  risk snapshot; current-state lookups by key, which stay fast through the primary index) pays it.

## Corrections

Migration 027's header records "336 s at two threads, 149 MiB peak" for building the states. That was
measured on a query whose outer SELECT never read the state columns, so ClickHouse never built them;
the real figures are in decision 1. 027 is applied and immutable, so the correction is here and in the
header of 028.
