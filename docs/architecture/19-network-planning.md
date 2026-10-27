# Network and product planning - what the device catalogue can say, and what it cannot

**Status:** Being built, in pieces, from 2026-10-27. This document grows with each piece; a section
says plainly when its piece is not built yet.
**Read with:** `docs/research/2026-10-feature-research.md` (where N1-N7 were proposed, and the product
owner's answers), `03-data-model.md` (the binding, and why a number is not a subscriber),
`14-relationship-explorer.md` (the dual-SIM pair rule), `15-feed-quality.md` and
`docs/discovery/05-shifted-imei-verification.md` (the feed defects every count here must set apart).

---

## What is being built

Six views for network and product planning, asked for on 2026-10-27:

| | View | Built |
|---|---|---|
| N1 | 2G/3G sunset readiness: numbers every one of whose active handsets lacks LTE | 2026-10-27, section 3 |
| N2 | 5G and band readiness against the operator's own bands | 2026-10-27, section 5 - the operator's band list is not configured yet |
| N3 | eSIM readiness | 2026-10-27, section 3 |
| N4 | Handset lifecycle: device age, model age, upgrade cohorts, model-to-model moves | not yet |
| N5 | IoT / M2M fleet | 2026-10-27, section 3 |
| N6 | Satellite (NTN) devices | 2026-10-27, section 5 |

Every one of them is a statement about **device models** joined to the population that holds them.
So the first piece is the device model's side: what its GSMA record says about its radios, read once
and correctly.

The product owner's decisions of 2026-10-27, which every piece follows:

- Lists of numbers for a campaign leave the system only through a **controlled export** - its own
  permission, a recorded reason, and a second person's approval above a size threshold - built before
  any list export.
- Trends that need the state of every number per delivery start with the first planning run; trends
  that the per-model marts can answer are recomputed for the deliveries those marts hold.
- A device whose only LTE is **LTE-M or NB-IoT** survives a 2G/3G shutdown and is reported on its own
  row, not in the sunset group.
- **"Not stated" is never "no"**, in the new views and in the existing dashboard alike (below).

## 1. The radio profile (built 2026-10-27)

`sqm.tac_radio` (analytics migration 029): one row per TAC per GSMA version, parsed by
`Sqm.Domain.Devices.RadioProfileParser` when a TAC file is loaded, and - if the active version predates
the parser, or the parser has changed (`RadioProfileParser.Version`) - before the next dashboard
rebuild. `sqm.tac_radio_active` is the active version's profiles, as `sqm.tac` is its records.
`Sqm.Ingestion --parse-tac-radio [--version N]` parses one by hand.

### The field has a grammar

`bandDetails` is not free text. Measured on the 2026-10 catalogue (271,830 TACs), it is sections
separated by `" | "`, each a label, a colon and a comma-separated list:

| Section | TACs | What it holds |
|---|---:|---|
| `Radio Interface` | 271,824 | `NONE`, `GPRS`, `EDGE`, `CDMA`, `3GPP2`, `SATELLITE` |
| `2G/3G` | 259,842 | `GSM 900`, `GSM850 (GSM800)`, `WCDMA FDD Band 1`, `TD-SCDMA`, `CDMA2000` |
| `4G` | 130,340 | `LTE FDD BAND 3`, `LTE TDD BAND 40` |
| `4G Cat`, `4G CA …`, `4G IB CA …`, `4G DC …` | up to 85,015 | LTE categories and carrier-aggregation combinations |
| `5G NR` | 37,163 | `n78` |
| `5G NA Options` | 36,286 | 3GPP architecture options: `5G-2Series`, `5G-3Series`, `5G-5Series`, `5G-7Series` |
| `5G DC BC EN-DC …`, `5G NR CA …` | up to 28,707 | NR band combinations; EN-DC and NE-DC are non-standalone |
| `LPWAN` | 4,316 | `Cat-NB1`, `Cat-M1`, `Cat-NB-LTEFDD LTE FDD BAND 20`, `EC-GSM-IoT` |
| `5G REDCAP` | 366 | NR RedCap bands |
| `SATELLITE` | 1,294 | `L-Band (1-2 GHz)`, `S-band (2-4 GHz)` |
| `NTN` | 963 | `E-UTRA B255`, `NR FR1 n256` |

The parser reads the grammar rather than searching the text, and keeps any token no rule recognises
in `unparsed` with its section - an ambiguity is counted, never turned into a band or dropped.

### What each answer means

| Column | Meaning | Not stated when |
|---|---|---|
| `sunset` | What survives a 2G/3G shutdown: **LTE** (a `4G` band or category, or NR), **LTE-M / NB-IoT only**, **2G/3G only** (GSM, WCDMA, TD-SCDMA, CDMA, EC-GSM-IoT) | the record lists no radio at all (13 TACs) |
| `lte` | Any LTE, LTE-M and NB-IoT included | the same |
| `five_g` | An NR band (RedCap included) | the same |
| `sa` | Standalone 5G, only as the record states it: `5G-2Series` | always, unless stated |
| `nsa` | Non-standalone 5G as stated: option 3, 4 or 7, or an EN-DC / NE-DC band combination | always, unless stated |
| `lte_bands`, `nr_bands`, `lte_m_bands`, `nb_iot_bands`, `umts_bands`, `gsm_bands` | Band numbers, normalised (`B3` is 3, `n78` is 78; GSM in MHz) | - |
| `esim` | An eUICC: either eUICC count is one or more | either count is `00` or blank and neither is positive |
| `ntn` | 3GPP NTN, from `ntnConnectivity`; `ntn_mode` NTN only or NTN and TN, `ntn_orbits` GSO / NGSO | "Not Known" - 88.8% of TACs |
| `satellite_listed` | A satellite radio in the band list - not the same claim as 3GPP NTN, and kept apart | - |
| `ims_emergency` | Emergency calls over IMS. **Not VoLTE** | "Not Known" |
| `allocation_date` | When the GSMA allocated the TAC - not a release or manufacture date | unparseable |
| `device_class` | The dashboard's device-mix class, now in one place | - |

Two facts behind those rules:

- **`00` is "not stated".** The GSMA count fields (eUICC, SIM slots, IMEI count) carry `00` where a
  record predates the field: 99,298 TACs have it in all four at once; up to 2018 nearly every
  allocation does (12,590 of 13,053 in 2015), from 2019 almost none (52 of 11,158 in 2026).
- **SA and NSA come from the record, and from two places in it.** Galaxy A54 (35004012) lists only
  `5G-5Series` as its architecture option - LTE on the 5G core - and dozens of EN-DC combinations,
  which are non-standalone by definition. Reading the options alone would call it "no NSA".

### What the catalogue does not carry

- **VoLTE.** Two records of 271,830 mention it. The IMS columns describe emergency calling. N1 shows
  VoLTE as "no data", not as a number.
- **Whether a SIM is physical or an eSIM.** Nothing in the feed says, so N3's "eSIM-capable handset on
  a physical SIM" is "no data".
- **Trackers.** No device type is a tracker; module, modem, IoT device and vehicle types are.

## 2. The existing dashboard, corrected (2026-10-27)

`sqm.tac_capability` - what the dashboard's capability widget and the device page read - is now built
from the radio profiles, and its flags are 1 / 0 / 2 like `ims_emergency` has always been. The
capability mart counts a 2 as **unknown**, beside the unknown and unregistered devices it already
counted there.

Two figures change, by the owner's decision:

- **eSIM unsupported** shrinks and **unknown** grows by the bindings whose record says `00`.
- **5G supported** loses the records that list an architecture option and no NR band - about a
  thousand TACs, feature phones among them.

The device-class mapping, written out three times in `refresh_marts.sql`, is now
`RadioProfileParser.DeviceClassOf`, stored with each profile; its output is unchanged.

**Proved by failing first:** reading `00` as zero, and a `5G NA Options` section as NR, each failed
the parser's tests (4 failures); the refresh's own capability statements, run against a scratch store
with the previous SQL in place, read the feature phone's option as 5G (6 supported where 3 are) and
failed `TacRadioStoreTests`.

## 3. The planning snapshot (built 2026-10-27)

"Numbers every one of whose active handsets lacks LTE" is a question about each number's whole set of
handsets. No per-model mart can answer it: a number holding a feature phone and a smartphone is in
both models' figures, and is not exposed. So it needs every active binding grouped by number -
`binding_current FINAL`, about 120 million bindings - which the API cannot read per request (the
same read took 100 s and more on 2026-10-19, ADR-017).

The import worker builds it instead, the way it builds the risk snapshot: `PlanningSnapshot`, one
chunk of the number range per idle moment, never beside an SQM import, published only when every
chunk is in, no number appears twice and nothing it read has changed (analytics migration 030;
ADR-019). `Sqm.Ingestion --refresh-planning [--force]` builds one to the end from the shell.

### One delivery's state

A run describes the delivery whose state current state holds - the latest by date - and is abandoned
if another delivery is folded before it is published, for the reason `refresh_marts.sql` gives: the
state of an earlier delivery cannot be recovered afterwards. So **per-number trends begin with the
first run** (the product owner's decision); each published run leaves its finished figures in
`planning_summary`, kept when its rows are dropped. The two newest runs keep their per-number rows.

Trends that the per-model mart can answer are drawn beside them for every delivery it holds
(`agg_device_model`, 67 deliveries on the laptop): bindings per model class under **today's**
catalogue - bindings only, because distinct counts do not add up across models.

### The units, the denominator, and what "active" means

| Unit | Is | Counted |
|---|---|---|
| Number | an MSISDN with at least one active binding | exactly, in the summary; merged estimates when a view is narrowed |
| Binding | one number, one SIM, one handset together | exactly |
| Handset | an IMEI - a dual-SIM phone has two | estimated (uniq, ~0.5%) |

**Active** means the operator's last event for the binding, up to the delivery, was an add - the fold
of `binding_current`. What a remove means to the operator is still an open question
(`docs/discovery/02-open-questions.md`).

**Every share is of the numbers with an active binding at the delivery.** A view narrowed to a
vendor, model or device type counts the numbers holding such a handset - whose class is still decided
by all their handsets - from the per-model uniq() states, and says so.

### How a number is classed

| Sunset class | When |
|---|---|
| Ready: LTE or 5G | one handset has LTE or NR |
| Ready: LTE-M / NB-IoT only | none has, one has LTE-M or NB-IoT (the owner's decision: ready, reported apart) |
| Exposed: 2G/3G only | every handset is known and lists only 2G/3G |
| Not assessable | no LTE known, and at least one handset that cannot be assessed |

A number is not assessable for the first reason that applies: a **shifted IMEI** (the feed defect of
2026-09-15, `docs/discovery/05-…`: usually another subscriber's handset, so it is not guessed either
way), an **unknown device** (`000000` or not 14 digits), a **TAC the catalogue lacks**, or a model
whose record **lists no band**.

**Dual-SIM pairs** settle one case and only one. An IMEI that cannot be assessed, whose partner by
the documented rule (`14-relationship-explorer.md`: the same TAC and a serial one apart, or a TAC a
hundred apart and the same serial) is active on the same number with a known profile, is the same
handset's other radio, and takes its partner's profile. Galaxy A54's twin TAC is not in the catalogue
at all; without this every A54 number holding both radios would be not assessable. Pairs never merge
handsets in a count.

eSIM is classed the same way: an eSIM-capable handset; none, every one known; or not stated.

### What each view shows

- **2G/3G sunset (N1):** the four classes and the reasons; inside the exposed class, the numbers whose
  every handset is GSM-only (stranded by a 2G switch-off too) and those holding an IoT device; the
  handsets resolved through their pair; who holds which model, by vendor, model, device type or TAC;
  the numbers themselves, a page at a time. **VoLTE: no data** - the catalogue has no such field.
- **eSIM (N3):** the three classes, the models, the trend. **Physical SIM versus eSIM: no data** - the
  feed does not say which a SIM is.
- **IoT fleet (N5):** IoT / M2M devices (the dashboard's class: modules, modems, dongles, routers, IoT
  devices, vehicle units), by device type, by 2G-only / 2G-3G / LTE-M-NB-IoT / LTE exposure, by vendor
  of module, and how many SIMs each has carried since the initial dump (the binding history).
  **Trackers: no data** - the catalogue has no such type.

### Permissions

`planning.view` (operational migration 020) for Viewer, Analyst and Administrator: the figures name
nobody. A page of numbers also needs `lookup.subscriber`, is masked without `identifier.reveal`, and
is audited with its class and filters, never a number; an aggregate CSV needs `data.export`. Pages
reach the first 10,000 numbers and no further: **a whole class as a campaign list** is the
controlled export's (section 4).

A list narrowed by vendor, model or device type is kept for the **exposed, LTE-M / NB-IoT-only and not
assessable** classes only: the run stores each number's models for those classes (9.6 M numbers), not
for the 65 M ready ones. A narrowed list of ready or eSIM numbers is refused with that reason - an
empty list would say no such number exists. The narrowed sunset view still shows the classes, from the
per-model breakdown; its handsets-assessed-through-a-pair figure is shown unnarrowed only.

## 4. Campaign lists - the controlled export (built 2026-10-27)

The product owner's decision: a list of numbers may leave the system for a campaign, with controls.
Operational migration 021 holds the requests (`campaign.export_request`); Settings > Campaign lists
shows them.

1. **Ask.** `campaign.export` (with `lookup.subscriber`) asks for one class of one planning run - the
   class on screen, with its filters - and states a reason of 10 to 1,000 characters. The server
   counts the list with the same condition the download will read, so the size on the request is the
   size of the file.
2. **Approve.** A list of more than `CampaignExport:ApprovalThreshold` numbers waits for someone with
   `campaign.approve` - never its requester: the store's conditional UPDATE refuses it and a CHECK on
   the table (`ck_export_four_eyes`) would refuse it again. The threshold is **0** by default, so
   every list waits for a second person until the owner sets a size.
3. **Download.** The requester, once approved, for `CampaignExport:ValidHours` (72). The file is
   streamed line by line from the run it was counted on - never held whole in memory - and refused
   (410) once that run no longer holds its numbers. Numbers are masked without `identifier.reveal`.

Every request, decision, withdrawal and download is audited with the request's id, count, class and
reason - never a number. Both permissions go to Administrator only; the owner grants them further.

## 5. Bands and 5G (N2) and satellite (N6) - per model (built 2026-10-27)

Both are per-model views. They read the delivery's per-model mart (`agg_tac_population`, the run's
delivery) with each model's radio profile, so they are shown for every run. **The unit is the
model**: a number holding two models is counted under each, the rows of a block need not add up, and
numbers and handsets are uniq() estimates. Shares are of every number with an active binding at the
delivery, exactly, from the run's summary.

**Reading uniq() states within the API's 476 MiB cap** took three rules, each measured on delivery
246 on 2026-10-27:

- *No state passes through a join.* Joined to the catalogue for its names, the IoT fleet by vendor
  took 1.84 GiB and the four NTN classes 796 MiB; with each model's profile looked up by TAC instead
  (`transform` over arrays built once), 380 and 386 MiB, and the LTE bands 382 MiB. Turning off
  ClickHouse's join side-swapping changed nothing.
- *Read by the mart's key.* The mart is ordered by (delivery, TAC), so a condition on the models skips
  every other model's states: 33-46 MiB read. The run's own `planning_breakdown` gives the same
  figures - the NTN classes agree to the binding - but is ordered by class first, and the IoT fleet
  read all 447 MiB of it and used 597 MiB.
- *A breakdown merges only its page.* It first finds its largest groups by active bindings - an exact
  sum that reads no state - then the numbers and handsets of those groups' TACs alone, ordered by
  numbers. The eSIM-capable models by TAC fell from 4.3 s to 2.0 s.

The peaks are 380-434 MiB - the largest a breakdown by vendor whose page holds the biggest vendors -
up to 91% of the cap; every view answers within 33 s (bands, the slowest). They are the first thing to
measure again when the catalogue or the network grows.

### Bands and 5G

- **Every band's population.** For each LTE and NR band any model on the network lists: the numbers,
  bindings and handsets of those models.
- **Against the operator's bands.** Each LTE model meets the operator's LTE bands in exactly one way -
  every one, some, none, or *LTE stated with no band listed*, which cannot be matched - and each 5G
  model the NR bands likewise.
- **SA and NSA, as stated.** Among 5G models (an NR band listed, section 2): both stated, only SA
  (`5G-2Series`), only NSA (options 3, 4, 7 or an EN-DC/NE-DC combination), neither. Nothing is
  inferred from the bands.
- **Ambiguous.** The numbers holding a model with a band token no rule recognised (709 models in GSMA
  version 1595), kept as written.
- **A band in use: no data.** The files say which handset a number uses, never the band it used.

The operator's bands are configuration, `OperatorBands:Lte` and `OperatorBands:Nr` (band numbers: 3
for LTE band 3, 78 for n78; as environment variables `OperatorBands__Lte__0=3`, `OperatorBands__Lte__1=...`).
**They are empty, and must stay so until the operator supplies them**: a guessed list would turn every
match into a claim. Empty, nothing is matched - against an empty list every model "has all" its
bands, so the match is never computed - and the view says the list is not configured; an invalid list
(LTE 1-255, NR 1-1024) is ignored the same way, with the reason.

### Satellite

From `ntnConnectivity` and the band list, four classes per model: **3GPP NTN capable**; **a satellite
radio listed, not 3GPP NTN** (L-band, S-band, Inmarsat, BGAN - another technology); **stated: no
NTN**; **not stated** (88.8% of TACs say "Not Known"). Unknown devices and TACs the catalogue lacks
are counted apart. NTN models are broken down by mode (NTN only, or NTN and the terrestrial network),
orbit (GSO, NGSO) and NTN band (E-UTRA B255/B256, NR n255/n256), with bindings per class at every
delivery the per-model mart holds. **Satellite service in use: no data** - capability is not a
subscription.
