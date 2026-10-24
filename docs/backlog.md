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
| **TAC rows in the Import history show their dataset date** | The Import Center's TAC card now reads the snapshot's date (2026-10-20); the history table's Day column still shows none for TAC files | Small: the same join in the history read | Priority |
| **Examples behind each Data quality count** - a "show examples" drill-down per category on the page | The page shows only counts; seeing what a category holds took a generated document (`docs/discovery/07-data-quality-examples.md`, 2026-10-21). The selection rules are written and checked in `tools/quality_examples.py` | An endpoint per category group, audited like a lookup since it returns identifiers; a panel on the page | Owner's decision |
| **Day-anomaly strip on the risk page** | Not built: it would repeat what the feed-quality monitor already flags (ADR-014, M10) | - | Only if the feed becomes clean enough that it would say something new |

## Engineering

| What | Why, and the evidence | Cost | Waits on |
|---|---|---|---|
| **End-to-end browser tests** (Playwright) | Layout and flows are verified by hand in the browser pane; component tests run without CSS - the line-clamp bug of 2026-10-20 was invisible to them (`17-dashboard-builder.md`, known limitations) | Adds a dependency and browser downloads | Owner's decision |
| **Check the Explorer as a user with only some lookup permissions** | Verified with unit tests and an administrator account only (`18-explorer-modes.md`, "Verified") | Create a test role and user on the local stack | Permission to create a test account |
| **Negative numbers in CSV exports** | `CsvExport` neutralises any cell starting with `-`, numbers included, so a spreadsheet will not sum them (`17-…`) | Small, but changes every CSV the product writes | Owner's decision |
| **PNG export of a very tall dashboard** | Pages over 6,500 px are split; the largest template was never exported and timed (`17-…`) | A measurement | Priority |
| **Merge the branch stack** | `feature/dashboard-builder` ← `feature/explorer-simple` ← `fix/ui-review-2026-10-20` are stacked; the longer they live, the harder they merge | Pull requests, review, merge in order | Owner's review |

## Deployment (ADR-007; `docs/operations/deployment/`)

Found while packaging the system for the department VM and MCI's OpenShift, 2026-10-22. The
decisions some of these wait on are numbered in `docs/operations/deployment/README.md`.

| What | Why, and the evidence | Cost | Waits on |
|---|---|---|---|
| **The dump in the Import Center's history, and its original in the file store** | `--load-dump` (2026-10-24) loads the dump but records no import job and keeps no copy: the Import Center does not show it, and retention (originals kept for ever - CLAUDE.md) then depends on someone keeping the file. On the VM it stays in `~/quadrasense/incoming` | Small: store the file through `IImportFileStore` and record a completed job, as `db/operational/jobs/backfill_bulk_load_history.py` did for the first machine | Priority |
| **OpenShift manifests** (`deploy/openshift/`, Kustomize: base + dev/uat/prd) | The compose file is their specification (`03-mci-openshift.md` §2); untested manifests would be guesses about storage class names, the registry path and the ingress IP | Days, then a test in the dev namespace | The dev namespace and its delivery e-mail |
| **A liveness signal for the worker** | It has no HTTP endpoint, so neither compose nor OpenShift can tell a hung worker from a busy one, and nothing restarts it | Small: a heartbeat the lease loop already has a place for, exposed as a file or a tiny endpoint | Priority |
| **Prometheus metrics** in the API and worker; ClickHouse's own endpoint switched on | The platform collects metrics but there are none of ours to collect (red lines §2-1 row 10; `03-…` §6) | A package and a few counters; one config line for ClickHouse | Priority |
| **ClickHouse least privilege** - GRANTs per user | `sqm_app` and `sqm_ingest` hold every privilege, as in development: two secrets, one privilege set (red lines §1-4 row 11) | Small to write; the work is a test that runs every endpoint as `sqm_app` | Before the security review |
| **Synthetic data generator** | The security test bed may not hold real data (red lines §1-3); every test so far used the real dump | A few days: operator-format daily files and a TAC file with the shapes the pipeline depends on (dual-SIM pairs, the 15 September shift, invalid identifiers) | Decision 8 |
| **Originals in object storage** (an S3 `IImportFileStore`) | Written once, kept for ever, ~130 GiB a year: the shape object storage is for; ClickHouse reads S3 itself | An implementation of `IImportFileStore` and its tests | Stage 2, when the file volume grows |
| **Secrets as files** (`KeyPerFile` configuration) | Environment variables show in `docker inspect` and the process environment; files mounted from a Secret do not | A package and a line in each `Program.cs` | Priority; the security office's view |
| **CI/CD on the department's GitLab** - `.gitlab-ci.yml` (build and push on a Group build runner, test, deploy by a shell runner on the server), a release file per environment | The department's rule: Nexus is the only source and no build reaches the internet; `update.sh` breaks it. Nexus also deletes images no `releases/*.env` names after 30 days (`05-department-ci-cd.md` §1-3, §7) | Days, once the pieces exist; the design is in 05 §7 | Requests and decisions in 05 §8: MCR and NuGet proxies, the repository and its group on GitLab, a build runner with the socket, a deploy runner on the server |
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
