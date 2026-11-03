# ADR-019: Network planning reads a per-delivery snapshot the worker builds when idle

- **Status:** Accepted
- **Date:** 2026-10-27
- **Decided by:** the product owner chose, on 2026-10-27: number lists only through a controlled
  export built first; per-number trends forward from the first run, with model-level trends
  recomputed for the deliveries the marts hold; LTE-M / NB-IoT ready but apart; "not stated" never
  "no", in the new views and the dashboard alike. The snapshot's shape follows from those and from the
  measurements here.

## Context

Network planning (N1-N6, `docs/research/2026-10-feature-research.md`) asks questions of two kinds:

- **About models**: which bands, 5G or not, eSIM or not, satellite or not. The per-model marts answer
  them: `agg_tac_population` holds, per delivery and model, the bindings and the uniq() states of
  numbers and handsets, and a filtered figure is a merge of those states (ADR-017).
- **About numbers' whole sets of handsets**: "every one of a number's active handsets lacks LTE". No
  per-model figure answers it - a number with a feature phone and a smartphone is in both models'
  figures, and is not exposed. It needs every active binding grouped by number.

Grouping current state by number is a read of `binding_current FINAL`, 120 million live bindings in
some 800 million stored versions; the same read took 100 s alone and 183 s on the dashboard on
2026-10-19 (ADR-017).

## Options

| | What | Against it |
|---|---|---|
| A. Query time | Group `binding_current` by number per request | 100 s and more per view; the API's budget is seconds |
| B. In the dashboard rebuild | Another statement in `refresh_marts.sql` | The rebuild is already 31 statements and ~45 minutes on the laptop, during which the dashboard serves the previous delivery; a per-number pass with arrays of handsets would lengthen every rebuild and widen that window |
| **C. An idle-time snapshot** | The worker builds it chunk by chunk when it has nothing to import, as it builds the risk snapshot (ADR-014) | Its figures appear after the dashboard's, not with them |

## Decision

**C.** Analytics migration 030: `planning_run` (the ledger), `planning_number` (one row per number:
its sunset and eSIM classes, the reason it is not assessable, its models), `planning_breakdown` (per
class and model: the uniq() states of the numbers and handsets holding it), `planning_iot_device`
(per IoT device, the SIMs and numbers it has carried) and `planning_summary` (every published run's
finished figures, kept for the trend). `Sqm.Ingestion.Processing.PlanningSnapshot` builds a run:

- **As of a delivery.** The delivery current state holds - the latest by date - and abandoned if
  another is folded before publication, because an earlier delivery's state cannot be recovered.
- **One chunk per idle moment**, by number range for the per-number tables and by IMEI range for the
  IoT devices, never beside an SQM import, under the import's limits.
- **Published only when whole**: every chunk written, no number or device twice, and the inputs - the
  delivery, the binding history's day ledger, the GSMA version, the radio parser, the definition -
  unchanged.
- **Two runs keep their rows**; every run keeps its summary.

The classes are decided from the radio profiles of migration 029 (`19-network-planning.md` §1-3),
and a number is in exactly one class: a number is in one chunk, so numbers and bindings per class are
exact counts, and handsets are merged uniq() states (estimated, labelled so).

## Consequences

- A view answers from a few rows of the summary, or a merge of the breakdown's states when narrowed.
- Per-number trends start with the first run; the model-level trend covers every delivery
  `agg_device_model` holds.
- Each run holds a row per active number; two are kept (sizes measured below).
- The API reads only; until a run is published the views answer 503 and say why.

## Measured on the real data

The laptop, 2026-10-27: delivery 246, GSMA version 1595 (271,830 TACs, 709 with a band token no rule
reads), 32 number chunks and 32 IMEI chunks, the worker's limits (1 thread, 1.12 GiB per statement).

**The first run failed every number chunk** at 1.25 GiB: the dual-SIM pairing compared every handset
of a number with every other inside one array expression, which grows as the square of the handsets -
and a few numbers hold thousands (SIMs listed under many numbers, shifted IMEIs). The chunk now writes
its distinct (number, IMEI) rows to `planning_handset_stage` once and pairs by a join on each
unassessable handset's four candidate partners. The second run:

| Statement, per chunk | Mean | Slowest | Peak memory |
|---|---|---|---|
| Stage: current state, one row per (number, IMEI) | 17.5 s | 50.0 s | 352 MiB |
| Numbers: pair, class, write | 9.9 s | 37.5 s | 415 MiB |
| Breakdown: per class and model | 3.2 s | 8.7 s | 195 MiB |
| IoT devices, per IMEI chunk | 42.9 s | 266.3 s | 493 MiB |
| Publish: the summary | 7.1 s (once) | | 84 MiB |

The whole run: **41.1 minutes**, one chunk per idle moment. One run holds 75,393,005 number rows
(513 MiB), 2,322,089 breakdown rows (876 MiB - the uniq() states), 15,130,125 IoT devices (200 MiB):
about 1.6 GiB, 3.2 GiB for the two runs kept.

What it found (`PlanningRealDataTests`, which also checks that no number is in two classes and that
the summary is the rows'):

| Class | Numbers | Share |
|---|---|---|
| Ready: LTE or 5G | 65,241,415 | 86.54% |
| Ready: LTE-M / NB-IoT only | 15,000 | 0.02% |
| Exposed: 2G/3G only | 9,570,153 | 12.69% |
| Not assessable | 566,437 | 0.75% |
| - an unknown device (000000 or malformed IMEI) | 347,684 | |
| - a shifted IMEI (feed defect) | 160,016 | |
| - a TAC the catalogue does not hold | 41,977 | |
| - a model whose record lists no band | 16,760 | |
| **All numbers with an active binding** | **75,393,005** | |

The dashboard's KPI for the same delivery, a HyperLogLog estimate from another pass, is 75,705,850:
0.41% from the exact count. eSIM: 6,433,907 numbers hold an eSIM-capable handset (8.53%), 54,836,527
none with every handset known, 14,122,571 at least one not stated.

The research note's 14.9 M (bindings on a handset without LTE) and 18.8% (bindings on a 5G-capable
handset) counted bindings from the dashboard marts, before the strict definitions of 2026-10-27 - "not
stated" was then "no", and 5G did not need an NR band. They are a different unit and a different rule,
not this view's figures, and were never targets: 10,311,936 bindings are on numbers in the exposed
class here.

## Addendum: handset lifecycle (2026-10-28)

N4 joined the run as two more passes (analytics migration 031, `DefinitionVersion` 2): **Lifecycle** by
ranges of numbers and **Device age** by ranges of IMEIs, each written as exact counts. The idle-time
snapshot was the only place it could go: a number's whole handset history is grouped per number, as
the sunset class is, and over the binding history rather than current state. The rules for a change
are the product owner's, from measurements (`19-network-planning.md` §6). On the laptop the lifecycle
adds about 55 minutes to a run and device age about 8, every statement under the 1.12 GiB limit -
after three statement shapes were measured and rejected for it; the run now builds 128 parts.

## Addendum: every number's models, and the band view's figures (2026-11-02)

Two backlog items were built together, as `DefinitionVersion` 3 (analytics migration 032).

**Lists of any class, narrowed by model.** The run kept each number's models only for the classes that
store them on their own row (9.6 M numbers). So a list of ready or eSIM numbers narrowed by vendor,
model or type was refused, and so was a campaign list. Each number chunk now also writes
`planning_number_model`: one row per (number, model), with the number's classes. It is merged to one
part per chunk, and a check fails the chunk unless the index holds exactly the chunk's numbers.

The index is ordered **by number**, not by model as the backlog sketched. A list is paged by number,
and a vendor is thousands of models: a Samsung filter selects 14,516 TACs on the network at delivery
246. Ordered by number, a class's numbers arrive in page order, so a page stops after its last
number. Ordered by model, the same list would read thousands of ranges and then sort them. That order
was neither built nor measured.

**The band view, written at publication.** Unnarrowed, the view merged every LTE and NR model's uniq()
states on each request: 33 s on the laptop, 42–48 s on the department VM. The worker now writes those
figures once, when the run publishes, and adds a last marker row. The page reads them. It falls back
to the live read when the figures are missing or carry no marker. The match against the operator's
bands stays live, because it is configuration.

Measured on the laptop, delivery 246:
- **The index:** 116,420,620 rows, 632 MiB. Each chunk is written in 4.3 s and merged in 1.2 s,
  under 390 MiB.
- **The figures:** written in 41.9 s (381 MiB). Read in 0.2 s, against 33 s live.
- **Narrowed lists**, for the ready class narrowed to Samsung (37.0 M numbers):
  - a page: 0.4 s at 77 MiB;
  - the count: 10.6 s at 95 MiB, once per list;
  - the whole list, streamed: 48 s at 166 MiB.

**Four traps, each measured before the work was let stand.** `19-network-planning.md` §3 has the
figures.
1. **In-order aggregation for the write:** 486–754 s per chunk, about 4.5 hours a run.
2. **`groupArray` over a whole class:** it reached the API's cap within 5.5 M rows.
3. **ClickHouse's read-ahead before an in-order merge:** a page read the whole run.
4. **Four or five parts per chunk:** the count needed 20 s and 323 MiB.

The laptop's run was stopped, fixed and resumed. Chunks already written stand, and an interrupted
chunk is rebuilt from its partition. Its index was merged to one part per chunk by hand, as the
worker now does.

**Verified on the department VM, 2026-11-03,** on the first run the worker wrote alone (delivery
236). It abandoned the stalled run of definition 2, because the fingerprint changed. The index holds
exactly the run's 74,786,228 numbers, one part per chunk. Definition 3 added 3.7 minutes to the run's
2 hours 20 minutes of statements. Every narrowed read stayed under 165 MiB, and a campaign list
streamed exactly its count. `19-network-planning.md` §3 and §5 have the figures.
