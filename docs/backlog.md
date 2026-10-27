# Backlog — suggested, not built

**Status:** living document, started 2026-10-20 at the product owner's request. Improvements that
were suggested and deliberately not built yet - for tokens, priority, or a decision still to be
made - so they are not lost between sessions. Each says what it is, the evidence behind it, the
rough cost, and what it waits on. When one is built, remove it here and name the commit.

Items that exist only because of the laptop are in `docs/operations/dedicated-hardware.md`, not
here.

`docs/research/2026-10-feature-research.md` (2026-10-21) lists some forty further candidates - new
features and improvements, with international references - awaiting the owner's choice. An item
moves here once it is chosen or deferred with a reason.

Collected on 2026-10-20 from this session and from the "not built" and "known limitations" sections
of `docs/`. Suggestions made in earlier sessions and never written into `docs/` may be missing.

---

## Product and data

| What | Why, and the evidence | Cost | Waits on |
|---|---|---|---|
| **Finished rows for filtered breakdowns** - one row per (filter value, breakdown key) for subscribers and handsets | A breakdown counted by subscribers or handsets under a filter still merges per-model states: 2.4 s alone, up to 12 s with three such widgets loading together (ADR-017) | Several more refresh passes over the 1 GiB of states - estimated minutes of refresh time; measure first | Owner's decision; maybe dedicated hardware (`dedicated-hardware.md` §3) |
| **Derive more marts from the per-model states** | Each mart that still reads `binding_current FINAL` pays the version bloat; deriving them from `agg_tac_population` removes passes from the refresh (ADR-017) | Per mart: a rewrite and a model-for-model comparison test | Priority |
| **Copied-IMEI detection that can work** | The four rules cannot see two handsets at once; that needs timestamped registrations with the serving cell, EIR CheckIMEI logs, IMEISV (`docs/discovery/06-…`, "What would make a valid detection possible") | A new measure and rule, once the data exists | **The operator's data** |
| **Corrected files for 15 September onwards** | Shifted IMEIs carry other subscribers' handsets, still ~20% of rows on 10 October; the `01…` re-encoding predates it (`docs/discovery/04-…`, `05-…`) | Re-import per day, `replace by date` | **The operator** |
| **Check every day's file rows against its events** | The laptop's pre-platform bulk load wrote only part of four days - 14.6 million events - and nothing said so: the backfill recorded each day's row count from the event log, not from the file. Found 2026-10-25/26 by comparing with the department VM; the four days were re-imported 2026-10-26 (jobs 2475-2479) and now match their files | A query of each completed day's `rows_committed` against its events, run after imports or on demand | Priority |
| **The first machine's bulk-loaded originals are outside its file store** | 134 of its 135 pre-platform files are recorded at `D:/SQM/...`, a folder that no longer exists (they are on E: now), and the file store refuses any path outside its root ("stored path escapes the storage root") - so for every bulk-loaded day "Import again" and the original's download cannot work, wherever the file is. Found 2026-10-26 repairing four of those days, whose files were copied into the store first | Small: move each file into the store's root (same disk, so a rename) and record its store path; check every SHA-256 first | The owner: it moves the files out of the folder they are kept in |
| **The migrator applies the analytics seeds, once** | `db/analytics/seed/002_vendor_map_seed.sql` was applied by hand on the first machine and not at all on the department VM, whose vendor filter then showed Samsung as six vendors (2026-10-25). Every new environment - OpenShift too - repeats the step or the mistake | Small: seeds tracked like migrations, so each runs once and an edit made in the product is never overwritten; a test that a second run changes nothing | Priority |
| **A TAC import that loaded every row says "Partially completed"** | Both machines' imports of `DeviceDatabase_TAC4Oct2026.csv` (271,830 rows in, 271,830 imported, 0 rejected, 0 invalid) end `PARTIALLY_COMPLETED`: the job's own reminder that the version is not active yet is a warning, and a warning makes the job partial - while migration 010 says partial means rows were rejected (found 2026-10-25) | Small: the reminder as an info event, or TAC's completion judged by its rows; a test with a clean file | Priority |
| **TAC rows in the Import history show their dataset date** | The Import Center's TAC card now reads the snapshot's date (2026-10-20); the history table's Day column still shows none for TAC files | Small: the same join in the history read | Priority |
| **Examples behind each Data quality count** - a "show examples" drill-down per category on the page | The page shows only counts; seeing what a category holds took a generated document (`docs/discovery/07-data-quality-examples.md`, 2026-10-21). The selection rules are written and checked in `tools/quality_examples.py` | An endpoint per category group, audited like a lookup since it returns identifiers; a panel on the page | Owner's decision |
| **Lists of ready and eSIM numbers narrowed by model** | The planning run keeps each number's models only for the exposed, LTE-M/NB-IoT-only and not-assessable classes (9.6 M numbers), so a list - or a campaign list - of ready or eSIM numbers cannot be narrowed by vendor, model or type; the API refuses it rather than answering empty. Even for the exposed class a narrowed scan reads the class's rows: the Samsung-narrowed sunset view over all 75 M numbers passed 60 s (2026-10-27) | A per-run (model, class, number) table ordered by model - about 110 M rows, est. 0.5-1 GiB a run, measure first - written by each chunk, and the lists read from it | Owner's decision: is a narrowed eSIM campaign needed? |
| **The operator's LTE and NR bands (N2)** | The band view lists every band's population but matches nothing until `OperatorBands:Lte` and `:Nr` hold the operator's real bands; a guessed list would make every match a claim (`19-network-planning.md` §5) | Configuration only: the band numbers, set in the deployment | **The operator** |
| **VoLTE readiness (N1)** | LTE is not VoLTE, and after a 3G switch-off voice needs it. The GSMA catalogue has no VoLTE field - 2 TACs mention it in free text (`19-network-planning.md` §1) | A per-model VoLTE flag, or IMS registrations per number, joined to the sunset classes | **The operator's data** |
| **eSIM-capable handset on a physical SIM (N3)** | The campaign question for an eSIM launch; the feed does not say whether a SIM is physical or an eSIM | The SIM type per IMSI (from the SIM inventory or the HSS), then one more class in the planning snapshot | **The operator's data** |
| **Trackers apart from other IoT (N5)** | The catalogue has no tracker device type; trackers are inside "IoT / M2M" with modules and routers | A maintained list of tracker models, or a rule on the module's marketing name, measured before use | Owner's decision |
| **Satellite service in use (N6)** | The view shows capability; who has a satellite plan or used one is not in the files | Subscription or usage data per number | **The operator's data** |
| **Day-anomaly strip on the risk page** | Not built: it would repeat what the feed-quality monitor already flags (ADR-014, M10) | - | Only if the feed becomes clean enough that it would say something new |

## Engineering

| What | Why, and the evidence | Cost | Waits on |
|---|---|---|---|
| **End-to-end browser tests** (Playwright) | Layout and flows are verified by hand in the browser pane; component tests run without CSS - the line-clamp bug of 2026-10-20 was invisible to them (`17-dashboard-builder.md`, known limitations) | Adds a dependency and browser downloads | Owner's decision |
| **Check the Explorer as a user with only some lookup permissions** | Verified with unit tests and an administrator account only (`18-explorer-modes.md`, "Verified") | Create a test role and user on the local stack | Permission to create a test account |
| **Negative numbers in CSV exports** | `CsvExport` neutralises any cell starting with `-`, numbers included, so a spreadsheet will not sum them (`17-…`) | Small, but changes every CSV the product writes | Owner's decision |
| **PNG export of a very tall dashboard** | Pages over 6,500 px are split; the largest template was never exported and timed (`17-…`) | A measurement | Priority |
| **Measure the suite again with no database** | CLAUDE.md's breakdown - 190 passed, 34 skipped of 224 - was measured with the stack down on 2026-09-24; the suite is 710 tests now (`README.md`, Tests), so what a green cloud run proves is not known to the test | One run with the stores stopped, or the test fixtures pointed at a closed port | A time when the laptop's stack may stop |

## Deployment (ADR-007; `docs/operations/deployment/`)

Found while packaging the system for the department VM and MCI's OpenShift, 2026-10-22. The
decisions some of these wait on are numbered in `docs/operations/deployment/README.md`.

| What | Why, and the evidence | Cost | Waits on |
|---|---|---|---|
| **OpenShift manifests** (`deploy/openshift/`, Kustomize: base + dev/uat/prd) | The compose file is their specification (`03-mci-openshift.md` §2); untested manifests would be guesses about storage class names, the registry path and the ingress IP | Days, then a test in the dev namespace | The dev namespace and its delivery e-mail |
| **A liveness signal for the worker** | It has no HTTP endpoint, so neither compose nor OpenShift can tell a hung worker from a busy one, and nothing restarts it | Small: a heartbeat the lease loop already has a place for, exposed as a file or a tiny endpoint | Priority |
| **Prometheus metrics** in the API and worker; ClickHouse's own endpoint switched on | The platform collects metrics but there are none of ours to collect (red lines §2-1 row 10; `03-…` §6) | A package and a few counters; one config line for ClickHouse | Priority |
| **ClickHouse least privilege** - GRANTs per user | `sqm_app` and `sqm_ingest` hold every privilege, as in development: two secrets, one privilege set (red lines §1-4 row 11) | Small to write; the work is a test that runs every endpoint as `sqm_app` | Before the security review |
| **Synthetic data generator** | The security test bed may not hold real data (red lines §1-3); every test so far used the real dump | A few days: operator-format daily files and a TAC file with the shapes the pipeline depends on (dual-SIM pairs, the 15 September shift, invalid identifiers) | Decision 8 |
| **Originals in object storage** (an S3 `IImportFileStore`) | Written once, kept for ever, ~130 GiB a year: the shape object storage is for; ClickHouse reads S3 itself | An implementation of `IImportFileStore` and its tests | Stage 2, when the file volume grows |
| **Secrets as files** (`KeyPerFile` configuration) | Environment variables show in `docker inspect` and the process environment; files mounted from a Secret do not | A package and a line in each `Program.cs` | Priority; the security office's view |
| **Run the department pipeline for the first time** - `.gitlab-ci.yml`, kaniko builds on `docker-global`, deploys by the group's shell runner (written 2026-10-24, not yet run) | Nothing of it has run on GitLab: the runner's real permissions, job-image pulls through Nexus and MCR access are unknown until `probe:runner` (05 section 7) | Hours of fixing what the first run shows | The owner's first push, the group's CI/CD variables, the deploy runner's registration (05 section 7) |
| **Builds through Nexus** - the web Dockerfile takes the npm registry's credential as a BuildKit secret (`always-auth`), and every base image and package source points at Nexus | The same rule; the build arguments exist (`01-container-images.md`), the secret mount does not | Small | An MCR proxy in Nexus (05 §8, request 1). NuGet and npm go through Nexus already (2026-10-24) |
| **Image scanning and a patch routine** | Every tag is pinned, so nothing updates itself (red lines §1-1 row 5) | A CI step and a monthly rebuild | MCI's GitLab |
| **Red Hat UBI base images** | The red lines name Red Hat as the OS (§1-1 row 3); UBI's .NET 10 image did not pull from the laptop on 2026-10-22 | A build argument per Dockerfile, then the verification of `01-container-images.md` again | Decision 9 |
| **Backups, automated, with a restore test** | None exist; the originals are kept but have no copy of their own (`02-…` §6, `03-…` §5) | Scripts or CronJobs; a quarterly restore | A destination; RPO/RTO (decision 14) |
| **Audit trail to the SOC** | The red lines want every log at the SOC (§2-4); ours is a PostgreSQL table | An export in the SOC's format | The SOC's format |
| **Captcha on the login page** | Red lines §2-3; conflicts with English-only | Self-hosted; Persian if required | Decision 6 |
| **High availability: PostgreSQL replication, ClickHouse replication with Keeper, a second site** | Red lines §2-3; `03-…` §5 sizes each | Medium to large; ClickHouse's is a migration of every table | Decision 7 |

## Identity and access (recorded as not built)

| What | Status |
|---|---|
| MFA | Not required for a strictly internal deployment; four steps to add it are in ADR-006 (`12-identity-and-access.md` §8) |
| LDAP / Active Directory | The seam exists (`IPasswordAuthenticator`, `provider` / `external_id`) |
| OIDC | A callback endpoint mapping a subject claim onto `external_id` |
| Self-service password reset | Needs email delivery, which local accounts do not have |
