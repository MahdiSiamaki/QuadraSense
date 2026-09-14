# ADR-008 — IMSI search: a second table ordered by IMSI, and a skip index on the event log

- **Status:** Accepted
- **Date:** 2026-09-14

---

## Context

Both analytics tables are ordered by MSISDN first:

| Table | Rows | On disk | `ORDER BY` |
|---|---:|---:|---|
| `binding_current` | 295,013,916 | 6.65 GiB | `(msisdn, imsi, imei)` |
| `binding_event` | 1,049,379,693 | 25.28 GiB | `(msisdn, imsi, imei, seq)` |

A filter on the **second** key column prunes nothing. ClickHouse falls back to "generic exclusion
search", which can only exclude the span between two marks when the leading column is unchanged
across it — and MSISDN is nearly unique, so it almost never is.

Measured, exact IMSI on `binding_current`:

| IMSI | Time | Rows read | Marks |
|---|---:|---:|---|
| 432113900974194 | 1,385 ms | 295,013,916 | 36,013 / 36,013 |
| 432113933569397 | 1,284 ms | 295,013,916 | 36,013 / 36,013 |
| 432113950963412 | 1,419 ms | 295,013,916 | 36,013 / 36,013 |
| 432113984058502 | 1,273 ms | 295,013,916 | 36,013 / 36,013 |
| 432113991761332 | **23 ms** | **16,384** | **2 / 36,013** |

The fifth row is not a fifth data point, and it cost an hour to realise that. It is one value
whose position lets the exclusion search bound it, and the first version of this investigation
took it as representative and concluded no work was needed. **Four of five read the entire
table.** A single sample is not a measurement.

The same on `binding_event`: **10,571 / 12,866 / 20,792 ms**, each reading all 1,049,379,693 rows
across 128,418 marks and 7.82 GiB.

### What a prefix is, and how short it can usefully be

Every IMSI in this feed begins `43211` — MCC 432, MNC 11, Iran / MCI. Prefix cardinality over the
295-million-row table:

| Prefix digits | Distinct values | Worst bucket | Average bucket |
|---:|---:|---:|---:|
| 3 | 1 | — | 295,013,916 |
| 5 | 1 | — | 295,013,916 |
| 6 | 8 | — | ~37,000,000 |
| 7 | 61 | — | ~4,800,000 |
| 8 | 86 | 20,139,179 | 3,426,414 |
| 10 | 3,769 | 557,158 | 78,183 |
| 12 | 350,712 | 94,931 | 840 |

So a prefix below ten digits is not a search: it is a request to read a large fraction of the
table. **Ten digits is the minimum the API accepts**, and the refusal names the reason — "every
IMSI here starts 43211, so a shorter prefix matches the whole network" — rather than stating a
rule that would read as arbitrary.

A prefix is then a **numeric range**, not a string match: `4321139917` is exactly
`[432113991700000, 432113991799999]`, which a primary index can seek to. `LIKE '4321139917%'`
would read the whole column.

## Options considered

### A. Bloom-filter skip index on `binding_current.imsi`

Built and measured first, because it is twenty times cheaper in storage than a copy.

| Configuration | Size | Exact lookup | Rows read | Marks |
|---|---:|---|---:|---:|
| `bloom_filter(0.01)` | 109 MiB | 73–190 ms | ~2.8M | ~330 / 36,013 |
| `bloom_filter(0.001)` | 164 MiB | 58–140 ms | ~280K | ~34 / 36,013 |

The false-positive counts match the theory exactly — 36,013 granules × the configured rate — which
is itself the confirmation that the index was doing the work rather than something else.

**Rejected as the whole answer.** A bloom filter answers set membership and nothing else, so
prefix search gets no help at all: it still read all 295M rows in **2,518 ms**. Half the
requirement is not served.

### B. A projection ordered by IMSI

Would serve both shapes from one structure, at the cost of a second sorted copy.

**Rejected on a hard constraint:** ClickHouse does not use projections for queries with `FINAL`,
and every read of `binding_current` needs `FINAL` to resolve the ReplacingMergeTree. The
projection would be built, stored, and never used.

### C. A second table ordered by IMSI — chosen

`sqm.binding_by_imsi`, a `ReplacingMergeTree(last_change_seq)` ordered by `(imsi, msisdn, imei)`,
kept in step by a materialized view on `binding_current`.

Measured after the backfill, 9 parts, same five IMSIs:

| Query | Time | Rows read | Marks |
|---|---:|---:|---:|
| exact (5 samples) | 20–50 ms | 69,143 | 9 |
| prefix, 13 digits | 16 ms | 69,143 | 9 |
| prefix, 12 digits | 21 ms | 69,143 | 9 |
| prefix, 10 digits | 39 ms | 183,831 | 23 |

**~60× faster on exact, ~120× on prefix, and 4,270× fewer rows read.** Nine marks is nine parts —
one granule each — so the floor is the part count, which is why the backfill lets merges compact
when it finishes.

Cost: **6.45 GiB**, against 916 GiB free. Through the API, end to end and warm, an exact search
is **46–54 ms**.

### D. Reorder `binding_current` by IMSI

Rejected without measurement. MSISDN lookup is the established primary access path — 12 ms
against 295M rows — and this would move the problem rather than solve it.

## Decision for the event log: a skip index, not a copy

`binding_event` is 1.05 billion rows and 25.28 GiB. A second copy would cost the same again and
would buy prefix search over history, which nothing asks for: a prefix is resolved against current
state, and then one SIM is opened.

So the event log gets `bloom_filter(0.001)` on `imsi` — **1.69 GiB, 6.7% of the table** — which
serves the query that is actually asked: one IMSI, over a date range.

| Query | Before | After (warm) |
|---|---:|---:|
| one IMSI, all 133 days | 10,571–20,792 ms, 1,049,379,693 rows | **586–683 ms, 16K–295K rows** |
| one IMSI, 30-day window | — | **143 ms, 16,384 rows** |
| one IMSI, 14-day window | — | **87 ms, 0 rows** |

`0.001` rather than `0.01` because this table has 128,418 granules: at one percent the false
positives alone would be ~1,284 granules and ~10.5M rows.

The date range prunes **partitions** before the index is consulted at all — the table is
`PARTITION BY data_date` — which is why the history panel puts the range control at the top rather
than in a filter drawer. It is the single biggest lever on what that query costs. The index is
1.69 GiB, so a cold first read of it costs seconds; a date range avoids most of it.

## Consequences

**The two tables must not drift.** A materialized view mirrors every insert into
`binding_current`, and the daily fold is an insert, so they converge without the ingestion code
knowing this table exists. The one way it breaks is stated in the migration: **an MV fires on
INSERT and on nothing else**, so a rebuild by `EXCHANGE TABLES` — which migration 015 did to
`binding_event` — bypasses it entirely. Any such rebuild must be followed by
`Sqm.Ingestion --backfill-imsi`.

**The backfill reconciles, and that earned itself immediately.** The first run reported every
chunk as succeeding and was short by exactly five rows: the chunk loop stopped at ten digits, and
five MSISDNs in the data have twelve, thirteen and fifteen — numbers beginning 971, 994 and 964,
which are the UAE, Azerbaijan and Iraq. They are anomalies and they are real. A job that says
"done" without checking is a job that will be believed.

**These two queries go over HTTP, not through the ADO driver.** Two things are needed that the
driver does not provide, and both were already paid for once in this project:

- `rows_read`, which the `JSONCompact` format returns in the response. The alternative is
  `system.query_log`, which lags by its flush interval — a search that had just run reported
  `rowsExamined: 0` — and forcing a flush costs ~500 ms against a 20 ms query.
- Per-query settings. `CustomSettings`, `set_*` in the connection string and a trailing `SETTINGS`
  clause each silently do nothing through the driver; URL parameters work. See
  `docs/architecture/11-clickhouse-memory.md` §3.

**Rows examined includes the TAC dictionary.** Both queries `LEFT JOIN sqm.tac`, which is 270,166
rows, so an exact search reports ~339,310 rows examined rather than ~69,143. The join itself costs
13 ms and the figure is still three orders of magnitude below a scan, so the scan-warning
threshold of 5,000,000 is unaffected — but anybody reading the number should know what is in it.

**Storage now carries a second copy of current state.** 6.45 GiB today, growing with the
population rather than with history. If that ever stops being acceptable, the fallback is option A
for exact search plus a minimum prefix long enough to be a range scan — which is a real
degradation, and is why it was not chosen first.

## Related

- `db/analytics/migrations/018_imsi_search.sql` — the structure, with these measurements in it
- `backend/src/Sqm.Ingestion/ImsiBackfill.cs` — the backfill and its reconciliation
- `backend/src/Sqm.Domain/Identifiers/ImsiQuery.cs` — the minimum-prefix rule and why it is ten
- `docs/architecture/12-identity-and-access.md` — `lookup.imsi` and `identifier.reveal`
- `docs/architecture/11-clickhouse-memory.md` — the merge/query collision that killed two of these runs
