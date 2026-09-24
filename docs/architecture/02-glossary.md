# Glossary

**Status:** Living. Every figure below was measured against the running system on 2026-09-16, at
delivery 151 (business date 2026-07-02).
**Read after:** `01-overview.md`. Read before: `03-data-model.md`.

---

This is the vocabulary the code, the commit messages and the ADRs all use. Most of it is ordinary
data-warehouse language, but a few terms mean something specific here and one of them — *fold* —
carries a property the whole architecture rests on. Where a word has a general meaning and a
narrower one in this system, both are given.

## The path the data takes

```
daily file  →  event log  →  fold  →  current state  →  marts  →  dashboard
  ~320 MB      1.18 billion            315 million      ~98,000     one number
               events, 151 days        bindings         models
```

Everything below is a station on that line.

---

## Binding

**One phone number, one SIM and one handset, in force together.**

```
binding = (msisdn, imsi, imei)
```

This is the grain of the whole system, and the most common mistake is to read it as "a subscriber"
or "a device". It is neither. Someone with two handsets on one SIM is **two bindings**; a handset
carrying two SIMs is also two.

That is why the dashboard reports figures that look inconsistent until you know the word:

| | at delivery 151 |
|---|---:|
| active bindings | 115,151,591 |
| distinct subscribers | ~73.6M |
| distinct handsets | ~82.3M |

Bindings are never deleted. `binding_current` holds **315,343,414** rows of which
**115,151,591** are active — the rest are bindings that existed once and no longer do, which is
itself an answer to "has this number ever been in this handset".

Proven to be the natural key: **0 duplicates across 125,939,523 rows** of the initial dump.
See `03-data-model.md` §1.

## Event, and the event log

The daily file does not deliver **state**, it delivers **change**. Each row is an event carrying a
label of `add` or `remove`:

```
2026-06-29    add     3,193,220
              remove  2,875,189
```

These accumulate in `sqm.binding_event` and are **never modified or deleted** — currently
**1,177,757,019 events across 151 days**. This is the event log: the complete, immutable history.
Every other table in the analytics store can be rebuilt from it, which is what makes the rest of
the system safe to get wrong once.

## Fold

**Collapsing the event log into current state.**

A billion events answer no question directly. The fold reduces them to one row per binding, and
the rule is a single line:

```sql
argMax(label, seq) = 'add'   -- the LAST event decides, and nothing else
```

**The fold is order-independent, and this is the single most load-bearing fact in the system.**
The feed has *set semantics*: `add` and `remove` toggle a binding, and the final state depends
only on the last event, never on the path taken. Verified over **8,062,257 transitions with zero
double-adds**.

Three consequences follow, and all three are relied on elsewhere:

- A day imported out of order still converges to the right answer.
- Re-importing a day is safe.
- The fold never reads current state, so it cannot corrupt what it is updating.

What is *not* order-independent is anything built **per delivery** — see **Mart**, and
`09-import-platform.md` for the incident that made the distinction concrete.

## Current state

`sqm.binding_current` — one row per binding, saying whether it is active and which delivery last
changed it. This is what every lookup and every snapshot mart reads.

Two re-ordered copies exist beside it, holding the same rows sorted differently:
`binding_by_imsi` (ADR-008) and `binding_by_imei` (ADR-009). They are not caches and not
summaries — they are the same data, sorted for a different question. All three reconcile exactly.

## Delivery, and `seq`

One daily file is a **delivery**, and gets a sequence number:

| business date | seq |
|---|---:|
| 2026-06-29 | 148 |
| 2026-07-02 | 151 |

`seq` is what the fold compares to decide which event is "last", and it is the partition key of
every snapshot mart. Higher seq = later day; the import queue enforces that days are processed in
calendar order precisely so the two can never disagree.

## Mart

**A table built ahead of time so a common question costs nothing to ask.**

The word tends to sound grander than it is. A mart here is just a pre-aggregated table whose name
starts with `agg_`, written once during an import and read thousands of times afterwards.

Why they exist, measured on this system:

| | |
|---|---:|
| count device models from `binding_current` | **2,350 ms** over 295M rows |
| read the same from `agg_device_model` | **10 ms** over ~98,000 rows |

**There are two kinds, and confusing them is how a dashboard lies.**

### Snapshot marts

`agg_device_model`, `agg_kpi_daily`, `agg_device_daily`, `agg_dimension_daily`,
`agg_capability_daily`, `agg_device_class_daily`.

Each answers *"what did the whole network look like at delivery N"*, and each is built by
aggregating `binding_current` **as it stands when the mart runs**. That is delivery N's state only
while no other delivery has been folded around it — so these **are** sensitive to processing order.

### Change marts

`agg_change_daily`, `agg_change_summary_daily`, `agg_sim_change_daily`, `agg_device_change_daily`.

Each answers *"what changed on day X"*, is partitioned by date, and is built from that day's events
alone. They are **completely insensitive to processing order**, which is why they can be used as an
independent source of truth when a snapshot is in doubt — and they were, to find the incident in
`09-import-platform.md`.

## Partition

A physical division of a table. `binding_event` and the change marts are partitioned **by day**,
the snapshot marts **by delivery**. Two things follow:

- A date range never opens the other partitions at all. Measured on one IMSI's history: **586 ms
  across 133 days, 87 ms across 14**.
- "Re-import a day" becomes "drop that partition and rebuild it" — atomic, instant, and the reason
  every import is idempotent. A `DELETE WHERE` would be a mutation, which rewrites every part it
  touches.

## ReplacingMergeTree, and `FINAL`

The storage engine behind `binding_current`. Its rule: for each sort key, the row with the highest
version column — here `last_change_seq` — wins.

So the fold deletes nothing. It writes a new row with a higher `seq` and the engine discards the
older one on its next background merge.

**Until that merge runs, both rows are present.** `FINAL` tells ClickHouse to resolve them at read
time. Leaving it out does not error; it quietly double-counts. This project has shipped that bug
once: **251,879,046 against a real 125,939,523**.

`FINAL` costs a merge-on-read, which is why the marts pay it once per delivery and the dashboard
never does.

## Materialized view

A table kept in step automatically. Every insert into `binding_current` is mirrored into
`binding_by_imsi` and `binding_by_imei` without the ingestion code knowing they exist.

**The one way it breaks, stated so it is not discovered:** a materialized view fires on `INSERT`
and on nothing else. Rebuilding a source table by `EXCHANGE TABLES` bypasses it entirely and leaves
the copies describing the old data. Any such rebuild must be followed by
`Sqm.Ingestion --backfill-imsi` / `--backfill-imei`, whose reconciliation step is what proves it
was. See ADR-008.

## Skip index

An index that does not find rows — it rules out **blocks** of rows that cannot contain a match, so
they are never decompressed. `binding_event` carries a `bloom_filter(0.001)` on `imsi`: **1.69 GiB,
6.7% of the table**, taking one SIM's full history from **10.5–20.8 seconds to 586–683 ms**.

A bloom filter answers set membership only. It cannot help a range or a prefix, which is why IMSI
prefix search needed a re-ordered table instead and a TAC filter could not use one at all — see
ADR-008 and ADR-009 for the arithmetic.

## TAC

**Type Allocation Code** — the first eight digits of an IMEI, identifying the device *model*.
`35004012` is a Galaxy A54 5G. The GSMA database gives manufacturer, brand, marketing name, device
type, OS and radio bands for each of **270,166** of them; **97,705** appear on this network at
delivery 151.

The relationship that makes the Devices module affordable: **an IMEI's first eight digits are its
TAC**, verified on every one of the **284,341,927** well-formed rows of current state with **zero**
mismatches. A device model is therefore a *contiguous range* of a table sorted by IMEI, not a
filter over 295 million rows — which is the difference between **2,000 ms and 3 ms**.

## Device, handset, SIM, subscriber

Four words that are casually interchangeable in conversation and strictly distinct here:

| term | key | what it is |
|---|---|---|
| **device** | TAC | a *model*, e.g. Galaxy A54 5G. Not a thing you can hold |
| **handset** | IMEI | one physical unit |
| **SIM** | IMSI | one subscriber identity module |
| **subscriber** | MSISDN | one phone number |

"How many Samsungs are on the network" is a question about devices; "whose handset is this" is a
question about a handset. The permission model draws the same line — `device.view` opens the
catalogue and names nobody, while `device.identifiers` lists everyone who owns a model.

## The unknown-device bucket

`000000` is what the source writes when it does not know the handset, and roughly 3.6% of IMEIs
are malformed in other ways. Together they are **5.5 million bindings — 4.8% of the network** —
with no TAC and therefore no model.

They are shown, labelled `(unknown device)`, and never silently dropped: hiding them would
understate every device total by about a twentieth. They are the largest single row in the Devices
catalogue, and the one row there that has no detail page, because it is the *absence* of a device
rather than a nameless one.

## Quarantine, and "partially completed"

A row that fails a validation rule is **quarantined** — recorded with its rule, a sample and a
count — rather than silently dropped or allowed to fail the import.

A daily import finishes as `PARTIALLY_COMPLETED` when rows were **rejected** — not imported, and so
missing from the data. A file whose every row landed is `COMPLETED`, and if validation raised
warnings on some rows the status reads **Completed · with warnings**; the job's quarantine lists
the rules, the counts and samples. (A TAC snapshot uses `PARTIALLY_COMPLETED` differently: loaded,
not yet activated.)

> **SUPERSEDED (2026-09-24).** This used to read: `PARTIALLY_COMPLETED` when *anything* was
> rejected or warned, kept deliberately so that "if that figure ever jumps from thousands to
> hundreds of thousands, the status is what sends someone to look." It did not. Every daily file
> carries a few thousand IMEIs that are neither 14 digits nor `000000` (median 3,062 a day, about
> 0.04% of rows), so all 60 days imported through the platform read partially completed with not
> one row of 422.9 million rejected. When 25 and 26 August arrived with about 10% of IMEIs cut to
> eight digits — 793,476 and 711,620 rows, 250 times the median — the status could not get any
> worse, and nobody looked. The product owner changed the rule; migration 010 relabelled the 60
> days. The warning count is now the signal, and it needs a threshold to be an alarm.

## Related

- `01-overview.md` — what the system is for
- `03-data-model.md` — the binding, its lifecycle, and the measured quality rules
- `09-import-platform.md` — deliveries, the queue, and why days run in order
- `11-clickhouse-memory.md` — merges, partitions and what they cost on a small node
- `docs/adr/` — where each of these choices was decided, against measurement
