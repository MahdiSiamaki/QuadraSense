# UI design system — layout, charts, responsiveness, and how a UI fix is proven

**Status:** Written 2026-10-07, after the product-wide layout review (PR #28). Motion conventions are
being decided now and will be added as their own section.
**Read after:** `06-repository-structure.md`.

---

## Why it exists

The product owner reported three layout bugs with screenshots on 2026-10-06 — timeline dates drawn
over each other, a large empty area beside the Explorer's entity panel, the Risk page's tab bar
touching the cards — and asked for a fix that holds "in every part of the system". Each of the three
had a cause in a shared component, not in the page it showed on, and the same causes produced
defects nobody had reported yet: device photographs squashed to a 110×25 strip, chart axis names
drawn over labels, pages that scrolled sideways on a phone.

This document is the set of conventions those fixes established, so that the next page is built to
them rather than rediscovering them.

## Foundations

- **Stack.** Vue 3 + TypeScript, Tailwind CSS v4, ECharts 6, TanStack Query. `reka-ui` is installed
  and unused.
- **Tokens.** `frontend/src/design-system/tokens.css` holds colours, radii and shadows, light and
  dark. Use them; never literal colours.
- **Three Tailwind/ECharts traps** (from `CLAUDE.md`, all still live):
  - zrender cannot parse `oklch()` — resolve every chart colour through `lib/chart-colors.ts`;
  - Preflight's `margin: 0` breaks `<dialog>` centring — `Modal.vue` restores it with `m-auto`;
  - `text-[var(--text-xs)]` compiles to a *colour* and `font-[var(--font-mono)]` to a *weight*.
    Sizes and faces use the theme utilities (`text-2xs` … `text-2xl`, `font-mono`).

## Page frame and spacing rhythm

- `main` is `max-w-[1600px]` with `px-5`. Every page root is `flex flex-col gap-5`. No page centres
  or caps its own root: Subscriber lookup did, and its heading started 332px right of every other
  page's at 1900px. A form that should not stretch caps the *form*, left-aligned (`max-w-4xl`).
- Page header: `h1` `text-xl font-semibold`, subtitle `mt-0.5 text-sm text-[var(--c-text-secondary)]`.
- **20px between page blocks**, everywhere. Use the page's `gap`, never margins between siblings.

### AsyncBoundary lays out what it loads

`AsyncBoundary` renders the four async states. Its root is a flex column with a `gap` prop
(`none | sm | md | lg`, default `lg` = 20px), because content rendered through one
`<template v-if>` arrives as several sibling blocks and **the page's own `gap` stops at the
boundary**. That is how the Risk page's warning banner, tab bar and card grid came to touch
(measured 0px; now 20px), and the user and role pages with them.

- Use `gap="none"` where blocks are meant to be flush: a table and its `Pagination` footer (the
  pager has its own `border-t`), or content that spaces itself with margins.
- The skeleton's `min-height` applies only while loading/empty/error; loaded content sets its own
  height (a reserved height left a blank band under short content).

## Cards

`design-system/Card.vue`:

- The header **wraps**. The title keeps at least `10rem` (`flex-[1_1_10rem]`); actions that do not
  fit beside it move to their own line, still right-aligned (`ml-auto`). Before, actions were
  `shrink-0` in a row that could not wrap: at 390px the Vendors card's controls ran 55px past the
  card, the page scrolled sideways and the title was 0px wide. (16rem was tried and made the
  Explorer panel's Back/close buttons wrap inside its 24rem aside — hence 10rem.)
- The body is `flex-1`; `flush` removes its padding for tables and charts.

## Side-by-side panels: never leave dead space

A grid row stretches its items to the tallest. A card whose content cannot grow then shows the
difference as blank space — 930px of it under the dashboard's churn chart when the Vendors list
beside it showed 20 rows. The rules:

1. **Pair like with like.** Two charts, two tables of the same row count (Operating systems now
   lists 10 rows, as Device types beside it does).
2. **A variable-length list beside a fixed-height chart scrolls inside a cap** (`max-h-[14rem]
   overflow-y-auto overscroll-contain` on the Vendors list; `max-h-[20rem]` on a risk list's
   device-type breakdown; `max-h-[28rem]` on relationship lists).
3. **When the two do not belong side by side, stack them** (Data quality: sequences above the
   period-length chart, instead of a 127px-blank card beside it).
4. **As many columns as there can be cards** (Relationships showed three columns for at most two).
5. **An expanding detail opens below the grid, not inside a grid item** (Explorer templates: the
   parameter form opened inside one card and stretched its neighbours).

### SplitView: a list and the thing opened from it

`design-system/SplitView.vue` is the layout for "a list, and the detail panel beside it" — the
Explorer's results and entity panel, the Risk page's lists and entity card.

- **Everything about the selection stacks in the main column**, the timeline included. Placed after
  the two columns, the timeline landed full-width under the taller one and left the space beside
  short results empty (the reported Explorer bug).
- **The aside is sticky and never taller than the window** (`xl:sticky xl:top-20
  xl:max-h-[calc(100dvh-6rem)] xl:overflow-y-auto`), so its lower half is always reachable.
- The Explorer keeps its last result on screen while the next page or sort runs (it used to unmount
  and empty the column), clears it when a run is refused, and ignores paging/sorting of the old grid
  while a new run is pending.

## Charts (ECharts 6)

- Every chart sets `animation: false`.
- **No `grid.containLabel`.** Under ECharts 6, without the legacy plugin, it keeps only axis
  *labels* inside the canvas; axis *names* were laid out at a fixed gap and drawn over rotated
  labels. The default outer-bounds layout contains both.
- **Tooltips** spread `tooltipBounds` from `lib/chart-tooltip.ts`: confined to the chart and wrapping
  at 20rem (a nowrap flagged-day tooltip ran off the window).
- **Time axes** align their first and last labels to the plot's edges
  (`alignMinLabel: 'left'`, `alignMaxLabel: 'right'`) — centred on the end tick, the last date ran
  16px off the canvas.
- Colours only through `lib/chart-colors.ts`.

## The binding timeline's axis

`features/timeline/TimelineChart.vue` is HTML, not ECharts. Its axis observes its own width
(`ResizeObserver`), measures each label in the scale's font (canvas `measureText`), and places
labels only where they fit: the axis's first day in the label column against the origin, its last
day at the right end, Januaries (which carry the year) first, then other months, each skipped — its
tick kept — when it would touch a neighbour or the edge. Percent positioning alone drew
"Dec 27, 2025" over "Jan 2026" (42×9px) at every width.

## Device images

`features/devices/DeviceImage.vue` fills its tile edge to edge (`object-contain`, white plate). Its
placeholder (silhouette + initials) renders only when there is no photo: a `v-else` bound to the
"unverified" badge drew it over every verified photo and squashed the photo to 110×25. Catalogue
images are normalised to the verified images' standard — 800×800, the device's long side 752px —
see ADR-011's amendment.

## Responsive targets

- **No horizontal page scroll at 1920, 390 or 320px** (320px is the WCAG reflow width). Checked on
  all 20 pages on 2026-10-07.
- Selects and inputs are `max-w-full` (a select is as wide as its longest option — "Device for the
  Automatic Processing of Data (APD)" pushed a 320px page sideways); minimum widths are
  `min-w-[min(18rem,100%)]`, never a bare `min-w-[18rem]`.
- Wide tables scroll inside their own wrapper; the permission matrix's wrapper also needs
  `contain-paint` (without it the role headers widened a 390px page by 80px even with
  `overflow-x: auto`).

## Stacking

- The app's top bar is `z-40`, above any in-page sticky layer.
- A table with sticky cells gets `isolate` on its scroll wrapper, so its `z-10` ranks only within the
  table (the permission matrix's first column slid over the top bar).
- In-page sticky toolbars (the image review selection bar) use `top-16` and an opaque surface.

## How a UI fix is proven here

Screenshots of the browser pane at an emulated 1920px are slow and come back scaled and cropped, so
layout claims are proven by **measurement**, and by the same rule as backend fixes — reintroduce the
bug and watch the check fail:

- **DOM sweep** (run in the page): framed siblings closer than 4px (touching), framed grid items
  whose content ends more than 60px above their bottom (dead space), grid columns ending more than
  150px above their row (short column), intersecting text boxes (overlap),
  `scrollWidth − clientWidth` (horizontal overflow). Navigate with the app's router so the helper
  survives; run long loops detached.
- **Canvas charts:** import the dev build's ECharts module, `getInstanceByDom`, and walk
  `getZr().storage.getDisplayList()` text elements for anything outside the canvas or overlapping.
- **Prove the fix:** restore the old file (`git show HEAD:<file> > <file>`), let Vite reload,
  measure the defect, restore the fix.

## Change record

| Date | Change | Commits (PR #28) |
|---|---|---|
| 2026-10-06 | AsyncBoundary gap and load-only min-height | 740db1a |
| 2026-10-06 | SplitView for Explorer and Risk; timeline in the main column | 20f0ca1 |
| 2026-10-06 | Timeline axis labels by measured width | 1bf8e1a |
| 2026-10-06 | DeviceImage placeholder no longer over photos | 3a4142b |
| 2026-10-06 | Card header wraps; chart tooltips confined | 5900648 |
| 2026-10-06 | Dashboard pairs line up | 4f4e5f7 |
| 2026-10-06 | Lookup frame, top bar z-40, sticky settings menu | dd9f972 |
| 2026-10-07 | Charts without containLabel; end labels aligned | 8dc217e |
| 2026-10-07 | Relationships two columns; risk analysis cap | b6c7938 |
| 2026-10-07 | Data quality stacking; template form under the cards | 8fb346b, 97c3944 |
| 2026-10-07 | Matrix contain-paint; 320px fits | b6320de, 373bbe2 |
| 2026-10-07 | Review fixes: pagers flush, panel header, no stale grid | 4c067b3 |
