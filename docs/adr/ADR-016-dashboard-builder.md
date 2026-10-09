# ADR-016: The dashboard builder

- **Status:** Accepted
- **Date:** 2026-10-09
- **Decided by:** the product owner asked for a configurable, persisted, shareable dashboard and
  delegated the usual architecture, design, widget grouping and defaults ("decide them, do not wait
  for confirmations", 2026-10-09). The decisions below are recorded with the evidence behind each.
  Two of them carry an explicit product-owner instruction from the same request and are marked so.

## Context

The dashboard at `/` was one Vue page, `features/dashboard/DashboardPage.vue`, with fixed grids
calling eight hooks from `api/dashboard.ts`. Nothing about it could be saved, shared or arranged.
The request is for several dashboards per person, an edit mode with add / configure / remove /
duplicate / arrange, save and cancel, sharing with users and roles, and export - persisted on the
server, so that it survives a reload, a new sign-in and another browser.

What was measured or read before deciding (2026-10-09, branch `feature/dashboard-builder`):

1. **The data is daily, aggregated and in three permission families.** Every analytic a widget can
   show already has an endpoint: `dashboard.view` for the population marts, `device.view` for new
   models and network age, `import.view` for freshness, imports and feed quality, `risk.view` (plus
   a lookup permission per rule family) for risk, `explorer.query` for the Explorer. None of them is
   a stream; the newest data is a daily file, typically weeks old.
2. **Most current figures are network-wide or current-population only.** KPI, top values and
   distributions honour the vendor / device type / OS / TAC filters; device-class mix, capability,
   daily change, churn and vendor movement ignore them; none of them is a historical snapshot. Risk
   rules have fixed windows (7 / 20 / 30 days, or all time).
3. **No safe user directory exists.** `/api/v1/users` needs `user.view` and returns email, provider
   and roles. A share picker needs its own, narrower endpoint.
4. **GridStack 14.0.0 with its official Vue 3 wrapper (`gridstack/dist/vue`) works with this
   toolchain, with two traps** - evaluated in a scratch app on the project's exact `package-lock`
   (Vue 3.5.42, TypeScript 6 with `noUncheckedIndexedAccess`, Vite 8.3, vue-tsc 3.3):
   - `vue-tsc -b` and `vite build` pass. One option was renamed in 14: `float: false` is now
     `mode: 'top'`.
   - **Declarative `<GridStackItem>` children render nothing**: they mount before the parent grid is
     initialised, so 0 of 2 items appeared. The wrapper's working model is GridStack-owned items
     (`children` / `addWidget` naming a `component`) with Vue content teleported in.
   - With that model, measured in the browser: toggling static/edit, renaming through a store,
     moving with `update()`, adding with `autoPosition`, dragging by the header handle and resizing
     re-mounted **nothing** (`mounts` stayed 1 per widget, `unmounts` empty; a counter inside a
     widget kept its value). Dragging from a widget's body did not move it. Removing a widget
     unmounted exactly that widget's component.
   - **GridStack's own responsive `columnOpts` loses the saved layout**: after 12 → 1 → 12 columns a
     widget saved at 0,3 came back at 4,2, and no `change` event reported it. Switching columns from
     the adapter (`column(n, 'none')` plus explicit positions) restored the layout exactly and
     emitted 0 change events.
   - `alwaysShowResizeHandle: true` is what keeps the resize handle visible in edit mode (the
     per-widget `resizable.autoHide` is overridden by it, `dd-gridstack.js:54`).
5. **Writes of user content already have a precedent.** `explorer.saved_query` (operational
   migration 013): JSONB definition, owner-scoped SQL, results never stored, audited by id and name.

## Decision

### 1. Storage: PostgreSQL, one JSONB definition per dashboard, relational access

Operational migration `016_dashboards.sql`, schema `dashboard`:

| Table | Holds |
|---|---|
| `dashboard.dashboard` | `id`, `owner_user_id`, `name`, `description`, `definition jsonb`, `schema_version`, `revision`, `source_template`, `created_at`, `updated_at`, `updated_by_user_id` |
| `dashboard.dashboard_grant` | `dashboard_id`, exactly one of `user_id` / `role_id`, `level` (`viewer`/`editor`), `granted_at`, `granted_by_user_id` |
| `dashboard.favorite` | `(user_id, dashboard_id)` |
| `dashboard.user_default` | `user_id`, one of `dashboard_id` / `template_key`, or neither |

Widgets and layout live **inside** the definition: a dashboard is read and saved whole, and a table
per widget setting would buy nothing but joins. Ownership, grants and preferences are relational
because they are what access is decided from. **No result is ever stored** - a definition is
configuration, recomputed against the data and the permissions of whoever opens it.

Metadata never goes to ClickHouse, and never only to `localStorage`.

Limits, configurable under `Dashboards` (`DashboardOptions`), defaults: 24 widgets, name 1-100,
description 1,000, widget title 120, one widget's config 4 KiB, the whole definition 128 KiB, 100
grants. The database checks the name, description and a 256 KiB ceiling as a backstop.

### 2. Optimistic concurrency on `revision`

Every update sends `expectedRevision`. The server validates, checks access, writes and increments
`revision` in one statement guarded by `WHERE revision = @expected`; a mismatch is **409** with the
current revision, who changed it and when. The client keeps the draft and offers *Reload the saved
version* or *Save as a copy*. Nothing overwrites without the revision check.

### 3. The widget contract: one allow-list, read by both sides

`frontend/src/features/dashboard/registry/widget-contract.json` lists every widget type: its stable
`type`, `configVersion`, group, the permissions it needs (fixed, or chosen by a config field - a
risk list's lookup permission follows its rule), and every config field with its kind, allowed
values, bounds and length. The API embeds the same file (`Sqm.Application.csproj`), and
`DashboardDefinitionValidator` checks a definition against it: unknown types, unknown or extra
config fields, values outside their bounds, layouts outside the 12-column grid, duplicate or
malformed instance ids, and sizes over the limits are refused with the path of the first fault.
A Vitest test checks the TypeScript registry against the same file, so a type cannot exist on one
side only. Adding a widget is therefore three edits - the contract, the registry entry, the
component - and the tests name the one that is missing.

The registry is code. It runs no plugin, no user JavaScript and no SQL.

### 4. Definition and instance

```jsonc
{
  "schemaVersion": 1,
  "defaults": { "countBy": "bindings", "includeUnknownDevice": true, "range": "all",
                "vendor": "...", "deviceType": "...", "operatingSystem": "...", "tac": "..." },
  "refreshMinutes": 0,                                  // 0 | 5 | 15 | 30
  "widgets": [
    { "id": "w_k3x9…", "type": "metric.kpi", "configVersion": 1,
      "title": null, "subtitle": null,
      "config": { "metric": "activeBindings", "display": "compact" },
      "overrides": { "countBy": "handsets" },           // only fields the type supports
      "layout": { "x": 0, "y": 0, "w": 2, "h": 3 } }
  ]
}
```

What is never serialised: DOM, component instances, results, tokens, the viewer's effective
access, functions. Instance ids are `w_` plus random base-36, unique per definition and kept by
duplicate-dashboard (a copy is a different dashboard) but replaced by duplicate-widget and import.

Definition upgrades are deterministic functions in `registry/migrations.ts`, tested; the server
accepts only current versions and the client upgrades before it saves or imports.

### 5. Layout: 12 columns stored, tablet and phone derived; the adapter owns breakpoints

Only the desktop layout (12 columns) is stored. Tablet (6 columns, under 1024 px of grid width) and
phone (1 column, under 640 px) are **derived** from it in reading order, every time, so a resized
window can never write over the saved desktop arrangement. `DashboardGrid.vue` is the only file
that talks to GridStack: it switches columns itself (evidence 4), commits positions only after a
user `dragstop` / `resizestop` (one history entry per gesture, never per frame), and treats
GridStack's events after programmatic updates as nothing. Vue owns the widget content.

Drag and resize are desktop-only gestures. Everywhere, and for keyboard and touch users, the card's
menu offers *Move up / down / to start / to end* and size presets (*Small, Medium, Large, Full
width*), which edit the same stored layout (WCAG 2.2, 2.5.7).

### 6. Edit lifecycle

Edit opens a **draft** - a copy of the saved definition - in a small Pinia store; the saved version
stays in TanStack Query, so there is one copy of server state. Add, remove, duplicate, configure and
each finished drag or resize are history entries (undo / redo, 100 deep). Save sends the whole draft;
Cancel throws it away. A dirty draft is guarded on Cancel, on route leave and on tab close. Save is
busy and single-flight; a failed save keeps the draft.

A widget whose type is unknown, retired, of a newer config version than this build knows, or no
longer permitted renders a fallback card with the reason and, in edit mode, *Remove*. One broken
card never blanks the dashboard (an error boundary per card).

### 7. Templates in code; `/` keeps its meaning

Templates are read-only, versioned definitions in `features/dashboard/templates/`: **Device
Population** (the former page, every card and every caveat kept), **Security & Risk**, **Support**
and **Data Operations & Quality**. Using one creates an independent private copy; widgets the
person may not use are left out and listed with the reason.

`/` (route name `dashboard`) opens the person's default: a dashboard, a template, or - when nothing
is set - Device Population. Its URL lens is unchanged, so every old link (`?vendor=…`,
`countBy=handsets`, `hideUnknown=1`) means what it meant. A saved dashboard is `/dashboards/:id`.
Templates are never seeded as rows, so a new dashboard or template changes nobody else's state.

### 8. Filters, the lens and their order

Each type declares `supportedFilters` (vendor, deviceType, operatingSystem, tac, includeUnknown),
`supportedMeasures` (count units) and `time` (`current`, `series`, `range`, `fixedWindow`, `none`).
Precedence is visible and fixed: **dashboard defaults → the URL lens → an explicit widget
override**, and only for fields the type supports. A widget that cannot apply a filter says so on
its face (*Network-wide*, *Current population*, *Fixed rule window*); an override shows a badge on
the card and in exports. The query key carries the effective data parameters and nothing visual,
so a palette or title change never refetches.

Relative ranges (*last 30 / 90 / 180 / 365 days*) are counted back from the **latest day in the
data**, not today: files arrive weeks late. A missing day is a gap, never a zero.

`msisdnPrefix` is not a lens field: no MSISDN, IMSI or IMEI goes into a URL, a stored default or a
shared definition.

### 9. Access: dashboard access and data access are two checks

| Who | Can |
|---|---|
| Owner | everything, if they still hold the permission the act needs |
| Editor grant | change layout and widgets, if they also hold `dashboard.edit` |
| Viewer grant | open it |
| `dashboard.manage` | share, delete and edit any dashboard - an administrator's override |

New permissions (operational migration 016): `dashboard.create`, `dashboard.edit`,
`dashboard.share`, `dashboard.manage`. Analyst and Data Operator get the first three, Administrator
all four, Viewer none - a viewer opens what is shared with them and keeps default and favourites.
**None of them grants anything to data.** A widget always queries as the person looking at it,
with their own permissions, cache and masking; a card they may not use renders "needs
`risk.view`" and sends no request. Sharing, re-sharing, deleting and grants belong to the owner
(with `dashboard.share`) and to `dashboard.manage`; an editor cannot widen access. Every read
resolves access from the database again, so a revoked grant, a changed role or a deactivated
account takes effect on the next request. Another person's dashboard id answers 404.

The share picker uses `GET /api/v1/dashboards/share-directory?q=`: active users (display name,
username) and roles (display name), at most 20 of each, at least two characters, for holders of
`dashboard.share` or `dashboard.manage`. No email, no login data, no role membership. A copied link
is a link: it grants nothing.

### 10. Risk and Explorer widgets

Risk aggregates (status, overview per rule, distribution, device types, daily SIM-change
evidence) name nobody and load on open. The **risk list** is a widget type whose eight rules are
presets of one type, not eight types. *Product-owner instruction, 2026-10-09:* its config may carry
a view (`risk` / `dataQuality`), device types, page size and a threshold override - but only within
what `POST /risk/list` accepts (`T + 1 >= floor`, refused below), defaulting to the configured
threshold from `/risk/status`. An override is a view parameter of that widget, shown as such; it
never changes the central calibration in `RiskOptions`. Lists run **only on an explicit Run**,
never on open, focus or a timer, because every run is audited and names people.

The **aggregate Explorer** widget stores a structured query that can be shared: aggregated, and
built only from fields that need no lookup permission (TAC, brand, model, manufacturer, device
type, OS, dates, activity). Identifiers can be counted (`CountDistinct`) but never filtered,
grouped or shown, so a shared definition can never hold an MSISDN, IMSI or IMEI. It runs on an
explicit Run, re-validated for the viewer, and never refers to a private My Queries entry - those
stay private.

### 11. Export

- **Snapshot (PNG) and Print / Save as PDF** render from `POST /api/v1/dashboards/export/snapshot`:
  the server validates the definition, checks dashboard access, `data.export` and each widget's own
  data permission, loads each permitted aggregate through an allow-listed adapter (the same stores
  the dashboard endpoints call), and audits the export. The page renders that data with the same
  widget components in a fixed light report style, with polling stopped. Every card carries its own
  as-of; the header never claims one atomic timestamp for sources that differ. A card that is
  forbidden, not ready or failed is shown as such, or left out by the person's choice, never
  silently. Risk lists and Explorer queries are re-run through their own audited endpoints.
  Print is the browser's print to PDF, named so. No server PDF service.
- **CSV** is written on the server with `CsvExport` and its formula neutralisation: one widget, or
  several as a ZIP. The file says what it is - *visible rows*, *top N* or *full result* - and a
  capped one is never called full. Risk lists and Explorer queries use their existing exports.
- **Config JSON** is the definition with name and description: no results, owner, grants or
  preferences. Import runs it through the same migrations and validator and creates a new private
  dashboard with new instance ids. It carries no data and no access.

Data exports need `data.export` on the server, and are audited as `data.export`; config export,
import and every change of a dashboard or its grants are audited by id and name (`dashboard.*`),
never with a query value.

### 12. Data and cost

Widgets read published marts through the existing endpoints, so `mart_ready`, the published-run
rule and every query budget apply unchanged; nothing here triggers a rebuild. Instances with equal
parameters share one query (six KPI cards are one `/kpi` request). Refresh is manual, with an
optional 5 / 15 / 30-minute interval for aggregates only; audited widgets never refresh by
themselves. Widgets below the fold mount when they come into view; print and export mount them all.
Population trend and period comparison are small transforms over `/daily-changes` rows (about 240
a year), not new aggregations.

## Rejected

- **`localStorage` persistence.** Fails "another browser" and "shared"; and per-user state in local
  storage survives a sign-out here (`lib/theme.ts` is the only user, and it is not personal).
- **Widgets and layout as rows.** A dashboard is read and written whole; rows per widget add joins,
  migrations for every new setting, and partial-save states.
- **GridStack's `columnOpts`.** Measured to lose the saved layout after a breakpoint round trip
  (evidence 4).
- **Declarative `<GridStackItem>`.** Renders nothing in GridStack 14 (evidence 4).
- **A second grid library** (vue-grid-layout and similar). GridStack passed the evaluation; the one
  defect found is avoided by owning breakpoints, which a simpler library would need too.
- **Templates seeded into the database.** A seed per person changes everybody's state when a
  template is added, and a seed re-run could overwrite edits.
- **A server-side PDF renderer.** No measured need; the browser prints to PDF.
- **Sharing by link.** A link that grants access would be an access-control bypass; anonymous links
  are out of scope.

## Out of scope

Alerting, scheduled delivery, public links, confirmed-fraud case management, real-time streaming,
and any widget without a real source: traffic, location, fraud maps, ticket SLAs, online
subscribers, VoLTE.

## Consequences

- Three new permissions to grant, one to reserve for administrators; `AccessControlTests` keeps
  the catalogue and the code aligned.
- A widget type is a three-file change, and the tests say which file is missing.
- The API build reads one file from `frontend/` (the widget contract). A backend built without the
  repository's frontend folder fails at compile time, by name, rather than accepting anything.
