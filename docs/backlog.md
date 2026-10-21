# Backlog — suggested, not built

**Status:** living document, started 2026-10-20 at the product owner's request. Improvements that
were suggested and deliberately not built yet - for tokens, priority, or a decision still to be
made - so they are not lost between sessions. Each says what it is, the evidence behind it, the
rough cost, and what it waits on. When one is built, remove it here and name the commit.

Items that exist only because of the laptop are in `docs/operations/dedicated-hardware.md`, not
here.

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
| **ADR-007: where the import worker runs** | The worker runs inside the API in Development; production should run it apart. `Program.cs` names ADR-007 as the place for that decision; it is not written | A short ADR | The move to dedicated hardware |
| **Merge the branch stack** | `feature/dashboard-builder` ← `feature/explorer-simple` ← `fix/ui-review-2026-10-20` are stacked; the longer they live, the harder they merge | Pull requests, review, merge in order | Owner's review |

## Identity and access (recorded as not built)

| What | Status |
|---|---|
| MFA | Not required for a strictly internal deployment; four steps to add it are in ADR-006 (`12-identity-and-access.md` §8) |
| LDAP / Active Directory | The seam exists (`IPasswordAuthenticator`, `provider` / `external_id`) |
| OIDC | A callback endpoint mapping a subject claim onto `external_id` |
| Self-service password reset | Needs email delivery, which local accounts do not have |
