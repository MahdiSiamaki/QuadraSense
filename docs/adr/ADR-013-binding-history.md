# ADR-013: The binding history, for timelines

- **Status:** Accepted
- **Date:** 2026-10-01
- **Decided by:** the product owner (storage, backfill, first-seen semantics, the IMSI page correction)

## Context

Phase 3 asks for timelines: for a handset, the SIMs and numbers it has had, with add and remove
dates, active or not, first and last seen - "with performance suitable for the large dataset" -
and the same for a number and a SIM.

A timeline needs every binding's dated events. The event log (1,800,586,041 events, 233 daily
partitions) is ordered by MSISDN within each day, so one entity's events are scattered across every
day its bindings were touched. Measured on the real log, reading an IMEI's events by its binding
keys:

| IMEI | Bindings | Events | Rows read | Time |
|---|---:|---:|---:|---:|
| typical | 5 | 46 | 2,121,728 | 1.5 s |
| shared | 243 | 2,202 | 84,578,811 | 29.7 s |
| most-shared | 8,578 | 16,383 | 240,695,941 | 64.8 s, over the 100M budget |
| any, by `imei =` with no index | - | - | 1,800,586,041 | full scan |

The cost grows with bindings times days, because each binding costs about one granule per day.
The entities that matter most to the risk work that follows - shared handsets - were the ones that
could not be shown.

## Options considered

1. **Read the event log by binding key** (no schema change). Fine for typical entities; shared ones
   are refused or limited. Rejected: it fails exactly where the product needs it.
2. **The IMEI bloom index (analytics migration 021).** Approved earlier for the Explorer's
   event queries, and still useful there. It does not help this case: the most-shared IMEI's 16,383
   events are spread over most days and most granules of them.
3. **One row per binding, all time.** Smallest, but a corrected day cannot be taken back out of an
   all-time aggregate without re-deriving every binding it touched from the whole log - the fold's
   slow path, about a minute and a half per million bindings.
4. **One row per binding per month, in three sort orders (chosen).** Prototyped on a block of a
   million numbers (13,048,419 events, 3,810,910 bindings, 0.72% of the log):

   | | |
   |---|---|
   | An IMEI's timeline, 218 bindings, 579 events | 81,920 rows read, 34 ms |
   | Stored events against the log, every binding in the block | 0 mismatches |
   | Rows per binding | 1.45 |
   | Size per sort order | ~180 MiB for the block, so ~23 GiB each, ~70 GiB for three (852 GiB free) |
   | Build time for the block | 32.7 s, plus 14-19 s per extra sort order (two threads) |

## Decision

Analytics migration 022 creates `binding_history` (by number) and two copies kept by materialized
views, `binding_history_by_imsi` and `binding_history_by_imei` - the pattern `binding_current` and
its copies already follow. Each row is one binding's events in one month: `in_dump`, first and last
event date, add and remove counts, and the events themselves as `(date, seq, label)`, all
`SimpleAggregateFunction`s so rows merge whatever order days arrive in.

- **Partition 0 is the initial dump.** It is a month of observations (2025-12-27 to 2026-01-25),
  not a snapshot, so its bindings have no start date. **First seen for them is the window** - shown
  as the window, sorted and computed as its first day (the product owner's decision).
- **Two paths, as the fold has.** A day the history has never seen is added (a few seconds). A day
  it has seen - a retry, or a corrected file - rebuilds its month from the log, as does any day of
  a month whose last write did not finish. A per-day ledger (`binding_history_day`), written
  'pending' before the insert and 'done' after, is what tells them apart.
- **Events apply by date, then sequence**, as the fold does: a late day takes the next sequence.
  Measured over the whole log on 2026-10-01: **no binding has more than one event on any day**
  (233 days, 0 cases), so the order within a day never matters; the domain still defines it.
- **Lifetimes are derived in the domain** (`Sqm.Domain.Timeline.BindingLifetime`) by applying the
  events with `BindingFold` - the fold's own state machine - so a timeline cannot tell a different
  story from current state. "Active" shown is current state's; a disagreement is counted and shown.
- **Not served incomplete.** Until every day of the log and the dump are written, the timeline API
  answers 503 rather than a history with days missing.
- **Permission per kind**, as on the Relationships page: the centre needs its own lookup
  permission; numbers, SIMs and handsets are each returned only to someone who may look them up,
  and are reported as withheld otherwise. `identifier.reveal` decides masking.
- **The IMSI page's "Seen" is corrected** (approved): first seen from the history, not the earliest
  last-change date; "still active" instead of a last-change date for a SIM still bound.

## Consequences

- ~70 GiB of disk, measured by extrapolation from the prototype; the real figure is recorded after
  the backfill.
- A one-time backfill (`Sqm.Ingestion --backfill-history`), estimated at 2 to 2.5 hours, run while
  nothing imports. Resumable, month by month, reconciled against the log.
- Every daily import gains the history step. Its cost on a real day is measured after deployment;
  the import executor's one thread makes it slower than the prototype's two.
- A corrected day costs a month's rebuild instead of a day's.
- Phase 4 inherits per-binding first and last seen and event lists - the inputs to SIM-swap,
  shared-handset and rapid-change signals - without reading the event log.
- Not covered: a TAC's history. A model has up to millions of bindings; that is the device page's
  question, and Phase 5's (first seen per model).
