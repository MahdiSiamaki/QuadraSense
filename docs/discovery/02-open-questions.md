# Phase 0 — Open Questions

Status legend: **[BLOCKING]** = Phase 1 architecture cannot be finalised without an answer.
**[IMPORTANT]** = I will proceed on a stated assumption, but a wrong assumption costs rework.
**[MINOR]** = I have assumed a sensible default and documented it; correct me at your convenience.

---

## A. Data semantics — the highest-risk area

### Q1 [BLOCKING] Is the initial dump a point-in-time snapshot, or a month of accumulated bindings?

The filename says `20251227_20260125` (a 30-day range), and the data agrees that it is a **range, not an instant**:

- 125,939,523 rows over 79,264,573 MSISDNs = **1.59 bindings per number**.
- 30.1% of MSISDNs have 2 or more `(imsi, imei)` bindings.
- A single number cannot be in two handsets at the same moment, so these must be sequential observations.

But the delta feed then treats the dump as if it *were* a starting state: 99.85% of the first delta file's `remove` rows match a row in the dump.

This ambiguity is the root cause of the two anomalies in §5.3 of the profiling report (19.2% redundant adds, 1.77% orphan removes).

**What I need:** which is it?

- **(a)** Active bindings as of 2026-01-25 (a true snapshot; the date range in the filename is just the export window).
- **(b)** Every binding seen at any point during 2025-12-27 → 2026-01-25 (a union).
- **(c)** Something else — e.g. every binding active for at least *N* hours, or a "last known device per SIM" view.

**Why it matters:** under (a) I can build an exact current-state table. Under (b) the current state is only reconstructable *approximately*, and the system must be explicit about that — I would model a binding's state as `active / inactive / unknown-at-baseline` rather than pretending to a certainty the data does not support.

---

### Q2 [BLOCKING] There is no date anywhere in the delta data. How do we know which day a file belongs to?

This is the most serious structural gap I found. The delta files contain **only** `msisdn, imsi, imei, label`. There is no timestamp column, no date column, and no date in the filenames (`dayli_01.csv`, `d01.csv`, `01.csv`).

The only ordering signals available are:

1. The batch folder name (`dump-04.260513040914` → 2026-05-13 04:09:14) — this is the **export** time, not the data date.
2. The filename sequence within a batch.

And the file numbering **restarts** across batches: `dump-02` has `dayli_01..18`, `dump-03` continues `dayli_19..34`, but `dump-04`, `dump-05` and `dump-06` each start again at `d01`. So filename alone does not give a global order.

I have verified that order genuinely matters: the same binding is added and removed repeatedly across days (within `dump-04` alone, 14.8M quads appear in 2 different files, 7.5M in 3, 3.9M in 4). Applying files in the wrong order produces a wrong current state.

Arithmetic that may or may not be a coincidence: 82 files × 1 day ≈ 82 days back from 2026-05-16 lands around 2026-02-23, leaving roughly a **27-day hole** between the dump's end (2026-01-25) and the first delta. A hole of that shape would explain the 19.2% redundant-add rate precisely.

**What I need:**

- Does each file correspond to exactly one calendar day? (The evidence says yes, but please confirm.)
- **What date does each existing file map to?** If you can get a manifest from the source team, that would resolve this completely.
- Is there a gap between 2026-01-25 and the first delta file? If so, what period is missing, and can it be re-requested?
- Going forward, will each daily delivery be **one folder containing one file** (like `dump-07`), or can a delivery contain several days at once (like `dump-02..06`)?

**Why it matters:** without a reliable data date, the system cannot answer *any* time-series question ("device growth per week", "daily change"), cannot detect a missing day, cannot detect a re-sent day, and cannot safely re-process. If no date can be obtained, I will have to derive an ingestion-order sequence number and be honest in the UI that the x-axis is "delivery sequence", not calendar time. That is a significantly weaker product, so it is worth pushing the source team hard on this.

---

### Q3 [BLOCKING] What is the system actually for?

The data supports three quite different products, and they imply different storage engines, different data models, and different costs. I do not want to guess.

- **(a) Device population analytics.** "How many active Samsung devices are on the network today? What is the Android/iOS split? Which models are growing?" → needs current state + daily aggregates. Cheapest and fastest.
- **(b) Change and churn analytics.** "How many subscribers switched from Xiaomi to Samsung last month? What is the device replacement rate?" → needs the full event history retained and queryable. ~3B rows/year.
- **(c) Subscriber-level operational lookup.** "Given this MSISDN, show every device it has used." → needs point lookups on 100M+ keys, and has much heavier privacy implications.

**Please tell me which of these are in scope, and rank them.** If all three are needed I will design for it, but (b) and (c) materially increase storage cost and change the engine choice, so I want that to be a deliberate decision rather than a default.

---

### Q4 [BLOCKING] Privacy: who is allowed to see raw IMEI, IMSI and MSISDN?

Every column in this dataset is a direct personal identifier. An MSISDN is a phone number; an IMSI identifies a specific SIM; an IMEI identifies a specific handset. Together they link a person to a device. This is the most sensitive kind of telecom data, and 79M individuals are in scope.

**What I need:**

- Who may see **raw** MSISDN values? All users, or only a specific role?
- Should MSISDN/IMSI be **masked** by default in the UI (e.g. `0912***4567`), with unmasking as a separately audited permission?
- Should identifiers be **hashed or tokenised at rest**, so the analytics store never holds raw values? (This is very achievable if the answer to Q3 is (a)-only; it becomes awkward if (c) is in scope.)
- Are exports of raw identifiers permitted at all? If so, by whom, and must every export be audit-logged?
- Are there **regulatory obligations** (national telecom regulator, internal data-protection policy) I should design to explicitly?

**My default if you have no strong preference:** raw identifiers never leave the database; the UI masks by default; unmasking and any export containing identifiers requires an explicit permission and writes an audit record naming the user, the filter, and the row count.

---

### Q5 [BLOCKING] Retention: how much history do we keep?

At 8.26M events/day the event history grows by **~3.0 billion rows/year**. In Parquet/columnar form that is roughly **50–60 GB/year** compressed — very manageable. In a row-store with indexes it would be several times that.

**What I need:**

- Keep the full event history indefinitely, or roll off after N months?
- Is a daily **aggregate** history (counts by TAC/manufacturer/model/device-type per day) sufficient for anything older than, say, 6 months? Aggregates are ~4–5 orders of magnitude smaller and would make long-range trend queries instant.
- How far back must the UI be able to query at full binding-level detail?

---

## B. Operations and delivery

### Q6 [IMPORTANT] How will daily updates actually arrive?

Today I see a folder tree on a local disk. For the production system I need to know the real channel: SFTP drop, a network share, a manual upload through the web UI, an API push, or direct database access at the source.

Related, and important: the batch protocol already has a **completion sentinel** (`1.done`). I intend to rely on it — a batch folder without `1.done` is not ingested. Note that **`dump-02` currently has no `1.done`**, so by that rule it is incomplete. Is that a real problem with that batch, or was the file just lost in transit to this machine?

Also: is `1.config` guaranteed to be present on every future delivery? I would like to use it as the authoritative column-order contract rather than trusting the CSV header.

### Q7 [IMPORTANT] What infrastructure will this run on?

- On-premise or cloud? (Given the data sensitivity I am assuming **on-premise**.)
- What are the actual server specs — CPU cores, RAM, disk type and capacity? This is the main input to the storage-engine decision.
- Is **Docker** available and permitted in production? (The CLI is installed here but the daemon is not running.)
- Is **Kubernetes** available, or is this a single-server or few-server deployment?
- Is there an existing database platform the organisation already runs and supports (PostgreSQL, Oracle, SQL Server, ClickHouse)? An engine your ops team already knows is worth real weight against a marginally better one they do not.

### Q8 [IMPORTANT] Network access from the build and production environments

Can the build machine and the production servers reach public package registries (npm, PyPI, Docker Hub, Maven Central)? If access is restricted or unreliable, that is not a minor inconvenience — it changes how I pin dependencies, whether I vendor them, and whether I build fully self-contained images. Please tell me early; it is much cheaper to design for than to retrofit.

### Q9 [ANSWERED] Users, roles and concurrency

Answered 2026-09-14. Built accordingly; see `docs/adr/ADR-006-authentication-and-access-control.md`.

| Question | Answer |
|---|---|
| Authentication source | **Local accounts**, behind an interface so LDAP/AD can be added later without a rewrite |
| SSO / LDAP / AD / OIDC in future | Likely. `provider` and `external_id` are in the schema from the first migration for that reason |
| Network exposure | **Strictly internal.** No internet path |
| Session mechanism | **Server-side sessions** rather than JWT, after the trade-off was put to the product owner |
| MFA at go-live | **Not required.** No dead columns were added; the four steps to add it are recorded |
| Roles | The four proposed were accepted, with one correction: Data Operator uploads a TAC snapshot but does not activate it (decision D4) |
| Multi-tenancy | Single-tenant, as assumed |

Still unanswered, and not blocking: how many named users, and peak concurrency. Nothing in the
design depends on the answer at the scale this deployment implies - sessions are one indexed
lookup and the user list pages from the first request - but a figure in the hundreds rather than
the tens would be worth knowing before the first load test.

**What would change the design:** if the deployment ever becomes internet-facing, three decisions
are wrong, in this order - per-IP lockout (absent), the 8-hour idle timeout (too long), and MFA
(not built).

---

## C. Product

### Q10 [IMPORTANT] What are the actual dashboard questions?

I can build many charts from this data, but I would rather build the ten that get used than forty that do not. Please give me the real questions the business asks — ideally the report someone assembles by hand today.

From the data, these are well-supported and cheap:

- Active device population by manufacturer / brand / model / device type / OS
- Daily and weekly change: devices added, removed, net growth
- Top N devices, and biggest movers week over week
- Smartphone vs feature-phone vs IoT/modem mix, and how it is trending
- Dual-SIM rate (one IMEI serving 2+ MSISDNs) — 15.3M IMEIs in the initial dump qualify
- Device-change rate (subscribers who swapped handsets in a period)
- Unknown-device rate (the `000000` population) as a data-health KPI
- TAC coverage and enrichment quality over time

Which of these matter? What is missing?

### Q11 [IMPORTANT] When a new TAC file is loaded, should past enrichment be recomputed?

You raised this yourself and it is a genuine fork in the road:

- **(a) Historical mapping preserved.** A binding enriched in January keeps January's manufacturer/model, even if the TAC file later corrects it. Reports never change retroactively. Requires a versioned TAC dimension and a version reference on enriched rows.
- **(b) Always current mapping.** Enrichment resolves against the newest TAC file at query time. Corrections apply everywhere immediately, but a chart you exported last month may not reproduce today.
- **(c) Hybrid.** Store the TAC key only, resolve at query time against the current version, but keep every TAC version so a specific historical version can be pinned for reproducibility.

**My recommendation is (c).** It avoids a costly reprocess of hundreds of millions of rows whenever the TAC file is updated, it makes corrections effective immediately, and it still allows an auditor to reproduce an old report. The cost is a join at query time — and given TAC is only 270K rows, that join is essentially free in any column store.

Do you agree, and how often will a new TAC file arrive?

### Q12 [IMPORTANT] Freshness requirement

Deliveries appear to be daily. Is a **daily batch** acceptable, with data visible within, say, 30–60 minutes of the file landing? Or is there a genuine near-real-time requirement? Batch is dramatically simpler and cheaper, and nothing I have seen in this data suggests streaming is warranted — but I would rather ask than assume.

### Q13 [IMPORTANT] Language, locale and calendar

Given this is an Iranian mobile operator's data:

- Should the UI be **Persian (Farsi), English, or bilingual**?
- Is **RTL layout** required? This is a foundational frontend decision, not a theme — it is much cheaper to build in from the start than to retrofit.
- Should dates display in the **Jalali (Shamsi)** calendar, Gregorian, or both?
- Persian and Arabic-Indic digit rendering — required, or optional?

### Q14 [MINOR] Export and reporting

- Which formats: CSV, Excel, PDF?
- What is the largest export a user should be able to request? (My assumption: anything above ~100K rows becomes a background job with a download link, so the browser is never blocked.)
- Are scheduled/emailed reports needed in v1, or can they wait?
- Are notifications needed (import succeeded/failed, data-quality threshold breached)? By what channel — in-app, email, SMS?

---

## D. SQM scope

### Q15 [BLOCKING] What exactly is "SQM"?

You referred to **SQM files** as a separate thing to upload, process, validate and import. So far everything I have found under `D:\SQM` is the subscriber device/SIM dump described above — `imei, imsi, msisdn` and its `add`/`remove` deltas.

- Is "SQM" simply your name for this subscriber dataset, so the Upload Center handles exactly these files?
- Or is there a **different** SQM dataset (Service Quality Management — KPIs, cell-level metrics, coverage, quality measurements) that I have not seen yet?

If it is the latter, that is a substantially different dataset with its own grain, its own time dimension and its own storage needs, and I need a sample before designing the data model. Please point me at a file or describe it.

---

## E. Non-functional

### Q16 [IMPORTANT] Availability, backup and recovery

- Is **high availability** required, or is a single server with a good restore procedure acceptable?
- What is the acceptable downtime for a planned upgrade?
- **RPO** — how much data loss is tolerable in a disaster? (Note: the source files are themselves a natural backup; if they are retained, a full rebuild is possible by re-running the pipeline, which is a genuinely strong recovery position.)
- **RTO** — how quickly must the system be back after a total loss?
- Is off-site/disaster-recovery replication required?

### Q17 [MINOR] Environments

Do you need separate **Dev / Staging / Production** environments, or is a local development setup plus a single production server sufficient? Is there existing CI infrastructure (GitLab CI, Jenkins, GitHub Actions), or should I design for local scripts plus documented manual deployment?

---

## F. Raised after Phase 0, from the data

### Q18 [IMPORTANT] Since 15 September the daily files attach other subscribers' handsets, one day at a time. What changed, and can the days be re-sent?

Added 2026-10-08. The evidence is `04-handset-changes-from-2026-09-15.md`. In short: about 1.0–1.6 million adds a day carry a `35…` IMEI with its first digit dropped and a `0` appended; with the digit put back, 87.5% of them (1% sample) are handsets that until then belonged only to other numbers. They sit beside the subscriber's own handset and 78% are removed the next day. That churn is 85% of the rise in handset changes, and it was still there on 6 October. On 16 September the same happened with IMEIs in their normal form, about 166,000 handset changes more than an ordinary day.

**What I need from the operator:**

- What changed in the export on 15 September, and was 16 September the same change?
- Can corrected files be sent from 15 September? They replace each day in place, and every chart follows.
- On ordinary days too, about two thirds of the handsets newly added in a handset change belong to another number and three quarters are gone within two days. Is that how the network really sees subscribers, or a smaller form of the same thing?

**Why it matters:** until it is answered, handset and SIM change counts from 16 September are 3.5–5 times what subscribers did, and the last question decides whether the ordinary days' figures can be trusted either.

---

## Assumptions I am proceeding on unless corrected

These are non-blocking; I have picked a sensible default and will document it. Tell me if any is wrong.

1. **Single tenant.** All data is MCC/MNC 43211 (MCI), so no multi-tenancy is built in — though the schema will carry an operator dimension so it is not painful to add later.
2. **`000000` is "device unknown"**, a legitimate business category, not a defect to be filtered away. It will be surfaced as an explicit bucket and tracked as a data-quality KPI.
3. **`(msisdn, imsi, imei)` is the natural key** for a binding, supported by 0 duplicates in 125.9M rows.
4. **TAC is derived as `substr(imei, 1, 8)` guarded by `length(imei) = 14`**, and is stored as TEXT to preserve leading zeros.
5. **Repeated `remove` is an idempotent no-op** — counted and reported, never an error. Justified by 0 double-adds across 8M transitions.
6. **File order within a batch is chronological**, and batches are ordered by their folder timestamp. This is an assumption forced by the absence of dates (Q2) and I will make it explicit and overridable in the ingestion metadata.
7. **Timezone is Asia/Tehran (UTC+3:30)** for display; all timestamps stored in UTC.
8. **Deliveries are daily batches**, not streams.
9. **Ingestion is idempotent, keyed on file content hash.** Re-uploading an identical file is detected and rejected as a duplicate rather than double-applied.
10. The `1.done` sentinel gates ingestion: no `1.done`, no ingest.
