# Dashboard Review — findings and fixes

**Status:** Complete for the 2026-09-16 review. Every figure below was measured against the
running system at delivery 151 (business date 2026-07-02), TAC version 2 active.
**Read after:** `02-glossary.md`. The vocabulary here — binding, fold, mart, delivery — is defined
there.

---

Five defects, found by reading the dashboard end to end and checking every number it displays
against the store underneath it. Four are fixed. One is recorded rather than fixed, with its
measured impact, because fixing it is a product decision rather than a correction.

## 1. The running total contradicted the headline, by 9,532,541

**The worst of them, and the one a reader could have caught.** The "Daily change" chart drew a
running active-binding total ending at **124,684,132** while the KPI card directly above it read
**115,151,591**. Two figures for one quantity, on one screen, differing by **8.3%**.

The line started at the initial dump and added each day's `added - removed`:

```sql
(SELECT active_bindings FROM sqm.agg_kpi_daily ORDER BY seq LIMIT 1)
    + sum(toInt64(added) - toInt64(removed)) OVER (ORDER BY data_date)
```

The dump is the problem. It lists every binding seen over a **30-day window** rather than at an
instant — it averages 1.57 handsets per SIM — so its 125,939,523 rows are not a population and no
event stream reconciles to them. The evidence that it is the *starting point* and not an
accumulating error: the drift is already **9,549,453** at the earliest delivery with a measured
population, and every later delivery sits within 0.4% of that same figure.

| delivery | measured | forward-anchored | drift |
|---|---:|---:|---:|
| 133 | 114,237,785 | 123,787,238 | 9,549,453 |
| 143 | 111,995,368 | 121,512,240 | 9,516,872 |
| 151 | 115,151,591 | 124,684,132 | 9,532,541 |

**Fixed by anchoring to the end instead of the beginning** — to the population the KPI mart
actually measured — and walking the net backwards:

```
cumulative(d) = anchor - net_through_anchor + running_net(d)
```

Checked against all sixteen deliveries whose population is known: **worst drift 28,554, or
0.025%**, against a constant 9.5 million. The last point is now exact by construction, which is
the one a reader cross-checks. Days folded but not yet in a complete snapshot still extend
forward from the anchor, which is what those days are.

A consequence worth stating: the line no longer begins at 125.9M but at about 116.4M. That is
more honest, not less — the dump's figure was never a population.

## 2. One card answered a question nobody asked

`GET /distribution/{dimension}` declared only `manufacturer` and `vendor`. The dashboard holds a
single filter object and spreads it into every request, and **a minimal API does not bind a query
parameter it has not declared** — so drilling into an operating system, a device type or a TAC
left the Device types card computing the *unfiltered* answer while the page above it displayed a
chip reading "Filtered by OS: Android".

Nothing errored. The store had always applied whatever it was given; the values simply never
arrived. The defect was an **absence**, which is what code review reliably misses.

Fixed by declaring the same filters as `/top/{dimension}`, and pinned by
`DashboardFilterBindingTests`, which reads the router's own endpoint list and the handlers' real
signatures and fails the build if a dashboard endpoint stops binding a filter the dashboard
sends. **Verified by reintroducing the original defect and watching the test fail.**

## 3. Two cards are network-wide and did not say so

`agg_device_class_daily` and `agg_capability_daily` are rolled up to `(delivery, measure, class)`.
There is no vendor or OS in them to filter on, and answering from the raw table would cost seconds
per card — so Device class mix and Network & SIM capability are network-wide by design.

That design is defensible. Showing network-wide figures under a page heading that has just been
labelled "Filtered by Vendor: Samsung" is not. Both now say so, but only while a filter is
active, so the note appears exactly when it changes the reading.

## 4. Business dates shifted a day for half the world

`formatDate` turned `2026-07-02` into `2026-07-02T00:00:00Z` and formatted it in the reader's own
timezone. A business date is a calendar day with no time of day, so this renders the day *before*
for everyone west of UTC:

| timezone | `2026-07-02` renders as |
|---|---|
| UTC | Jul 2, 2026 |
| Asia/Tehran | Jul 2, 2026 |
| America/New_York | **Jul 1, 2026** |
| America/Los_Angeles | **Jul 1, 2026** |

It affected the freshness card's "Data through", the missing-day list, the vendor window label
and every business date in the import history. Fixed by formatting business dates in UTC.
Timestamps are the opposite case and stay local: an import that ran at 08:31 happened at a moment,
and the reader wants it in their own time.

Not covered by a test: the frontend has no test runner, and adding one was outside this review.

## 5. Numbers written into prose, which nobody updates

Two footnotes asserted figures that had gone stale:

| claimed | actual |
|---|---|
| "for Samsung, 52.7M bindings against 39.3M handsets" | 49.88M and 36.62M |
| "VoLTE in 2 of 270,166 records" | 2 of **270,885** since TAC v2 was activated |

The VoLTE footnote now reads the active version's row count from the API. The Samsung one no
longer quotes figures at all — the sentence explains why bindings exceed handsets, and the
example was never load-bearing. A wrong number is worse than no number.

This is the same defect the missing-days footnote was fixed for earlier, and the comment there
already named it: a hard-coded list "happened to be right, which is exactly the problem".

---

## Recorded, not fixed: activating a TAC version does not rebuild the marts

The snapshot marts carry manufacturer, model, OS and device type resolved against whichever TAC
version was active **when the refresh ran**. Activating a new version changes `sqm.tac`
immediately — the Devices pages read it live — but leaves the dashboard's marts describing the
previous one until the next daily import rebuilds them.

Measured for the activation of version 2 on 2026-09-16: **11 bindings**, out of 115,151,591. The
719 TACs the new version adds are almost entirely unseen on this network, so the dashboard was
never meaningfully wrong.

That is luck rather than design, and a larger GSMA revision would not be so kind. The fix is to
refresh on activation, which costs the full ~14-minute mart rebuild and is therefore a decision
about how TAC activation should behave rather than a correction to make unasked. Recorded here so
it is a known property and not a surprise.

## UI defects, found by the product owner and fixed

Three, reported from screenshots on 2026-09-17. All three were measured in the browser against
this application's own stylesheet before being changed, and the first was measured again from the
painted canvas pixels afterwards.

### Hovering a bar made it disappear

Every chart, both themes. The design tokens in `tokens.css` are `oklch()`, and ECharts renders to
canvas where the browser understands that string perfectly - so bars drew correctly. But ECharts
does not only paint a colour, it *computes* with it: the default hover state is the base colour
lifted, through zrender's own parser, which handles hex, `rgb()`, `rgba()`, `hsl()` and `hsla()`
and returns `undefined` for anything else. Measured against the installed version:

```
lift('#4f79e8')             -> 'rgba(86,133,255,1)'
lift('oklch(58% 0.16 264)') -> undefined
```

So the hover fill resolved to nothing and the bar vanished. Confirmed from the canvas itself, by
reading the pixel inside a bar before and during hover:

| | before hover | on hover |
|---|---|---|
| oklch token, as shipped | `rgba(72,116,216,255)` | **`rgba(0,0,0,0)`** |
| resolved to `rgb()` | `rgba(72,116,216,255)` | `rgba(79,127,237,255)` |

`lib/chart-colors.ts` now resolves a token by painting one pixel and reading it back, which asks
the browser for the colour it would actually have drawn. That is the right answer by definition,
and stays right if the tokens ever move to `color()` or `lab()`. The three ECharts components -
`BarChart`, `ChangeTimeSeries`, `ChurnTimeSeries` - all use it; the other widgets are plain CSS,
where `var()` works and there was never a bug.

### Modals opened in the corner, not the middle

A modal `<dialog>` is laid out in the top layer against `inset: 0`, so the user agent centres it
with `margin: auto`. Tailwind's Preflight resets `margin: 0` on every element, `dialog` included,
which silently removes that. Measured in this app: computed margin `0px`, dialog at `left 0,
top 0`. With `margin: auto` restored it centres. One class on the shared `Modal.vue`, so every
dialog in the product is fixed at once.

### The lookup Search button sat below its input

The form was `flex items-end`, and the accepted-formats help text was *inside* the flex item - so
the button aligned to the bottom of the whole column rather than to the input. Measured at **26px
low**. The help text is now a sibling of the row, which is 0.

Still carrying a magic number: `ImsiSearchPage` aligns its button with `pt-[1.55rem]` against
`items-start`. It is correct today and fragile by construction, but it was not what was reported
and changing it was not worth the churn in the same pass.

## Also noted

`.env` and `.env.example` are the same file and both carry `change_me` for the ClickHouse and
PostgreSQL passwords, while the application actually connects with `sqm_dev` from
`appsettings.Development.json`. Nothing is broken — the application never reads `.env` — but
anyone following `.env` to run a query by hand gets an authentication failure that looks like a
server problem.

## Checked and found correct

Worth recording, so the next review does not repeat the work:

- **The enrichment breakdown genuinely sums to the total.** `tac` is a materialised column defined
  as `if(length(imei) = 14, substring(imei, 1, 8), '')`, so the unknown-device sentinel and
  malformed IMEIs cannot carry a TAC and the four categories are disjoint by construction.
- **Every mart read selects its delivery from `sqm.mart_ready`**, not `max(seq)` of the mart
  itself, so a partly-built refresh cannot be served.
- **Every read of `binding_current` uses `FINAL`.**
- **No SQL is built from user input.** Dimensions are an enum allow-list, columns come from
  `AnalyticsSchema`, and every value is a bound parameter.
- **The charts pass ISO date strings straight to the category axis**, so they were never affected
  by defect 4.
