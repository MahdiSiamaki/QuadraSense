# UI design system — layout, charts, responsiveness, and how a UI fix is proven

**Status:** Written 2026-10-07, after the product-wide layout review (PR #28). Extended 2026-10-08 with
Phases 1 and 2 of the design audit (below). Motion conventions beyond the two settled here wait for
the owner's Phase 3 decisions.
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
- **Three more, found by the design audit (2026-10-07):**
  - **An unlayered rule beats every utility.** Tailwind's utilities live in `@layer utilities`;
    a plain rule in `tokens.css` outranks them whatever its specificity. `* { border-color }` at
    top level painted 74 of 76 coloured borders grey on five pages, and `:focus-visible`'s radius
    squared every round control while focused. A default a utility may override goes in
    `@layer base`.
  - **Vue renames keyframes in `<style scoped>`** (`indeterminate` became
    `indeterminate-af83c61a`), so a utility class that names them animates nothing. Keyframes
    that utilities use are `@theme` tokens (`--animate-<name>` plus its `@keyframes`) in
    `tokens.css`.
  - **ECharts' tooltip ignores `animation: false`.** It defaults `transitionDuration` to 0.4s
    and throttles repositioning to 50ms; `tooltipBounds` sets it to 0.

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
- **Tooltips** spread `tooltipBounds` from `lib/chart-tooltip.ts`: confined to the chart, wrapping
  at 20rem (a nowrap flagged-day tooltip ran off the window), and `transitionDuration: 0`, so the
  tooltip follows the pointer instead of trailing it for 400ms.
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

## Controls

- **One button.** `Button.vue`'s four variants are the same height: primary and ghost carry a
  transparent 1px border, as secondary and danger carry a visible one (30.56px against 32.16px
  before). A hand-rolled button is a smell; `AsyncBoundary`'s "Try again" was one.
- **A disabled control does not answer the pointer.** Hover backgrounds are `enabled:hover:`
  (Button, the pager), or `not-aria-disabled:hover:` where a control is disabled through
  `aria-disabled` so that its reason still shows on hover (`SegmentedControl`).
- **A label that names a control operates it.** `Toggle`'s label is `for` the switch, so the
  words toggle it, not only the 36×20px track.
- **A two-to-five-way choice is `SegmentedControl`**, never hand-written. One look (joined,
  26px, the chosen option on the accent-subtle ground) and the WAI-ARIA keyboard contract: one
  Tab stop on the chosen option; arrows, Home and End move between enabled options and wrap; in
  a radio group the arrows also choose, in a tab row (`tabs`) they only move focus and Enter or
  Space opens the tab, because several panels behind tabs run a query. A tab row's panel carries
  `role="tabpanel"`, the `panel-id` it was given and `aria-labelledby` from `tabId()`. Modifier
  keys are left to the browser. A choice the arrows can reach must be cheap to undo: the Explorer
  parks each dataset's query rather than discarding it when the other is chosen.
- **A menu of two links and a button is a disclosure**, not `role="menu"` (the user menu):
  `aria-expanded` and `aria-controls`; Escape closes it and returns focus to its button; tabbing
  past the last item closes it. A menu role promises arrow keys and roving focus.
- **Dialogs.** A form dialog's primary button is `type="submit"` and the dialog listens for
  `submit`, so Enter submits; a destructive confirmation does not listen, so it needs its button.
  Focus starts on the first field (`autofocus`, which the browser's dialog focusing steps
  honour), never on the close button, which is 32×32.
- **What exists only in a `title` is out of reach of a phone and a keyboard.** A KPI card's
  definition and exact figure are also behind a small "i" button, in a popover over the card -
  over, not inside, because a card that grows stretches its whole row.
- **A focus ring inside a clipping frame is drawn inside** (`focus-visible:-outline-offset-2`):
  segmented controls, the user menu's items.

## Colour for words

Status colours are light enough for a dot, a bar or a border, not for a word at 11-12px: warning
text measured 2.42:1 on its own subtle ground, success 3.57:1, danger 4.40:1, under WCAG AA's
4.5:1. So each has a text token at a reading weight, the same hue:

| Token | Light | Dark |
|---|---|---|
| `--c-warning-text` | 52% — 5.00:1 on warning-subtle | = `--c-warning` — 7.22:1 |
| `--c-success-text` | 50% — 4.98:1 on success-subtle | = `--c-success` — 6.09:1 |
| `--c-danger-text` | 52% — 5.23:1 on danger-subtle | 70% — 5.37:1 |
| `--c-text-muted` | 53% — 5.27:1 on white, ≥4.53:1 on every subtle ground | 65% — 5.48:1, ≥4.53:1 |

**Words use the `-text` tokens; marks - dots, bars, tracks, borders - keep the plain ones**, so a
status dot is the same colour in every component. A tone map that colours both keeps two keys,
`fg` and `dot` (StatusBadge, PlanSummary, RiskLevelBadge). An icon that identifies a control needs
3:1 (the light-theme sun uses `--c-warning-text`). Contrast is computed, not judged: OKLCH →
linear sRGB → WCAG luminance, against every ground the text is placed on. Never dim a row with
`opacity` to de-emphasise it - it takes the row's status text below AA; quieten its name instead.

## Figures and words

- **Every number goes through `lib/format.ts`**, fixed `en-US`. Never `toLocaleString()`: it uses
  the browser's locale, and a fa-IR browser printed Persian digits in the pager.
- **Signed decimals** use `formatSignedDecimal` (grouped, never "-0.00"); their colour is chosen
  from the value as shown, not the raw one.
- **A share above zero never reads 0.** `formatPercent` prints "<0.1%" (or "<0.01%") when a
  positive value would round to zero.
- **A count appears once it is known.** "0 total" during the first load read as "no users".
- **One and many.** "1 model", "1 IMEI", "1 of 30 days has data".
- **A missing value is left out, not drawn as an orphaned dash** ("TAC · 200.0 MB", not
  "TAC · — · 200.0 MB"), and a figure that cannot exist is said in words ("New since the first
  delivery"), never shown as 0.

## Motion — settled so far

The brief and ADR-005 allow functional motion only (~150ms), and the global reduced-motion rule
in `tokens.css` sets every duration to 0.01ms. Two things are settled; the rest (tokens, press
feedback, menu and dialog entry, theme switching, the gear and theme-icon timings, what reduced
motion should mean) is Phase 3 of the design audit and waits for the owner.

- **Hovering a chart has no motion** (tooltip `transitionDuration: 0`).
- **The indeterminate sweep** (`ProgressBar`, an import stage that cannot be counted) is the
  `--animate-indeterminate` token: translate −100% → 300% of a bar a third of the track, 1.4s.
  Under reduced motion it is a faint full-width band, because the global rule would otherwise
  leave a still third that reads as 33%.

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

## Design audit, 2026-10-07

The product owner asked for the design to be measured with Emil Kowalski's design-engineering
skills. Four reviewers read the frontend, one per skill — motion (`improve-animations`),
realistic worst-case data (`break-ui`), component polish (`emil-design-eng`) and phones
(`mobile-native`) — and four separate verifiers then tried to refute each of the 56 findings at
its file:line. None was refuted: 37 confirmed, 9 confirmed with a corrected location or
severity, 10 needing a real device. Final severity: 3 high, 14 medium, 39 low; 13 proposed fixes
were corrected on the way (in Tailwind v4 `scale-*` sets the `scale` property, which a
transition on `transform` does not ease, for one).

The owner chose to take the work in phases, asked before each:

| Phase | Scope | State |
|---|---|---|
| 1 | Defects with no taste decision: the cascade, the frozen sweep, wrong device figures, tooltip glide, import pages, figures and words, controls | **Done** on `feature/design-audit-fixes`; the cascade change approved from before/after screenshots |
| 2 | Accessibility and keyboard: user menu name and keyboard model, Enter in dialogs and initial focus, segmented-control focus ring and arrow keys, light-theme status-text contrast (warning text 2.42:1) | **Done** on `feature/design-audit-phase2`; the owner chose a disclosure for the user menu, one `SegmentedControl`, the text tokens with darker muted text, an "i" button on KPI cards, and one parked query per Explorer dataset |
| 3 | Motion system: tokens, press feedback, menu and dialog entry, theme switching, gear and theme-icon timings, reduced motion | Waiting; every item is the owner's decision |
| 4 | Phones: 16px fields on touch screens (iOS zooms on focus), charts that trap vertical scrolling, `dvh`, `theme-color`, ungated scoped hovers, `select-none` on control chrome | Waiting; half need a real device |
| 5 | Data decisions: GSMA's "Not Known" brand sentinel (moves approved image keys), length limits on user names | Waiting; the owner's call |

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

Design audit, Phase 1 (`feature/design-audit-fixes`):

| Date | Change | Commit |
|---|---|---|
| 2026-10-08 | Device page: no percentage change for a model new since the first delivery | e0c0da5 |
| 2026-10-08 | Indeterminate sweep moves (keyframes as a theme token) | 74481a3 |
| 2026-10-08 | Chart tooltips without glide | 2dfd0a3 |
| 2026-10-08 | Import pages: "Not finished", a wide preview scrolls in its card | 292754b |
| 2026-10-08 | Figures and words: en-US counts, plurals, "<0.01%", counts once loaded | 4a65eae |
| 2026-10-08 | Controls: no hover while disabled, switch label, Try again is a Button | 21ef991 |
| 2026-10-08 | Base defaults in `@layer base`: coloured borders and round focus rings show | a7541c2 |
| 2026-10-08 | Every button variant the same height | 9da4ae6 |

Design audit, Phase 2 (`feature/design-audit-phase2`):

| Date | Change | Commit |
|---|---|---|
| 2026-10-08 | Status and muted text pass WCAG AA in both themes | fe6a7fd |
| 2026-10-08 | `SegmentedControl`: one component, the radio and tabs keyboard contracts | c2115bd |
| 2026-10-08 | Explorer parks each dataset's query | cea2ff1 |
| 2026-10-08 | User menu as a disclosure, named on phones | 93e5b31 |
| 2026-10-08 | Dialogs: Enter submits, focus starts on the first field | 3ff2755 |
| 2026-10-08 | KPI definition behind a focusable "i" button | 17431bf |
| 2026-10-08 | Focus after a failed sign-in, theme cards, upload input | 00f4c49 |
