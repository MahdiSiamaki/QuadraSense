# 17 — Dashboard builder

How the configurable dashboards work, what each widget type reads and promises, and where the
rules live. The decision and its evidence are in
[ADR-016](../adr/ADR-016-dashboard-builder.md); this page is the reference.

## Pieces

| Piece | Where | What it does |
|---|---|---|
| Widget contract | `frontend/src/features/dashboard/registry/widget-contract.json` | The allow-list: 33 types, their config fields and bounds, permissions, lens filters, count units, time scope and execution. Embedded into `Sqm.Application`; both sides validate against it, and a spec fails if the registry and the contract disagree. |
| Definition validator | `Sqm.Application/Dashboards/DashboardDefinitionValidator.cs` | Refuses unknown types and fields, out-of-bound values, bad layouts and ids, identifier-bearing Explorer queries. Stores a normalised definition. |
| Store | `Sqm.Infrastructure/Identity/PostgresDashboardStore.cs`, migration `016_dashboards.sql` | `dashboard.dashboard` (JSONB definition, revision), `dashboard_grant`, `favorite`, `user_default`. |
| Service | `Sqm.Application/Dashboards/DashboardService.cs` | Access (owner, grants via user or role, `dashboard.*` permissions), optimistic concurrency, audit. |
| Export | `DashboardExportService.cs`, `DashboardWidgetDataLoader*.cs`, `Sqm.Api/Endpoints/DashboardExportEndpoints.cs` | Snapshot (PNG / print) and CSV, loaded per widget under the exporter's own permissions, audited `data.export`. |
| Page | `frontend/src/features/dashboard/DashboardPage.vue` | Switcher, lens bar, grid, edit mode with draft / undo / redo / save / conflict, export and share dialogs. |
| Grid | `components/DashboardGrid.vue` | GridStack 14 behind an adapter: 12 / 6 / 1 columns, drag handle in the card header, keyboard and menu moves. Cards read at a glance - KPI, KPI summary, Data freshness, Device class mix, Network & SIM capability, and the bar-chart and table widgets - are as tall as their content (definition `height: 'content'`) and never scroll; every card on a row ends with the tallest. GridStack counts in 10px sub-rows so a fitted card wastes at most 10px; layouts are stored in 40px rows. A bar chart sizes itself, 30px per bar. |
| Widgets | `widgets/<group>/` | One folder per catalogue group: `config.ts`, components, `index.ts` with `definitions`. |

## Rules that hold for every widget

- **Two checks.** Opening a dashboard decides nothing about data. Each widget needs its own
  permissions (contract `permissions` and `permissionsByConfig`); without them it renders a
  card saying which permission is missing, and its queries never run. An export applies the same
  check on the server.
- **The lens** (count by, unknown devices, date range, vendor, device type, OS, TAC) is resolved per
  widget: dashboard defaults, then the URL, then the widget's overrides - and only the fields its
  type supports. A widget that ignores a filter says "Network-wide" on its card.
- **Relative ranges count back from the data's last day**, never from today.
- **Distinct counts are estimates** (HyperLogLog, about 0.5%) and are labelled so wherever shown.
- **Identifiers never enter a URL, a stored definition, a log or an audit detail.** Lists of
  identifiers (risk list, Explorer question) run only on an explicit Run, through their own audited
  endpoints, and never in an export. The quick-lookup box hands its value to a lookup page in
  memory only - for that page, once, for a few seconds - never in the URL or browser history.

## Catalogue

Columns: **Source** is the endpoint the card reads (an export reads the same composition on the
server). **Perm** is beyond `dashboard.view`. **Filters**: V vendor, D device type, O OS, T TAC,
U unknown devices. **Time**: `current` (latest refreshed delivery), `series`/`range` (the
dashboard's range applies), `fixedWindow` (the source's own window), `none`. **CSV** is the scope
the exported rows carry.

### Overview & Population

| Type | Source | Unit | Perm | Filters | Time | Config (defaults) | Empty / error | CSV | Caveats |
|---|---|---|---|---|---|---|---|---|---|
| `metric.kpi` | `/dashboard/kpi` | metric's own | — | V D O T U | current | metric (activeBindings), numberFormat compact, precision 1, definition and qualifier on | skeleton, then retry | visible | estimates labelled |
| `metric.kpiSummary` | `/dashboard/kpi` | each metric's | — | V D O T U | current | metrics (six), numberFormat, precision, definitions | same | visible | one request for all |
| `population.trend` | `/dashboard/daily-changes` | bindings | — (shading: import.view) | — | series | grain day/week/month, line/area, flagged shading, missing days | "No daily files loaded yet" / "No delivered day in this range" | visible | network-wide; a missing day is a gap |

### Device Intelligence

| Type | Source | Unit | Perm | Filters | Time | Config (defaults) | Empty / error | CSV | Caveats |
|---|---|---|---|---|---|---|---|---|---|
| `device.top` | `/dashboard/top/{dim}` | count by | — | V D O T U | current | dimension vendorCanonical, limit 10, bar, by count, values and shares, keep unknown, click to filter | empty state | topN | click to filter only for vendor and TAC; subscribers overlap |
| `device.distribution` | `/dashboard/distribution/{dim}` | count by | — | V D O T U | current | deviceType, limit 10, table, shares, click to filter | empty state | topN or full | OS normalised for case and whitespace |
| `device.classMix` | `/dashboard/device-class-mix` | count by | — | — | current | composition | empty state | full | network-wide |
| `device.capabilities` | `/dashboard/capabilities` | count by | — (GSMA row count: import.view) | — | current | all four capabilities, VoLTE note | empty state | visible | network-wide; VoLTE is not in GSMA |
| `device.newModels` | `/devices/new-models` | models | device.view | — | range | brand, device type, known only, limit 10 | not-ready message from the source | topN or full | first seen ≠ launch date |
| `device.modelArrivals` | `/devices/arrivals` | models | device.view | — | series | month, chart | not-ready message | full | first weeks are dump catch-up |

### Change & Movement

| Type | Source | Unit | Perm | Filters | Time | Config (defaults) | Empty / error | CSV | Caveats |
|---|---|---|---|---|---|---|---|---|---|
| `change.daily` | `/dashboard/daily-changes` | bindings | — (shading, calendar: import.view) | — | series | added, removed, running total; bars; shading; missing days | as trend | visible | network-wide |
| `change.simHandset` | `/dashboard/daily-churn` | numbers | — (shading: import.view) | — | series | both series, lines, shading | as trend | visible | same-day definition is a floor; two feed defects named |
| `change.vendorMovement` | `/dashboard/vendor-movement` | events or bindings | — | — | range (movement only) | movement, 5, chart | empty state | topN | events ≠ devices; growth against the network |
| `change.periodComparison` | daily changes or churn | metric's own | — | — | range | net, previous period, total, both | "unavailable" sentence (before data, no delivered day, dates missing) | visible | uneven delivered days flagged, never filled |

### Risk & Investigation

| Type | Source | Unit | Perm | Filters | Time | Config (defaults) | Empty / error | CSV | Caveats |
|---|---|---|---|---|---|---|---|---|---|
| `risk.status` | `/risk/status` | — | risk.view | — | fixedWindow | rules table on | not-ready / stale banners | visible | observed, not proven |
| `risk.overview` | `/risk/overview` | entities | risk.view | — | fixedWindow | all eight rules, table | not-ready | visible | names nobody |
| `risk.distribution` | `/risk/distribution` | entities (log) | risk.view | — | fixedWindow | SharedImeiSims30, both, threshold shaded | not-ready | full | values under the floor not stored |
| `risk.deviceTypes` | `/risk/device-types` | IMEIs or SIMs | risk.view | — | fixedWindow | HighDeviceCount30, 10, table | not-ready / nothing listed | topN or full | SIM rules use the most frequent TAC |
| `risk.simChangeEvidence` | `/risk/daily` | numbers | risk.view | — | series | both, shading | not-ready | visible | set aside = feed defects |
| `risk.evidenceQuality` | `/risk/overview` + status | entities | risk.view | — | fixedWindow | all eight rules | not-ready | visible | all-time rules have no data-quality view |
| `risk.list` | `POST /risk/list` (audited) | entities | risk.view + the rule's lookup permission | — | fixedWindow | HighDeviceCount30, risk view, 25 rows, no override | "runs only when you press Run" | its own audited export | explicit Run; override is for this widget only, `T + 1 >= floor` |

### Data Quality

| Type | Source | Unit | Perm | Filters | Time | Config (defaults) | Empty / error | CSV | Caveats |
|---|---|---|---|---|---|---|---|---|---|
| `quality.enrichment` | `/dashboard/kpi` | bindings | — | V D O T U | current | four figures | retry | full | four sum to the total; only malformed IMEI is a defect |
| `quality.feedTrend` | `/quality/days` | rate or rows | import.view | — | range | all four checks, rate, chart, flagging lines | "No measured day…" | visible | counts are rows for every check |
| `quality.flaggedDays` | `/quality/days` + `/imports/freshness` | days | import.view | — | range | flagged and missing, 20 | "every day delivered and ordinary" | topN or full | missing days from the import calendar |
| `quality.signals` | `/quality/signals` | bindings, numbers or est. SIMs/IMEIs | import.view | — | current | all groups, bindings, table | not-published message | visible | categories overlap |

### Data Operations

| Type | Source | Unit | Perm | Filters | Time | Config (defaults) | Empty / error | CSV | Caveats |
|---|---|---|---|---|---|---|---|---|---|
| `ops.freshness` | freshness, worker health, TAC versions, unfiltered KPI | days, states | import.view | — | none | pipeline and TAC on | retry | full | 35/45-day lag thresholds stated |
| `ops.recentImports` | `/imports` | jobs | import.view | — | none | any source, all states, 10 | "No import matches" | topN or full | superseded dimmed |
| `ops.importStatus` | worker health + freshness | jobs | import.view | — | none | — | retry | full | "stalled" in words |
| `ops.workerHealth` | worker health + freshness | workers | import.view | — | none | pending-rebuild note on | retry | full | API-hosted worker named |
| `ops.blockages` | `/imports/blockages` | days | import.view | — | none | 10 | "Nothing is blocked" | topN or full | — |
| `ops.tacVersion` | `/tac-versions` | versions | import.view | — | none | pending shown | retry | full | activation changes every figure |

### Analyst Tools

| Type | Source | Unit | Perm | Filters | Time | Config (defaults) | Empty / error | CSV | Caveats |
|---|---|---|---|---|---|---|---|---|---|
| `tools.explorerAggregate` | `POST /explorer/query` (audited, budgeted) | measures | explorer.query | — | fixedWindow | query (groups and counts), last N days, 20 rows, table | "Runs only when you press Run" | the Explorer's own export | no identifier field offered; validated on save |
| `tools.quickLookup` | the lookup page | — | the target's lookup permission | — | none | target | — | — | value never saved, never in a URL |
| `tools.modelNetworkAge` | `/devices/{tac}/network-age` (+ detail) | days | device.view | — | current | TAC (required), name the model | not ready / never seen | full | not the age of any handset |

## Templates

Code, not data (`frontend/src/features/dashboard/templates/`): **Device Population** (the old page,
captions preserved, its KPI row one "Key figures" card with one menu, the six figures in a row as before; `/` opens
it until a person chooses another default), **Security & Risk**,
**Support**, **Data Operations & Quality**. A template opens read-only; *Copy to my dashboards*
makes it editable.

## Editing

The draft is in memory (`composables/useDashboardDraft.ts`) until Save; nothing is stored before.

- **Save names the revision the draft was taken from**, never the cached detail's. A refetch while
  editing - a reconnect, the window regaining focus - moves the cache on to somebody else's newer
  revision; naming that one overwrote their changes without a conflict. Now it is a 409, and the
  draft is kept.
- **One action, one undo step**: adding a widget with its title and subtitle, a configure Apply, the
  settings dialog, a rename with its description, a finished drag or resize. 100 steps are kept.
- **Limits are enforced where the action is, not only at Save**: Add and Duplicate stop at the
  most widgets a dashboard holds (24) and Duplicate's menu item says why; the grid stops a resize at
  the contract's 40 rows and the draft caps any height a gesture hands it; the configure form
  refuses a widget's own TAC that is not eight digits and a text filter over 120 characters, as
  Save would.
- **Add widget**: another preset of the same type starts from its own settings, and the preview
  from its own form at once - the previous form's data settings never reach the new card. A list of
  text (a risk list's device types) is typed as one comma-separated line.
- **Settings are copied through JSON.** The configure form is reactive, so every list in its
  settings is a Vue proxy, and `structuredClone` refuses a proxy: copying that way made Add and
  Apply throw for the ten types whose settings hold a list.

Each of these has a test that fails when the rule is taken out (`tests/unit/features/dashboard/`).

## Export

`POST /api/v1/dashboards/export/snapshot` and `/export/csv` need `dashboard.view` and
`data.export`. The request carries the definition and the lens - never in a URL - and, for a saved
dashboard, its id for the access check and the audit.

| Status | Meaning | In a PNG / printout | In CSV |
|---|---|---|---|
| `ok` | loaded | drawn by the widget's own component | its rows, with the scope |
| `forbidden` | the exporter lacks a permission | named with the reason, or left out by choice | listed in `about.txt` |
| `notReady` | the source says not yet | the source's reason | listed |
| `error` | the load failed (logged as event 1400) | "The server could not prepare this widget." | listed |
| `interactive` | risk list, Explorer question | "runs only on request" | — |
| `noData` | quick lookup | — | — |

One widget is one CSV named `<dashboard>-<widget>-<scope>-<UTC stamp>.csv`; several are a ZIP with
an `about.txt` giving the lens, each file's scope and caveats, and what was left out and why. The
`X-Export-Scope` header carries the scope. Every export writes one `data.export` audit entry with
the format, counts and scope - never the lens or a figure.

## Running it

Migration `016_dashboards` runs with the others (README, *Running it* §3), then its grants file
once, as for every schema added after 003:

```bash
dotnet run --project backend/src/Sqm.Migrator -- --target postgres --dir db/operational/migrations
psql -U sqm -d sqm -f db/operational/grants/016_dashboards.sql
```

The migration also adds the four permissions and gives `dashboard.create`, `.edit` and `.share` to
analyst, data operator and administrator, and `dashboard.manage` to administrator only.

Tests: `dotnet test backend/Sqm.slnx`; in `frontend/`, `npm test` (Vitest) and `npm run build`.

## Known limitations

Measured or observed on 2026-10-09; each says what was and was not checked.

- **A filtered KPI is slow.** With a vendor filter, `/dashboard/kpi` took 90.4 s, and the same read
  through an export 72.6 s, on the local stack (`DashboardExportRealDataTests`). Unfiltered it reads
  the marts. Every KPI card, the KPI summary and the enrichment breakdown wait on it once a filter
  is applied. The path predates the builder (`ClickHouseAnalyticsStore` falls back to the raw table);
  it needs its own measurement and probably a mart, recorded as a separate task.
- **Negative numbers in CSV read as text.** `CsvExport` neutralises any cell starting with `-`, a
  number included, so `-1234` is written `'-1234`. Safe, but a spreadsheet will not sum it. The same
  holds for every CSV the product writes; changing it changes them all, so it is left for a decision.
- **PNG on a very tall dashboard is not measured.** Pages over 6,500 px are split; the largest
  template has not been exported and timed.
- **No end-to-end browser suite.** The project has no Playwright dependency; adding one downloads
  browsers. Component behaviour is covered by Vitest, the API by integration tests, and the layout
  is checked in the browser by measurement.
- **Dashboard names are unique per owner, and that can be probed.** An editor who renames a shared
  dashboard onto a name the owner already uses gets 409; the message no longer names it or says
  whose it is, but the refusal itself confirms the owner has a dashboard of that name. Each probe
  that does not collide really renames the dashboard and is audited, so it is noisy, not silent.
  Closing it fully - for example by not letting editors rename - is a product decision, not made.
- **The old page's parts remain in the tree unused**: `DataFreshnessCard.vue`,
  `VendorMovementCard.vue`, `useFilterState.ts`. Their content moved into widgets; removing them is
  a separate change.
