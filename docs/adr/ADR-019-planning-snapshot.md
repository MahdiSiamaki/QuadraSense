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
