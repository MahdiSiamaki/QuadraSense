# Research: what QuadraSense could do next

**Date:** 2026-10-21, on the data through 2026-10-10 (delivery 246).
**Status:** Research for the product owner's decision. **Nothing here is built or approved.** Each idea
says what it is, why (with the evidence or the international system it comes from), what data it
needs, a rough effort, and the decision it waits on. Section 6 lists the questions whose answers
decide the order.
**Method:** an inventory of what the system does today; figures measured on the served delivery;
and comparable international systems - national device registries (CEIR / DIRBS), the GSMA's device
and network services, device-analytics products, telecom fraud practice, BI and data-observability
tools. Sources are at the end. Effort is an estimate - S: days, M: one to two weeks, L: several weeks -
not a measurement.

---

## 1. Where the system stands

| Area | What it does today |
|---|---|
| Dashboards | Builder with 33 widget types (51 catalogue entries), templates, sharing, a URL lens (vendor, device type, OS, TAC), PNG/CSV export, refresh intervals |
| Devices | Catalogue of models from the GSMA TAC database, device pages, new-model arrivals, network age, image review |
| Explorer | Simple mode (one search: details, relationships incl. dual-SIM pairs, history) and Advanced (a costed, budgeted query builder) |
| Risk signals | Four shared-IMEI rules, SIM-on-many-IMEIs, SIM changes per number; levels capped on feed-defect days |
| Data quality | 17 categories from the measures snapshot, day-by-day feed checks against ordinary days |
| Imports | SQM daily files and GSMA TAC snapshots: validation, quarantine, versions, activation, replay by date |
| Settings | Users, roles and a permission matrix, audit log, appearance |

**What the data is - and is not.** One file a day of `(msisdn, imsi, imei, add|remove)`, plus the
GSMA catalogue. No time of day, no cell or location, no traffic, no tariff, no subscriber
attributes. Every idea below is marked for whether today's data is enough.

**What the catalogue already knows and the product barely uses:** radio bands per model, 5G, LTE,
eSIM (removable and embedded), satellite (NTN) connectivity, NFC, Wi-Fi, IMS emergency calling, SIM
slots, IMEI count per device, allocation date. Measured on delivery 246:

| Capability | Bindings | Share | Numbers (est.) |
|---|---:|---:|---:|
| Handset **without LTE** | 14,922,607 | 12.4% | 13,498,315 |
| 5G-capable handset | 22,582,066 | 18.8% | 18,890,751 |
| eSIM-capable handset | 7,060,277 | 5.9% | 6,546,467 |
| IoT / M2M device | 9,578,675 | 8.0% | 8,751,326 |
| Feature phone | 7,121,472 | 5.9% | 6,521,283 |

---

## 2. What comparable systems do

**National device registries (CEIR, DIRBS, Hamta).** Pakistan's DIRBS imports operator data dumps of
exactly this shape - IMEI, IMSI, MSISDN - with the GSMA database, importer lists and stolen lists, and
classifies each IMEI: GSMA-valid, unique, not stolen, type-approved. Duplicates are counted over a
30-day window by distinct numbers (published thresholds: "Duplicate" at 8 or more numbers with a
daily average above 2.5, "Duplicate large" at 60 or more), and a genuine device is *paired* to its
numbers rather than blocked. India's CEIR blocks lost phones on report and checks for cloned devices
across all networks. Iran's own registry (Hamta) has, since 2017, limited networks to devices that
entered legally; unregistered phones stop working after 30 days.

**The GSMA's services.** The *Device Registry* is a shared blocklist of lost, stolen and fraudulent
IMEIs that participating operators contribute to and receive; *Device Check* gives an IMEI's
history and status. *Device Map* (with DeviceAtlas) joins TACs to ~150-220 device properties and is
used for 5G SA/NSA readiness, deciding 2G/3G shutdowns, segmentation and enriching CDRs.

**Network APIs (CAMARA / GSMA Open Gateway).** *SIM Swap* answers "did this number's SIM change
recently, and when?"; *Device Swap* answers the same for the handset. Banks call them before
authorising a payment or a password reset. Both are standardised, certified, and live at operators
in Spain, the UK, Germany, Brazil and Qatar.

**Fraud practice.** IMEI-SIM pairing is a core signal (a device used by many SIMs, a SIM used in
many devices), read as a graph rather than SIM by SIM; TACs separate phones from modems and trackers.
The same sources warn of what this system has already met: sequential IMEIs are often factory
testing, and **a corrupted feed makes one device look like many swaps** (`docs/discovery/05-…`).

**BI tools.** Metabase, Superset and Grafana all send scheduled reports and threshold alerts by
email or chat; annotations mark events on charts.

**Data observability.** Freshness, volume, schema and distribution monitored continuously, with
anomaly detection, and *data contracts* agreed with the producer and checked at every hand-over.

---

## 3. New features

Feasibility: **A** - today's data is enough; **B** - needs a new feed or integration; **C** - needs
a policy decision first.

### 3.1 Network and product planning

| # | Feature | Why | Feas. | Effort |
|---|---|---|---|---|
| N1 | **2G/3G sunset readiness.** Numbers whose every held handset lacks LTE (and, once known, VoLTE), by vendor, model and device type; IoT modules that are 2G/3G-only; trend over deliveries; an exportable target list for a migration campaign | 14.9 M bindings are on non-LTE handsets today. Device Map's flagship use; AT&T's shutdown showed some "4G" devices still need 3G for voice | A | M |
| N2 | **5G and band readiness.** Parse `bandDetails` against the bands the operator actually runs: which handsets can use each LTE/5G band, 5G SA vs NSA where the catalogue says | Spectrum and rollout planning; 18.8% of bindings are 5G-capable, but capable of *which* bands is the real question | A (needs the operator's band list) | M |
| N3 | **eSIM readiness.** eSIM-capable population by model and trend; numbers whose handset is eSIM-capable but who use a physical SIM | For an eSIM launch or migration; 6.5 M numbers are on eSIM-capable handsets | A | S |
| N4 | **Handset lifecycle.** Device age (first seen on the network) and model age (GSMA allocation date); time-to-upgrade cohorts; the models people leave and the ones they move to (extends Vendor movement) | Device-upgrade and retention planning; analysts tie handset age to churn | A | M |
| N5 | **IoT / M2M fleet view.** Modules and trackers apart from phones: counts, SIM rotation, 2G-only exposure, vendor of module (Quectel, SIMCom, Fibocom...) | 9.6 M IoT bindings; ADR-014 already found the risk lists' long tail is modules | A | M |
| N6 | **Second-slot insight.** Dual-SIM handsets where only one radio has ever carried one of our SIMs - a lower bound on "the other slot is empty or another operator's" | Competitive insight from data already held; the pairing rule (`14-…`) supplies it. Must be stated as a lower bound - it cannot tell empty from competitor | A | M |
| N7 | **Satellite-capable (NTN) devices** | The catalogue carries `ntnConnectivity`; direct-to-device service is arriving | A | S |

### 3.2 Fraud, security and compliance

| # | Feature | Why | Feas. | Effort |
|---|---|---|---|---|
| F1 | **Swap check service** - CAMARA-shaped *SIM Swap* and *Device Swap* answers for one number: last change date, changed in the last N days | Exactly what banks ask operators for; the binding history already holds it. Limit, stated in the response: daily granularity, and feed defects (multi-number SIMs, shifted IMEIs) must be set aside | A + C (who may call it) | M |
| F2 | **IMEI compliance status** (DIRBS-style): GSMA-valid, unique (duplicate thresholds over 30 days), not on a stolen list, registered - per IMEI, with lists to hand to the network's EIR | The regulators' standard model; our risk rules are already half of it | A for validity and uniqueness; B for stolen and registered | M |
| F3 | **Stolen-device presence.** Import the GSMA Device Registry blocklist; flag held bindings on blocklisted IMEIs | Lost and stolen phones still working on the network | B (GSMA Device Registry membership) | S once the feed exists |
| F4 | **Registry reconciliation (Hamta).** Held handsets not in the national registry, by model and age | Compliance and smuggling indicators; Hamta is the Iranian standard | B (registry extract) + C | M |
| F5 | **Case management and watchlists.** Open a case from any risk entity; assign, status, notes, outcome; a watchlist that reports when an entity changes | Today a reviewer has nowhere to record a decision, and the rules' precision cannot be measured without outcomes | A | L |
| F6 | **Link-analysis graph.** The relationships of Explorer drawn as a graph, several hops out, with the shared-IMEI and SIM-change signals on the nodes | Fraud rings are graphs; vendor practice and patents read IMEI-SIM pairing this way | A | M |
| F7 | **SIM-box / bulk-SIM indicators** from what the feed holds: modems with many SIMs, SIMs rotating across modules, sequential-IMEI fleets set apart as benign | ADR-014 measured the shape; industry sources say IMEI alone is weak - so an indicator, not a verdict | A | M |

### 3.3 Data operations

| # | Feature | Why | Feas. | Effort |
|---|---|---|---|---|
| D1 | **Defect evidence pack for the operator.** One click: a document per flagged defect with counts, examples, SQL and the questions (as `04-…` and `05-…` were written by hand) | The shifted IMEIs are still 20% of rows; the operator needs evidence to fix the export | A | M |
| D2 | **Delivery SLA.** Expected arrival per day; alert when a file is late, short or arrives twice | Freshness is shown, but nothing warns | A | S |
| D3 | **Anomaly detection on the daily file.** Volume, adds/removes ratio, identifier shapes, per-vendor shares against seasonal expectations - beyond today's four fixed checks | The September defects were found because a chart looked wrong, weeks late | A | M |
| D4 | **Data contract with the operator.** Columns, formats, the `000000` sentinel, removal semantics - agreed, versioned, checked at import | Removal semantics are still unknown (`02-open-questions.md`) and decide what "active" means | C (needs the operator) | S to write |
| D5 | **Quality history.** Each category's trend across snapshot runs, not only the latest | The page shows one run; a regression cannot be seen | A | S |
| D6 | **Examples behind each quality count** (already in `docs/backlog.md`) | | A | M |

### 3.4 Analytics experience

| # | Feature | Why | Feas. | Effort |
|---|---|---|---|---|
| X1 | **Alerts and scheduled reports.** A threshold on any widget's figure; a dashboard as PDF on a schedule; delivered by email or a chat channel | Standard in Metabase and Grafana; listed as deferred in the roadmap | C (channel) | M |
| X2 | **Chart annotations.** Mark days with what happened - a GSMA version, a defect, a campaign - shown on every daily chart | The feed-quality shading does this for defects only | A | S |
| X3 | **Cross-filtering.** Clicking a bar filters the whole dashboard; chips already exist | Deferred in the roadmap | A | M |
| X4 | **Forecasts.** Population and share trends projected a quarter ahead with an interval | Planning (5G, sunset) needs "when", not only "now" | A | M |
| X5 | **Questions in plain English** turned into a validated Explorer query - never SQL, so the server's checks still decide | Lowers the bar for analysts; the Explorer's structured query model makes it safe | C (whether an AI model may be used, and where it runs) | M |

### 3.5 Platform and governance

| # | Feature | Why | Feas. | Effort |
|---|---|---|---|---|
| G1 | **Single sign-on** (LDAP / Active Directory) | Recorded as not built (`12-identity-and-access.md`); the seam exists | B | M |
| G2 | **Reason for access.** Ask why before a lookup or an export of identifiers; record it with the audit entry | Common in systems holding subscriber data; masking is off by decision, so the audit trail carries the weight | C | S |
| G3 | **Four eyes for bulk exports** of identifiers | A target list of millions of numbers is a different act from one lookup | C | M |
| G4 | **Audit analytics.** Who looked up what, how often, outside normal patterns | The audit log is complete but only listed | A | M |
| G5 | **Integration API** with service tokens, rate limits and its own audit - needed by F1 and by other operator systems | | C | M |
| G6 | **Monitoring, backups and a timed restore drill** (roadmap Phase 6) | A backup never restored is not a backup | B (dedicated hardware) | M |

---

## 4. Improvements to what exists

| Module | Improvement | Why | Effort |
|---|---|---|---|
| Dashboards | Finished rows for filtered breakdowns by subscribers or handsets (backlog) | 2.4-12 s today | M |
| Dashboards | A "what changed since my last visit" strip on each dashboard | The data changes daily; readers look for the difference | S |
| Dashboards | Mobile layout check and touch targets for the grid | Built for desktop; verified down to 390 px but not used on phones | S |
| Devices | Capability chips and bands on the device page; filter the catalogue by capability (N1-N3) | The catalogue has the fields; the page shows few | S |
| Devices | Vendor normalisation editor with preview of what each change moves | Spellings collapse by a map; changes should show their effect | S |
| Explorer | Save an investigation (the trail of identifiers opened) as a case (F5) | Simple mode's Back trail is lost on leaving | M |
| Explorer | Compare two identifiers' histories side by side | Common investigation step | S |
| Explorer | Batch lookup: paste a list, get each one's summary | Analysts receive lists, not single numbers | M |
| Risk | Show "new since last run" on each list | Reviewers re-read the same entities | S |
| Risk | Rule back-testing: what a threshold would have listed on past runs | Thresholds were cut once; tuning has no tool | M |
| Imports | TAC rows in Import history show their dataset date (backlog) | | S |
| Imports | Watched folder / SFTP pick-up with the `1.done` gate - planned in `01-overview.md`, not in the code: every file is uploaded by hand today | Removes the daily manual upload | M |
| Data quality | Trend per category (D5), examples per category (D6) | | S / M |
| Settings | Role templates per job (analyst, investigator, operator) | Faster, fewer permission mistakes | S |

---

## 5. Recommendation

If the answers in section 6 do not change the picture, a first tranche that uses only data the
system already holds, each piece small enough to measure and ship on its own:

1. **N1 Sunset readiness** - the largest measured exposure (14.9 M bindings), and a decision the
   operator has to make anyway.
2. **D1 Defect evidence pack + D2 delivery SLA** - the shifted-IMEI defect is still 20% of rows; the
   fastest fix to every downstream figure is the operator fixing the export.
3. **F1 Swap check** (internal first, as a page and an API behind a permission) - the most standard
   telecom anti-fraud product, already answerable from the binding history.
4. **F5 Case management** - turns risk lists into outcomes, and outcomes are what measure a rule.
5. **N3 eSIM + N2 band readiness** - cheap once N1's capability work exists.
6. **X2 Annotations + D5 quality history** - small, and they make every chart and the quality page
   readable over time.

Deliberately later: anything needing a new feed (F3, F4, G1) until the feed is promised; X5 until
the question of AI use is settled; anything heavy before the dedicated hardware.

---

## 6. Questions for you

1. **Who is this for, first?** Network planning, marketing, fraud and security, or regulatory
   compliance - the order of section 3 follows the answer.
2. **What may the system reach?** Is it air-gapped, or may it call the GSMA (Device Registry,
   Device Check), the national registry (Hamta), an email server, a chat service, an AI model?
3. **What more can the operator send?** Registration events with time and cell, EIR CheckIMEI logs,
   tariff or plan, subscriber province, activation dates, the operator's band list. Each one opens
   features above that today's data cannot support.
4. **Who else will use it?** Other departments or outside parties (banks for F1) change the access
   model: integration API, reason-for-access, four-eyes.
5. **Subscriber lists for campaigns** (N1, N3, N6 produce them): allowed, and under what governance?
6. **Hardware timing:** features that read `binding_current FINAL` heavily (F2's 30-day windows over
   all IMEIs, N4's cohorts) are cheaper after the move; should they wait for it?

---

## 7. The product owner's answers (2026-10-21), and what they change

| Question | Answer | Consequence |
|---|---|---|
| Who is it for | **All four**: network planning, marketing, fraud and security, compliance | The first tranche carries one piece for each, on a shared foundation |
| What it may reach | **A closed network**, with an **internal email server** and an **AI model running inside the network** | X1 alerts and scheduled reports go through the internal email. X5 plain-English questions only on the in-network model - no identifier leaves the network. The GSMA Device Registry and Hamta can arrive only as files: F3, F4 wait for such a file |
| What more the operator can send | **Nothing for now** | Only A-features. N2 also needs the operator's band list - a short list a person can type in, but still to be supplied |
| Subscriber lists for campaigns | **Allowed, with controls** | A separate permission, a recorded reason, and a second person's approval above a size threshold (G2, G3) - built before any list export (N1, N3, N4, N6) |

**The first tranche, revised:**

| # | For | What |
|---|---|---|
| 0 | Everyone | **Controlled list export** (G2 + G3) - the gate every campaign list passes; **operator defect pack and delivery SLA** (D1 + D2) - the feed itself |
| 1 | Network planning | **N1 sunset readiness**, with **N3 eSIM readiness** beside it (same capability work) |
| 2 | Marketing | **N4 handset lifecycle** - device and model age, upgrade cohorts - and its target lists through 0 |
| 3 | Fraud and security | **F1 swap check** (page, then an internal API) and **F5 case management** |
| 4 | Compliance | **F2 IMEI compliance status** from today's data: GSMA validity and uniqueness over 30 days; the stolen and registered parts wait for their files |
| 5 | Everyone | **X1 alerts and scheduled reports** by internal email; **X2 annotations** |

Later: X5 once the in-network model is known (which model, where it runs - likely after the move to
dedicated hardware); F3 and F4 when their files exist; N2 when the band list arrives.

---

## Sources

- Pakistan PTA, *DIRBS SOP* - https://dirbs.pta.gov.pk/asset/DIRBS_SOP_29_July_2019.pdf
- Pakistan PTA, *DIRBS FAQs* (2025) - https://www.pta.gov.pk/assets/media/2025-10-14-FAQs_updated.pdf
- Pakistan PTA, *DIRBS consultation document* (2016) - https://www.pta.gov.pk/assets/media/dirbs-cons-paper-220616.pdf
- GSMA Services Showcase Live 9 (DIRBS duplicate thresholds) - https://www.gsma.com/services/wp-content/uploads/2023/06/GSMA-Servives_ShowcaseLive9_Nauman-Khalid.pdf
- India CEIR / Sanchar Saathi coverage - https://www.boomlive.in/explainers/ceir-tracking-lost-mobile-easy-but-privacy-concerns-linger-22024 , https://tele.net.in/?p=45281
- Iran mobile registry (Hamta) - https://www.eghtesadonline.com/en/news/746680/iran-disconnects-2500-smuggled-cellphones-under-mobile-registry-scheme , https://www.visitouriran.com/blog/mobile-phone-registration-in-iran-your-essential-guide/
- GSMA Device Registry - https://www.gsma.com/services/deviceregistry/ ; GSMA policy handbook on device theft - https://www.gsma.com/solutions-and-impact/connectivity-for-good/public-policy/mobile-policy-handbook/consumer-protection/mobile-device-theft/
- GSMA Device Map / DeviceAtlas for operators - https://deviceatlas.com/products/mobile-operators ; BICS case study - https://www.gsma.com/services/wp-content/uploads/2019/07/bics_case_study.pdf
- 3G sunset preparation - https://www.mobileworldlive.com/blog/feature-how-are-operators-preparing-for-the-3g-sunset/ ; AT&T, *3G shutdown impact on 4G devices* - https://www.business.att.com/content/dam/attbusiness/briefs/3G_Shutdown_Impact_on_4G_Devices_brief.pdf
- GSMA Open Gateway API descriptions - https://www.gsma.com/solutions-and-impact/gsma-open-gateway/gsma-open-gateway-api-descriptions/ ; Telefónica Open Gateway, SIM Swap - https://developers.opengateway.telefonica.com/docs/simswap , Device Swap - https://developers.opengateway.telefonica.com/v1/docs/deviceswap
- SIM-card fraud detection (IMEI-SIM pairing, benign sequential IMEIs, feed corruption) - US patent 12041453, https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/12041453 ; US patent 8165563, https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/8165563
- SIM-box detection at network access (NDSS 2023) - https://www.ndss-symposium.org/wp-content/uploads/2023-416-paper.pdf
- Handset lifetimes - https://www.analysysmason.com/research/content/articles/mobile-device-survey-rdmd0/
- BI tools compared (alerts, subscriptions) - https://www.modern-datatools.com/compare/metabase-vs-superset-vs-grafana
- Data observability and contracts - https://www.soda.io/resources/data-observability
