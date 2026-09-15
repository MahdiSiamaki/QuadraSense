# ADR-009 — Devices: a model is a TAC, and an IMEI-ordered table makes it a range

- **Status:** Accepted
- **Date:** 2026-09-15

---

## Context

The product needed a Devices section: a browsable catalogue of device models, reachable by name or
by any identifier, with a detail page carrying everything known about one model.

Two questions had to be settled before any of it could be built, and the second follows from the
first.

### 1. What is a device?

The requirement listed both model-level attributes (manufacturer, brand, marketing name, device
type, OEM) and instance-level ones (IMEIs, the SIMs and numbers bound to them, first and last
seen). Those are different grains and conflating them produces a page that is wrong about both.

The data settles it:

| Concept | Key | What it is |
|---|---|---|
| **Device** | TAC, 8 digits | A device **model**. What the GSMA record describes. |
| **Handset** | IMEI, 14 digits | One physical unit. |
| **Binding** | `(msisdn, imsi, imei)` | A number + SIM + handset in force together. |

**A device is a TAC.** The GSMA export has 270,166 rows, one per TAC, and every field in it —
`manufacturer`, `modelName`, `marketingName`, `brandName`, `deviceType`, `operatingSystem`, `oem`,
`bandDetails` — is a statement about a model. There is nothing in it about an individual handset,
because there is nothing to say: handsets of one model are identical to the GSMA.

A handset reaches SIMs and numbers only through bindings, so:

- one device model has many handsets;
- one handset has many SIMs and numbers over time — SIM changes are visible in the binding log,
  and the detail page shows them;
- one SIM has been in many handsets, possibly of different models.

### 2. The relationship that makes it affordable

**An IMEI's first eight digits are its TAC.** Measured over all 295,013,916 rows of
`binding_current`:

| | |
|---|---:|
| rows | 295,013,916 |
| 14-digit IMEIs | 284,341,927 |
| malformed IMEIs (`tac = ''`) | 10,671,989 |
| `substring(imei,1,8) != tac` | **0** |
| distinct IMEIs | 120,355,763 |
| distinct TACs | 168,198 |

Zero exceptions on 284 million rows. That is what makes a device model a **contiguous range** of a
table ordered by IMEI, rather than a filter over it.

## The problem, measured

`binding_current` is `ORDER BY (msisdn, imsi, imei)`. A filter on `imei` — the third key column —
prunes nothing, and neither does one on `tac`, which is derived from it. Warm, on 25.8:

| Query | Samples | Time | Rows read |
|---|---:|---:|---:|
| exact IMEI | 4 | 1,861 / 1,966 / 1,999 / 2,085 ms | 295,013,916 each |
| one TAC | 3 | 6,488 / 6,803 / 8,906 ms | 295,013,916 each |

**Seven of seven read the entire table.** ADR-008 records how a single lucky sample nearly ended
that investigation early; this one was sampled enough to be sure.

The catalogue itself has a third shape: a per-model rollup with distinct counts, which over the
whole table costs **2.35 s** — a mart-build cost, not a keystroke cost.

## Options considered

### A. A bloom-filter skip index on `tac` — rejected without building it

The arithmetic decides it. The table is ordered by MSISDN, so a TAC's rows are scattered
uniformly. The most populous TAC holds 296,686 of 295,013,916 rows — 0.1% — and at 8,192 rows per
granule the chance a given granule contains none of them is `0.999^8192 = 0.03%`. The filter would
select essentially every granule and cost over 100 MiB to do it.

This is the opposite of the IMSI case, where a bloom filter worked well for exact lookup: an IMSI
appears in a handful of rows, a TAC in hundreds of thousands.

### B. A projection ordered by IMEI — rejected on a hard constraint

The same one ADR-008 hit: ClickHouse does not use projections for queries with `FINAL`, and every
read of current state needs `FINAL`. It would be built, stored and never used.

### C. A second table ordered by IMEI, plus a per-model mart — chosen

Two structures, because there are two shapes of question.

**`sqm.binding_by_imei`** — `ReplacingMergeTree(last_change_seq)` ordered by
`(imei, msisdn, imsi)`, kept in step by a materialized view on `binding_current`, exactly as
`binding_by_imsi` is. `ORDER BY (imei, …)` and not `(tac, imei, …)`: a TAC *is* an IMEI prefix, so
one key serves both, and a leading `tac` column would add eight bytes per row to repeat what the
next column already says. Cost **6.5 GiB**, against 905 GiB free.

**`sqm.agg_device_model`** — one row per model per delivery: bindings, handsets, SIMs,
subscribers, first seen, last seen. 97,903 rows, built with the other marts on every delivery.

## Results

Warm, five runs each, median reported:

| Query | Before | After |
|---|---:|---:|
| exact IMEI | 1,861–2,085 ms, 295,013,916 rows | **3 ms, 8K–24K rows** |
| one model's identifiers | 6,488–8,906 ms, 295,013,916 rows | **50–71 ms, 614K–786K rows** |
| catalogue list, page 1 | — (2.35 s aggregate) | **75 ms, 368K rows** |
| catalogue, filtered | — | 59 ms |
| catalogue, text search | — | 67 ms |
| catalogue, page 51 | — | 85 ms |
| device detail | — | 31 ms |
| timeline, all 133 days | — | 35 ms |
| timeline, 30-day window | — | 11 ms |

**Exact IMEI is ~620× faster and reads 12,000–36,000× fewer rows.** A model's identifier list is
~130× faster.

Through the API, end to end: catalogue 118–140 ms, detail and timeline under 60 ms, a page of
identifiers 142 ms.

### Where the list's 368,110 rows go, and why they stay

Every catalogue query reads 368,110 rows: 97,903 from the mart plus the 270,166-row TAC
dictionary it joins. Taken apart:

| | Best of 3 | Rows |
|---|---:|---:|
| mart alone, no join | 10 ms | 97,905 |
| + join `sqm.tac` | 50 ms | 368,072 |
| + both joins (what ships) | 60 ms | 368,110 |

The join costs ~50 ms. It was kept rather than denormalised into the mart, because `sqm.tac` is a
view over the **active** GSMA version: the catalogue follows a TAC activation or rollback with no
mart rebuild, and 60 ms is well inside what an interactive list can spend. Denormalising is the
fallback if the model count grows by an order of magnitude.

## The mart replaced work rather than adding it

`agg_dimension_daily` already held a per-TAC slice, aggregated from `binding_current` on every
delivery. Building `agg_device_model` beside it would have meant two full aggregates computing the
same per-TAC counts from the same source — the sort of duplication that eventually disagrees.

So the refresh script now builds `agg_device_model` in one pass and **derives** the dimension
mart's `tac` slice from it, reading 97,903 rows instead of 295 million. The delivery does strictly
less work than before this module existed.

That is also why the distinct counts use `uniq()` and not a better estimator: this table now feeds
the dashboard's TAC breakdown, and a different estimator would silently move numbers already on
screen. Two estimates that disagree about one population are worse than either of them.

## Device imagery: curated here, because there is none to import

The question "where does the picture come from" has a real answer and it is not obvious.

**The GSMA TAC database has no imagery.** All 26 columns were checked: `manufacturer`,
`modelName`, `marketingName`, `brandName`, `allocationDate`, `lastUpdatedDate`, `organisationId`,
`deviceType`, `bluetooth`, `nfc`, `wlan`, the three IMS columns, the four UICC/eUICC columns,
`networkSpecificIdentifier`, `ntnConnectivity`, `simSlot`, `imeiQuantity`, `operatingSystem`,
`oem`, `bandDetails`. Not one is an image, a URL, or a reference to one.

**And the deployment is internal-network-only** (ADR-006), so a device-catalogue CDN is
unreachable rather than merely undesirable.

The product owner chose **administrator upload with a drawn placeholder**. Images live in
`catalog.device_image` in PostgreSQL as `bytea`, capped at 512 KB, behind a new
`device.image.manage` permission.

`bytea` and not the import file store: that store is right for multi-gigabyte source files and
wrong for these. A photograph is tens of kilobytes, there will be hundreds rather than hundreds of
thousands, and holding the bytes in the row keeps them atomic with their metadata — no orphaned
files, no rows pointing at files a restore did not bring back.

**The placeholder is drawn, not fetched.** It stays crisp at any size, follows the theme, costs no
request, and is informative: the silhouette is the device's own type, so a tablet, an IoT module
and a wearable are distinguishable in a list where most rows will have no photograph for a long
while.

**The content type comes from the file's magic bytes, never from the upload's header.** This
endpoint stores something the server later hands back with that type on it, and trusting the
client's word about it is how an image upload becomes a way to serve arbitrary content from your
own origin. Verified: a PE binary sent as `image/png` is refused.

## Access control: four permissions, not one

They carry genuinely different risk and an organisation should be able to separate them.

| Permission | What it opens | Granted to |
|---|---|---|
| `device.view` | The catalogue. Models, capabilities, populations. Names nobody. | everyone, incl. Viewer |
| `lookup.imei` | One handset → its SIMs and numbers. | analyst, data_operator, administrator |
| `device.identifiers` | **Every** identifier of a model. Bulk. | analyst, administrator |
| `device.image.manage` | Upload or replace a photograph. | data_operator, administrator |

`device.identifiers` is deliberately not folded into `lookup.imei`. Resolving one IMEI exposes one
person's handset; listing a model's identifiers exposes everybody who owns that model — 208,895
handsets for the most populous one. Those are different acts even though they read the same table,
and a permission model that cannot tell them apart is not describing the risk.

It is also the first grant where `data_operator` is deliberately narrower than `analyst`: getting
files in and marts rebuilt needs no list of two hundred thousand people's handsets.

Verified against the running system with an account holding `device.view`, `lookup.imei`,
`device.identifiers` and `lookup.imsi` but **not** `identifier.reveal`, `lookup.subscriber` or
`device.image.manage`:

- identifiers returned masked — `35004012****** | 43211******5470 | 912*****28`;
- an MSISDN search refused with the permission named, not an empty list;
- an IMEI search allowed and resolved to the right model;
- an image upload refused with 403.

Every one of those, success and refusal alike, is in `audit.event` with the actor, the model and
the row count — and never with an identifier.

## Consequences

**The two tables must not drift.** A materialized view mirrors every insert into
`binding_current`, and the daily fold is an insert, so they converge without the ingestion code
knowing this table exists. The one way it breaks is the one ADR-008 states: **an MV fires on
INSERT and on nothing else**, so a rebuild by `EXCHANGE TABLES` bypasses it entirely. Any such
rebuild must be followed by `Sqm.Ingestion --backfill-imei`.

**The backfill is shared with ADR-008's.** `ImsiBackfill` became `CurrentStateBackfill` with a
target descriptor, because both tables exist for the same reason and fail in the same four ways.
The IMEI run copied 295,013,916 rows in 629 s with zero chunk failures, compacted 301 parts to 5,
and reconciled exactly: **0 rows difference, 0 active difference**.

**The unknown-device bucket is in the catalogue, labelled.** 5,557,995 bindings whose IMEI
identifies no model — 4.9% of the network. It is the largest row in the list. It has no detail
page, because it has no TAC, and its identifiers cannot be listed, because a prefix range needs a
prefix. Hiding it would understate the catalogue by a twentieth; showing it unlabelled renders the
largest row as a line of blanks.

**Search accepts five kinds of input and decides by length.** TAC 8, MSISDN 10, IMEI 14, IMSI 15 —
four distinct lengths, which is a property of *this feed* and not of GSM in general. It works
because no IMEI here is 15 or 16 digits, which was measured. `DeviceSearchTermTests` is where that
assumption should break first if the feed ever changes.

**A new schema needs a new grants file, and that was discovered the good way.** Migration 005
created `catalog.device_image` and everything worked against it — as the schema owner. The first
request through the running API returned 500: `permission denied for schema catalog`. The
application connects as `sqm_app`, which holds USAGE on `imports`, `auth` and `audit` and nothing
else. That is least privilege working exactly as designed, and it surfaced as a 500 on the first
request rather than as an audit finding that the application had been running as owner all along.

**No virtualisation, despite the requirement mentioning it.** The server pages at 40 rows and caps
at 100, so the DOM never holds more than a hundred rows of a plain table. What made the list fast
was the mart — 97,903 rows instead of a 2.35 s aggregate over 295 million — not the rendering. A
virtual scroller would add a scroll-position bug surface to solve a problem pagination has already
solved.

## Related

- `db/analytics/migrations/019_device_module.sql` — the structure, with these measurements in it
- `db/operational/migrations/005_device_module.sql` — permissions and the image catalogue
- `db/operational/grants/005_device_catalog.sql` — why the first request returned 500
- `backend/src/Sqm.Ingestion/CurrentStateBackfill.cs` — shared with ADR-008
- `backend/src/Sqm.Domain/Identifiers/DeviceSearchTerm.cs` — the length rule and why it holds
- `docs/adr/ADR-008-imsi-search.md` — the same problem one column over
- `docs/architecture/11-clickhouse-memory.md` — the merge cap that let the backfill finish
