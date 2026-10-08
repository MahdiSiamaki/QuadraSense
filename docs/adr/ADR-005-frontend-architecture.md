# ADR-005 — Frontend architecture: Vue 3 SPA with a custom design system

- **Status:** Accepted
- **Date:** 2026-09-12

---

## Context

The brief sets a high and specific bar: the UI must feel like a modern enterprise product, not a legacy admin
panel, and it states plainly that *using a component library is not the same as good design*. It calls out
Information Architecture, visual hierarchy, typography, spacing, density, interaction, feedback, loading,
empty and error states, and responsive behaviour as the criteria.

It must also survive a genuinely demanding data workload:

- tables over a ~108M-row current state — nothing may be sent to the browser unpaginated
- dashboards with many widget types, each independently loading, cancellable and refreshable
- filter state that is shareable via URL
- long-running uploads and background jobs with live progress

Confirmed constraints that materially simplify the build: **English only, LTR only, Gregorian only.** No i18n
framework, no RTL logical-property discipline, no Jalali calendar library. The team's stated frontend
strength is **Vue.js**.

## Options considered

### Framework

| Option | Assessment |
|---|---|
| **Vue 3 + Vite (SPA)** — chosen | Team's stated strength. Composition API with `<script setup>` and TypeScript gives excellent type safety. Vite's dev experience is the best of the group. |
| Nuxt (Vue + SSR) | SSR/SEO is worthless for an authenticated internal tool, and it adds a Node server to operate on-premise. Rejected: cost without benefit. |
| React / Next.js | Larger ecosystem, but not the team's strength, and nothing here needs React specifically. |
| Svelte | Smallest output, but the smallest talent pool and not a team strength. |

### UI layer

| Option | Assessment |
|---|---|
| **Tailwind CSS v4 + Reka UI (headless) + own design system** — chosen | Reka UI supplies accessible, unstyled primitives (dialog, popover, combobox, tabs); all visual decisions stay ours. Directly serves the brief's demand for real design rather than a library's default look. |
| PrimeVue / Vuetify / Naive UI | Fast to start, but every such app looks like the library. Overriding the theme deeply to escape that costs more than building on headless primitives. Rejected on the brief's explicit terms. |
| Hand-rolled CSS, no framework | Maximum control, far too slow, and accessibility would have to be rebuilt from scratch. |

### Charting

| Option | Assessment |
|---|---|
| **Apache ECharts** — chosen | Covers every chart type in the brief (time series, stacked bar, area, heatmap, pie, treemap, geo). Canvas rendering handles large series where SVG libraries stall. Mature, actively maintained, one dependency for all chart needs. |
| Chart.js | Lighter, but lacks heatmap/treemap/geo and struggles with dense series. |
| D3 directly | Total control, far more code, and we would re-implement what ECharts already does well. |
| Multiple small libraries | Rejected outright — the brief forbids several libraries with overlapping functionality. |

### State

**Server state and client state are separated deliberately**, as the brief requires:

- **TanStack Query (Vue)** for all server state — caching, background refetch, request cancellation,
  retry, and per-widget loading/error status. This is what makes "every widget loads independently and can be
  cancelled" straightforward rather than bespoke.
- **Pinia** for the small amount of genuine client state (auth session, theme, dashboard layout draft).
- **URL as the source of truth for filters**, so any view is shareable and reloadable — a stated requirement.

### Large tables

**TanStack Table + TanStack Virtual.** Headless, so they impose no styling, and virtualisation means row
count in the DOM stays constant regardless of page size. Combined with server-side pagination, no large
result set ever reaches the browser whole.

## Decision

```
Vue 3.5 + TypeScript + Vite
├── Tailwind CSS v4          design tokens, utilities
├── Reka UI                  accessible headless primitives
├── TanStack Query (Vue)     server state
├── Pinia                    client state
├── TanStack Table + Virtual large tables
├── Apache ECharts           all visualisation
├── Vue Router               routing + URL filter state
├── VeeValidate + Zod        form validation, schemas shared with API contract
└── openapi-typescript       generated client from the .NET OpenAPI document
```

Nine runtime dependencies, each solving one problem with no overlap.

### Design system

Built before the first screen, not retrofitted:

- **Type scale** and **4 px spacing scale** as tokens; no arbitrary values in components.
- **Semantic colour tokens** (`surface`, `surface-raised`, `border`, `text-primary`, `text-muted`,
  `accent`, plus status colours) defined once for light and dark, so dark mode is a token swap rather than a
  parallel stylesheet.
- **Density**: this is a data product; default to compact rows and tight vertical rhythm, and let whitespace
  do the structural work instead of decoration.
- **Every async surface defines four states** — loading (skeleton matching final layout), empty, error, and
  loaded. Treated as a component contract, not an afterthought.
- **Motion is functional only** (state transitions, ~150 ms), honouring `prefers-reduced-motion`. The brief
  rules out decorative animation.

### Performance rules

- Route-level code splitting; ECharts imported by chart type, never the full bundle.
- Every list is server-paginated; every long list is virtualised.
- Every query is cancellable; navigating away aborts in-flight requests.
- Exports above 100,000 rows become background jobs with a download link — the browser is never blocked.

## Consequences

- **Positive:** the team's strongest frontend language; a visual identity that is genuinely ours; a single
  charting dependency; server/client state cleanly separated; accessibility inherited from Reka UI primitives
  rather than hand-rolled.
- **Negative:** a headless approach means more up-front work before the first screen looks finished. This is
  accepted deliberately — the brief prioritises the end result over the first-week demo.
- **Negative:** ECharts is a large dependency (~1 MB). Mitigated by per-chart-type imports and the fact that
  this is an internal tool on a fast network.
- **Neutral:** English/LTR/Gregorian removes i18n, RTL and calendar-library work entirely. If Persian or RTL
  is ever required, using logical CSS properties from the start keeps the door open at low cost — so the
  design system will use them regardless.

---

## Amendment, 2026-10-08 — motion, measured against the design audit

The decision above - "**Motion is functional only** (state transitions, ~150 ms), honouring
`prefers-reduced-motion`. The brief rules out decorative animation." - now reads: functional only,
~150 ms typical and 300 ms at most for anything that answers the reader. Three things report work
rather than answer, and run longer by design: the progress fill (500 ms between 1.5 s polls), the
indeterminate sweep (a 1.4 s loop) and the "working" pulses. The design audit
(docs/architecture/16-ui-design-system.md) found what motion had become in practice, and the
product owner settled these questions:

1. **Things that open occasionally arrive, briefly.** The user menu and popovers: 150 ms, opacity
   and a 3% scale from the corner they open from. Dialogs: 200 ms from the centre, the backdrop
   fading with them. No exit animation. They used to appear in a single frame.
2. **A button gives under the pointer**: a 3% scale (the CSS `scale` property) for 120 ms, on the
   `Button` and `Pagination` components, the dialog's close button and the Explorer's icon
   buttons - not on rows, links or segmented controls, which are used too often for it. Buttons
   built by hand were moved onto those components, so every button presses alike.
3. **The settings gear keeps its one flourish**, shortened from 400 to 250 ms; the theme icon's
   sun-to-moon morph from 500 to 300 ms.
4. **Reduced motion means less motion, not none.** Nothing moves, scales or turns; opacity fades
   stay, because they explain a change without moving anything, and so do the opacity loops that
   say "working"; the spinner pulses instead of turning. This supersedes the previous global
   rule, which cut every transition and animation to 0.01 ms - fades included.
5. **What is seen tens of times a day changes at once**: rows, nav links, chart hovers.

The two curves and the press, popover, dialog, turn and morph durations are tokens in
`tokens.css`; nothing animates a layout property. Each was measured in the browser; the evidence
is in the UI doc's Motion section and in the commits that made the change.
