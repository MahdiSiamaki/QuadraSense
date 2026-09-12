# Phase 0 — Discovery: Data Profiling Report

**Date:** 2026-09-11
**Scope:** `D:\SQM\1 Month`, `D:\SQM\2026-05-13`, `D:\SQM\2026-05-16`, `D:\TAC`
**Method:** Full-scan profiling with DuckDB 1.5.5 over Parquet projections, plus deterministic 1/64 sampling for event-replay analysis. No file was loaded into memory in full.

> All numbers below are **measured**, not estimated, unless explicitly marked "est.".

---

## 1. Inventory

| Dataset | Path | Files | Bytes | Rows |
|---|---|---:|---:|---:|
| Initial full dump | `D:\SQM\1 Month\dump_subs_device_sim_info_20251227_20260125.csv` | 1 | 5,219,007,985 (4.86 GiB) | **125,939,523** |
| Delta batches | `D:\SQM\2026-05-13\dump-02..dump-06` | 81 | ~31.9 GiB | **670,485,040** |
| Delta batch | `D:\SQM\2026-05-16\dump-07` | 1 | 0.31 GiB | **7,095,661** |
| TAC device DB | `D:\TAC\DeviceDatabase_TAC1Sep2026.csv` | 1 | 206,286,771 (197 MiB) | **270,166** |
| **Total** | | **84** | **~34.9 GiB** | **~803.8 M** |

Delta batch detail:

| Batch | Files | Rows | add | remove |
|---|---:|---:|---:|---:|
| dump-02.260513015344 | 18 | 149,033,955 | 73,249,880 | 75,784,075 |
| dump-03.260513024554 | 16 | 139,584,564 | 68,458,364 | 71,126,200 |
| dump-04.260513040914 | 18 | 152,162,313 | 76,590,505 | 75,571,808 |
| dump-05.260513051425 | 18 | 150,756,599 | 75,372,226 | 75,384,373 |
| dump-06.260513054445 | 11 | 78,947,609 | 39,552,212 | 39,395,397 |
| dump-07.260516092203 | 1 | 7,095,661 | 3,268,729 | 3,826,932 |
| **Total** | **82** | **677,580,701** | **336,491,916** | **341,088,785** |

---

## 2. File formats

### 2.1 Subscriber dump (initial)

- CSV, header `imei,imsi,msisdn`, **LF** line endings, no BOM, pure ASCII, unquoted, no embedded delimiters.
- 3 columns, fixed shape. Zero malformed rows (all 125,939,523 data lines parsed cleanly).

### 2.2 Daily delta files

- CSV, header `msisdn,imsi,imei,label`, **LF**, no BOM, pure ASCII, unquoted.
- Column order **differs from the initial dump** (msisdn first, not imei). Confirmed by the sidecar `1.config` present in every batch folder: `{"msisdn":0,"imsi":1,"imei":2,"label":3}`.
- `label` domain is exactly `{add, remove}` — **0 rows** carry any other value across all 677.6M rows.

### 2.3 Batch control files (the delivery protocol)

Each batch folder contains:

| File | Content | Meaning |
|---|---|---|
| `1.config` | `{"msisdn":0,"imsi":1,"imei":2,"label":3}` | Column ordering contract |
| `start.txt` | `05/13/2026 13:14:04` | Export start (US format `MM/DD/YYYY`) |
| `finish.txt` | `05/13/2026 13:53:41` | Export end |
| `1.done` | empty (0 bytes) | **Completion sentinel** |

The folder name encodes the export timestamp: `dump-NN.YYMMDDHHMMSS`.

`dump-02` has **no `1.done` file** — by the source system's own protocol, that batch is unsealed/incomplete.

### 2.4 TAC device database

- CSV, 26 columns, **CRLF**, no BOM, RFC-4180 quoted (`bandDetails` contains commas).
- Header: `tac, manufacturer, modelName, marketingName, brandName, allocationDate, lastUpdatedDate, organisationId, deviceType, bluetooth, nfc, wlan, authenticatedIMSEmergencyCallSupport, unauthenticatedIMSEmergencyCallSupport, imsEmergencyCallWithoutUICC, removableUICC, removableEUICC, nonremovableUICC, nonremovableEUICC, networkSpecificIdentifier, ntnConnectivity, simSlot, imeiQuantity, operatingSystem, oem, bandDetails`
- This is a **GSMA TAC Device Database** export.

---

## 3. Identifiers and grain

All IMSIs carry **MCC+MNC = 43211** (Iran / MCI — Hamrah-e-Aval). Single operator, single tenant in the data.

| Metric | Initial dump |
|---|---:|
| Rows | 125,939,523 |
| Distinct MSISDN | 79,264,573 |
| Distinct IMSI | 79,490,189 |
| Distinct IMEI | 88,562,574 |
| Distinct (msisdn, imsi, imei) | **125,939,523** |
| **Exact duplicate rows** | **0** |

**`(msisdn, imsi, imei)` is a natural key with zero duplicates.** The grain is a **device–SIM–number binding**, *not* one row per subscriber.

Relationship cardinality:

- **IMSI ↔ MSISDN is effectively 1:1** — 79,465,454 of 79,490,189 IMSIs (99.97%) map to exactly one MSISDN. 24,735 IMSIs map to 2+ MSISDNs (SIM re-issue / number change within the window).
- **MSISDN → IMEI is 1:N** — 55.5M MSISDNs have 1 device, 14.6M have 2, 4.7M have 3, with a tail to 8+.
- **IMEI → MSISDN is 1:N** — 73.2M IMEIs used by 1 number; 15.3M IMEIs used by 2+ (dual-SIM devices and device resale).

Mean rows per MSISDN = **1.59**. Since one number cannot be in two handsets simultaneously, this is the strongest evidence that **the "1 Month" file is the union of bindings observed over 2025-12-27 → 2026-01-25, not an instantaneous snapshot.** (Needs confirmation — see Q1.)

---

## 4. Delta semantics — validated by event replay

Ordering assumed for the replay: batch folder timestamp, then filename. This yields `day_seq` 1..82.

### 4.1 Files are **days**, not shards

Within `dump-04`, the same `(msisdn, imsi, imei, label)` quad occurs in 2 files (14.8M quads), 3 files (7.5M), 4 files (3.9M), 5+ files (2.0M). A key-sharded export cannot do this. Every file also spans the full MSISDN range (`910…` → `996…`).

**Each file is an independent time slice, and file order is semantically significant.**

### 4.2 Within a single day the feed is clean

For `dump-07` (7,095,661 rows):

| Metric | Value |
|---|---:|
| Rows | 7,095,661 |
| Distinct (msisdn, imsi, imei, label) | 7,095,661 |
| Distinct (msisdn, imsi, imei) | 7,095,661 |
| Distinct MSISDN | 6,509,198 |

**Zero intra-day duplicates, and no triple carries both `add` and `remove` on the same day.** 166,575 MSISDNs show both an add and a remove that day (a device swap).

### 4.3 Set semantics — the decisive test

Replaying the 1/64 MSISDN sample (10,592,576 events over 2,530,319 triples) and checking whether `add`/`remove` strictly alternate per triple:

| Result | Count | Share |
|---|---:|---:|
| Transitions checked | 8,062,257 | |
| **Alternation violations** | **30,849** | **0.383%** |
| — of which `add` after `add` | **0** | **0.000%** |
| — of which `remove` after `remove` | 30,849 | 0.383% |

**Zero double-adds across 8M transitions.** `add` and `remove` are set operations on the `(msisdn, imsi, imei)` binding. The only anomaly is a repeated `remove` of an already-inactive binding — idempotent and safe to treat as a counted no-op.

Net balance `(adds − removes)` per triple over the full history:

| net | triples |
|---:|---:|
| −5 | 1 |
| −4 | 2 |
| −3 | 161 |
| −2 | 8,737 |
| −1 | 299,509 |
| 0 | 1,976,626 |
| +1 | 245,283 |

**Net never exceeds +1.** 99.64% of triples land in {−1, 0, +1}, exactly as a toggling set requires.

### 4.4 The initial dump is *approximately* the starting state

First event per triple vs. presence in the initial dump:

| First event | In initial dump | Triples | Share within label |
|---|---|---:|---:|
| `remove` | **yes** | 698,455 | **97.88%** |
| `remove` | no | 15,128 | 2.12% |
| `add` | **no** | 1,560,400 | **85.89%** |
| `add` | yes | 256,336 | 14.11% |

A triple first seen being *removed* was almost certainly active at snapshot time (97.9% present). A triple first seen being *added* was almost certainly not (85.9% absent). The model holds — but the **14.1% / 2.1% residual is systematic, not noise** (see Q1/Q2).

Direct check on the first delta file (`dayli_01.csv`):

- `remove` rows found in the initial dump: **99.85%** (4,185,377 / 4,191,523)
- `add` rows already in the initial dump: **63.72%** (2,634,843 / 4,134,727)

### 4.5 Reconstructed current state (1/64 sample, extrapolated ×64)

| Metric | Value |
|---|---:|
| Active bindings after replaying all 82 days | **~108.0 M** |
| Distinct MSISDNs with at least one active binding | **~73.3 M** |
| Total distinct triples ever observed (base ∪ delta) | **~227.0 M** |

Active bindings per MSISDN in the final state: 65.2% have exactly 1, 13.4% have 2, 3.9% have 3, and 13.9% have 0 (fully churned out).

### 4.6 Daily volume

| Metric | Rows/day |
|---|---:|
| Min | 6,570,609 |
| **Mean** | **8,263,179** |
| Max | 13,533,737 |

~8.3M change events/day against ~108M active bindings ≈ **7.7% daily churn** at the binding grain.

---

## 5. Data quality findings

### 5.1 IMEI

| Class | Initial dump | % | Delta | % |
|---|---:|---:|---:|---:|
| Valid length 14 | 117,129,937 | 93.005 | 657,402,667 | 97.022 |
| **Literal `000000`** | **8,776,237** | **6.969** | **20,040,673** | **2.958** |
| Other all-zeros | 2,140 | 0.002 | — | — |
| Other lengths (6–13) | 31,209 | 0.025 | 137,361 | 0.020 |

`000000` is a **sentinel for "device unknown"**, not a parse error. At ~7% of the initial dump it is material: these rows can never be TAC-enriched and must be modelled as an explicit "Unknown device" bucket rather than dropped or left NULL.

Other malformed IMEI values observed: `000100` (25,562), `000510` (1,785), `501515014` (1,052), `000400`, `100010`, `601461400`, `6015404`, `00000030`, `000000301000`.

IMEIs are **14 digits = TAC(8) + serial(6)**; the Luhn check digit is already stripped. No IMEI is 15 or 16 digits, so no IMEI/IMEISV normalisation is required — but a `length = 14` guard is mandatory before `substr(imei, 1, 8)`.

### 5.2 IMSI and MSISDN

| Field | Anomaly | Initial dump |
|---|---|---:|
| IMSI | length 16 | 4 rows |
| IMSI | length 17 | 5 rows |
| MSISDN | length 8 | 16 rows |
| MSISDN | length 9 | 3 rows |
| MSISDN | length 12 | 3 rows |
| MSISDN | length 13 | 1 row |
| MSISDN | length 15 | 1 row |

Negligible in volume, but they break fixed-width assumptions and must be quarantined, never silently truncated. All values are numeric — zero non-numeric characters in any identifier column.

342 distinct 4-digit MSISDN prefixes appear. The head is standard Iranian MCI (`9149`, `9148`, `9144`, `9147`, `9146`, `9143`, `9142`, `9118`, `9119`, `9931`, `9930`, `9189`…). The tail contains non-conforming prefixes (`2358`, `8458`, `4158`, `5858`, `2658`) at very low volume — likely M2M/corporate ranges, or dirt.

### 5.3 Referential anomalies (measured on the 1/64 replay)

- **Orphan removes** (remove of a binding that is not currently active): 94,252 / 5,332,386 = **1.77%**
- **Redundant adds** (add of a binding that is already active): 1,009,102 / 5,260,190 = **19.18%**

Both are concentrated at the boundary between the initial dump and the first delta batch, consistent with a **missing window of deltas** (see Q2). The pipeline must treat both as non-fatal, counted, and reportable — never as a hard failure.

---

## 6. TAC dataset

| Metric | Value |
|---|---:|
| Rows | 270,166 |
| Distinct `tac` | **270,166** |
| **Duplicate TACs** | **0** |
| NULL `tac` | 0 |
| TACs not exactly 8 chars | 0 |
| Non-numeric TACs | 0 |

**`tac` is a clean primary key.** Leading zeros are significant (`00100100`), so it must be stored as **TEXT, never an integer**.

Dimension cardinality: 10,529 manufacturers · 10,195 brands · 135,681 models · 143,537 marketing names · 19 device types · 555 operating systems · 10,543 organisations · 6,058 OEMs.

`allocationDate` parses 100% with `%d-%b-%Y`, range 1992-01-22 → 2026-08-31.

### 6.1 Enrichment coverage — measured against the initial dump

| Outcome | Rows | % |
|---|---:|---:|
| **TAC matched** | **116,876,401** | **92.804** |
| No valid IMEI (`000000` etc.) | 8,807,446 | 6.993 |
| Valid IMEI, TAC not in GSMA DB | 255,676 | 0.203 |

**92.8% enrichment coverage.** Only 0.2% of rows have a well-formed IMEI whose TAC is genuinely missing from the GSMA database — the coverage ceiling is set almost entirely by the `000000` sentinel, not by the TAC file.

95,571 distinct TACs appear in the data (35% of the 270,166 in the GSMA DB). Largest unmatched TACs: `30025072` (23,519 rows), `30025081` (20,736), `60025091` (10,043), `00106800` (2,425), `00000000` (2,269).

### 6.2 Key distribution / skew

The TAC distribution is **mild and long-tailed** — there is no hot-key problem:

| Bucket | Share of rows |
|---|---:|
| Top 10 TACs | 2.16% |
| Top 100 TACs | 13.27% |
| Top 1,000 TACs | 56.72% |
| Total distinct TACs | 95,571 |

The largest single TAC is `35004012` at 302,749 rows = **0.24%**. Safe for hash partitioning and for pre-aggregation without skew mitigation.

### 6.3 Business signal (proof the enrichment is worth building)

Top manufacturers by enriched initial-dump rows:

| Manufacturer | Device type | Rows | % |
|---|---|---:|---:|
| Samsung Korea | Smartphone | 51,432,020 | 40.84 |
| Xiaomi Communications Co Ltd | Smartphone | 27,974,097 | 22.21 |
| Apple Inc | Smartphone | 9,241,034 | 7.34 |
| HMD Global Oy | Feature phone | 5,821,066 | 4.62 |
| HUAWEI Technologies Co Ltd | Smartphone | 3,406,550 | 2.71 |
| Quectel Wireless Solutions Co Ltd | Modem | 2,480,387 | 1.97 |
| Fibocom Wireless Inc | Modem | 1,451,093 | 1.15 |
| Nokia Corporation | Handheld | 1,426,952 | 1.13 |
| SIMCOM Wireless Solutions Co Ltd | Modem | 1,303,326 | 1.04 |

The modem vendors (Quectel, Fibocom, SIMCOM ≈ 4.2% combined) indicate a real **IoT/M2M segment** in the subscriber base.

### 6.4 Network and SIM capability — what the TAC data does and does not carry

Measured across the 270,166-row GSMA export and weighted by active bindings:

| Capability | Source field | Assessable bindings | Supported | Share of assessable |
|---|---|---:|---:|---:|
| **LTE** | `bandDetails` lists LTE bands | 116,876,401 | 99,704,527 | **85.31%** |
| **5G** | `bandDetails` contains `5G NR:` or `5G NA` | 116,876,401 | 21,947,454 | **18.78%** |
| **eSIM** | `removableEUICC` + `nonremovableEUICC` counts | 116,876,401 | 6,571,548 | **5.62%** |
| IMS emergency calling | `authenticatedIMSEmergencyCallSupport` | **426,010** | 326,083 | 76.54% |

**VoLTE is not in this dataset.** Two independent checks:

- `bandDetails` mentions VoLTE in **2 rows out of 270,166**.
- The three IMS columns describe *emergency calling over IMS*, which is a different capability, and
  they are `Not Known` for **254,264 TACs (94.1%)**.

There is no honest way to report VoLTE support here. The dashboard says so explicitly rather than
substituting a proxy — "LTE-capable" is not "VoLTE-capable", and presenting one as the other would look
like an answer without being one.

The IMS row is published anyway, labelled for what it actually is, and drawn muted with its coverage
stated: it reads 76.5% supported, but only across **0.34%** of the subscriber base.

Notes on the detection rules:

- Matching bare `5G` instead of `5G NR` / `5G NA` also catches **135 TACs** that mention it incidentally.
- The eUICC fields are **counts, not flags**. Both `0` and `00` occur and both mean none; `1`, `2` and `3`
  all appear and all mean the device has an embedded UICC.

### 6.5 Manufacturer normalisation is required

`manufacturer` is a raw free-text GSMA string and is **not dashboard-ready**:

- **Samsung**: `Samsung Korea` (34,761), `Samsung Euro QA Lab` (474), `Samsung Korea (PO Box 105, Gyeonggi-Do)` (357), `Samsung` (259), `Samsung Electronics America` (131), `Samsung Electronics Co Ltd` (33)
- **Motorola**: `Motorola Mobility LLC, a Lenovo Company` (4,590), `Motorola Inc.` (1,798), `Motorola` (304), `Motorola UK` (172), `Motorola Ltd UK` (152), `Motorola Solutions, Inc` (56), `Motorola Electronic GmbH` (46)
- **Nokia**: `Nokia Corporation` (7,458), `Microsoft Mobile Oy, Nokia Corporation` (719), `Nokia` (530), `Nokia Mobile Phones Ltd` (454), `Nokia Solutions and Networks Oy` (51)

Case/whitespace dirt alone: `operatingSystem` 555 raw → 460 normalised (e.g. `Not Known`, `Not Known  `, `Not known`); `brandName` 10,195 raw → 9,220 normalised.

Missing-value rates: `brandName = 'Not Known'` 61,761 (22.9%), `operatingSystem = 'Not Known'` 102,368 (37.9%). NULLs: `brandName` 40, `operatingSystem` 1, `bandDetails` 6.

**A curated vendor-mapping layer is needed**, owned as data (editable in the UI), not hardcoded in application logic.

---

## 7. Measured performance baseline

On this workstation (D: NVMe, 8 threads, DuckDB 1.5.5):

| Operation | Volume | Time | Throughput |
|---|---:|---:|---:|
| CSV → Parquet (zstd), initial dump | 125.9M rows / 4.86 GiB | **22.7 s** | **5.55 M rows/s** |
| CSV → Parquet (zstd), all 82 delta files | 677.6M rows / 32.2 GiB | **206.5 s** | **3.28 M rows/s** |
| `wc -l` sequential read | 337 MB | 0.64 s | ~530 MB/s |
| `count(DISTINCT)` ×4 over 125.9M rows | 125.9M | 74.1 s | — |
| TAC join coverage over 125.9M rows | 125.9M | 4.0 s | — |
| Top-N TAC aggregation over 117.1M rows | 117.1M | 2.3 s | — |

Parquet + zstd compression: initial dump 4.86 GiB → **2.13 GiB** (2.3×); deltas 32.2 GiB → **11.4 GiB** (2.8×).

**Columnar storage turns a full-history scan into a 2–6 second operation.** This is the single most important input to the storage-engine decision in Phase 1.

---

## 8. Environment

| Component | Status |
|---|---|
| Working directory | `D:\New_PI` — **empty, not a git repository** |
| OS | Windows 11 Enterprise 26200 |
| Python | 3.12.0 and 3.14.7 (`py` launcher); Inkscape's bundled 3.12 has no pip |
| Node.js | v24.19.0 |
| Java | 26.0.2 |
| .NET | 10.0.400 |
| Docker | CLI 29.7.2 installed, **daemon not running** |
| Git | 2.55.0.windows.4 |
| Disk | C: 41.8 GB free · **D: 124.1 GB free** |

---

## 9. Artifacts produced

- `tools/profiling/*.py` — reproducible profiling scripts (DuckDB).
- `D:\_sqm_discovery\pq\base.parquet` (2.13 GiB) and `delta.parquet` (11.4 GiB) — working projections. **Scratch only**, outside the repo, safe to delete.

---

## 10. Date recovery attempt — ordering confirmed, calendar dates not recoverable

> **SUPERSEDED (2026-09-12).** Dated files were subsequently supplied at `D:\SQM\New CDR\New CDR`,
> which resolves this section entirely — see `03-dated-daily-files.md`. The ordering inferred below
> proved **exactly correct**, but two estimates in it were wrong: there is **no gap** after the
> initial dump, and the true span of these 82 files is **2026-01-26 → 2026-04-17**, roughly four
> weeks earlier than estimated here. Kept for the record because the inference method, and its
> stated limits, still stand.

Because the delta files contain no date column (Q2), I attempted to recover the ordering and the dates from
the data itself, using the TAC `allocationDate` as an independent clock: a device cannot appear on the
network before its TAC was allocated, so the "newest device allocation date" observed in a file is a lower
bound on that file's data date, and it should advance monotonically through a correctly-ordered sequence.

Measured across all 82 files in the assumed order (batch folder timestamp, then filename), using a
consistent estimator — the high quantiles of `allocationDate` over TACs in `add` rows:

| Files | p999 | p9999 | p99999 |
|---|---|---|---|
| 1–13 | 2025-09-30 | 2025-11-18 | 2025-11-18 |
| 14–28 | 2025-10-08 → 10-20 | 2025-11-18 | 2025-12-16 → 2026-01-06 |
| 29–52 | 2025-10-20 | 2025-12-03 → 12-19 | 2026-01-06 → 2026-01-26 |
| 53–70 | 2025-10-20 → 11-04 | 2025-12-16 → 12-19 | 2026-01-06 → 2026-02-11 |
| 71–82 | 2025-11-03 → 11-12 | — | — |

**Result: the assumed ordering is correct.** The device-allocation frontier advances **monotonically
non-decreasing** across the full sequence at every quantile. A wrong ordering would produce a series that
jumps around; this one does not. This independently validates Assumption 6 (batch timestamp, then filename)
and de-risks the ingestion design.

**Result: exact calendar dates are *not* recoverable.** The frontier drifts only ~43 days across 82 files
(~0.52 days per file), because device adoption lags TAC allocation and allocations are not uniform in time.
The signal is strong enough to order the files, and far too weak to date them.

**Conclusion: Q2 still requires an answer from the source team.** The ordering assumption is now
evidence-backed, but any calendar x-axis in the UI would be fabricated. Until real dates are supplied, the
system will label the time axis as *delivery sequence*, not date.

---

## 11. Entity-level analytics (scope confirmed: population + churn + subscriber lookup + SIM swap)

Measured on the 1/64 MSISDN sample across the full history (initial dump ∪ 82 delta days).

### 11.1 SIM swap — distinct IMSIs per MSISDN

| Distinct IMSI per MSISDN | MSISDNs | % |
|---:|---:|---:|
| 1 | 1,307,057 | **98.260** |
| 2 | 22,378 | 1.682 |
| 3 | 618 | 0.046 |
| 4 | 78 | 0.006 |
| 5+ | 77 | 0.006 |

**~1.74% of MSISDNs changed SIM at least once** over the ~4.5-month window. Estimated
**~83,000 new SIM bindings per day** network-wide. SIM swap is cleanly detectable and is a well-supported
KPI — and, being a known fraud vector, is worth surfacing as a monitored metric rather than only a chart.

### 11.2 Device change — distinct IMEIs per MSISDN

| Distinct IMEI per MSISDN | MSISDNs | % |
|---:|---:|---:|
| 1 | 647,302 | **48.662** |
| 2 | 298,898 | 22.470 |
| 3 | 145,638 | 10.949 |
| 4 | 77,517 | 5.827 |
| 5 | 45,214 | 3.399 |
| 6 | 28,686 | 2.157 |
| 7 | 19,900 | 1.496 |
| 8 | 13,945 | 1.048 |
| 9 | 10,173 | 0.765 |
| 10+ | 42,935 | 3.228 |

**51.3% of MSISDNs were seen on 2 or more devices** in the window. This is a much richer churn signal than
expected and makes device-migration analysis (for example, "which manufacturer is Xiaomi losing users to")
genuinely viable.

### 11.3 Derivable event types

From `(msisdn, imsi, imei)` plus add/remove, these are all computable without any extra source data:

| Event | Definition | Supported |
|---|---|---|
| Device change | same `(msisdn, imsi)`, `imei` changes | yes — 51.3% of MSISDNs |
| SIM swap | same `msisdn`, `imsi` changes | yes — 1.74% of MSISDNs |
| Number change / SIM reuse | same `imsi`, `msisdn` changes | yes — 24,735 IMSIs in the initial dump |
| Dual-SIM device | same `imei`, 2+ MSISDNs active concurrently | yes — 15.3M IMEIs in the initial dump |
| Multi-device subscriber | same `msisdn`, 2+ IMEIs active concurrently | yes — 34.8% in the final state |
| Device activation / deactivation | first / last `add` per IMEI | yes |
| Manufacturer migration | device change crossing a manufacturer boundary | yes, via TAC enrichment |

---

## 12. Open questions

See `02-open-questions.md`.

**Answered (2026-09-11):**
- **Q3 — Scope:** all three workloads are in scope (population analytics, churn analysis, subscriber-level lookup), plus SIM swap and "any other meaningful statistic". → the **full event history must be retained and be queryable**.
- **Q15 — SQM:** "SQM" is this subscriber dataset. There is no separate service-quality dataset.
- **Q4 — Privacy:** raw identifiers displayed, no masking required. → masking is built as a config flag, disabled by default; export audit logging is retained (it was in the original brief).
- **Q7 — Infrastructure:** not yet decided. → design must run well on one server and scale out without a rewrite.
- **Q5 — Retention:** full event history, retained indefinitely.
- **Team stack:** .NET/C#, Vue.js, JavaScript, MS SQL Server, MongoDB, Kafka, Elasticsearch, Redis. → ADR-001 (.NET) and ADR-005 (Vue).
- **Q13 — Locale:** English only, LTR only, Gregorian only. → no i18n framework, no RTL work, no Jalali calendar library.

- **Q2 — File dates:** **RESOLVED.** Dated daily files supplied; see `03-dated-daily-files.md`. There is no gap after the initial dump, and 7 days are missing in May 2026.

**Still blocking:** Q1 (snapshot vs. union) — and now more important, since the missing-window explanation for the 19.18% redundant-add rate has been ruled out.
**Newly raised:** does the organisation already own MS SQL Server licences? (affects ADR-002 only, and only if answered before Phase 2 completes)
