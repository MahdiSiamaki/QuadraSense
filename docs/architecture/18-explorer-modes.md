# Explorer — one place to look something up, in two modes

**Status:** Built 2026-10-19, on branch `feature/explorer-simple`. Replaces three pages; see
"What moved" for each one's capabilities and where they are now.
**Read with:** `14-relationship-explorer.md` (the relationships and the dual-SIM pair rule, unchanged),
`ADR-008-imsi-search.md` (why an IMSI prefix must be at least ten digits), `ADR-012-explorer-query-engine.md`
(the query builder, now the Advanced mode), `04-security-model.md` (the permissions).

---

## Why

Looking up one identifier took four pages: Subscriber lookup for a number, IMSI search for a SIM, the
Relationship explorer for what any of them is bound to, and the Explorer's summary panel - which
needed `explorer.query`, so most people who may look things up could not open it. Each page had its
own search box with its own rule for what it accepted.

The Explorer now has two modes:

- **Simple** - the default. One search box for a subscriber number (MSISDN), a SIM (IMSI, complete or by
  prefix) or a handset (IMEI), and three views of what it finds: **Details**, **Relationships**,
  **History**.
- **Advanced** - the query builder that was the whole Explorer, unchanged.

The mode is in the address, `/explorer?mode=advanced`, so a link, Refresh and Back keep it. The
identifier never is.

## Who sees what

Nothing about permissions changed on the server: every view calls the endpoint the old page called,
and each endpoint checks the identifier kind's permission and writes the audit entry itself.

| | Needs |
|---|---|
| The Explorer in the navigation, and `/explorer` | any of `explorer.query`, `lookup.subscriber`, `lookup.imsi`, `lookup.imei` |
| Simple mode | any of the three lookup permissions |
| Each identifier type in the search box | its own: MSISDN `lookup.subscriber`, IMSI `lookup.imsi`, IMEI `lookup.imei` |
| Advanced mode | `explorer.query` |

The search box offers only the types the user may look up. A value Auto reads as a type they may not
look up is refused in the page, with the permission named, and nothing is sent.

**ICCID** (since 2026-10-26). The operator's files carry no ICCID; the owner gave the operator's
numbering, which derives it from the IMSI: `8998`, the IMSI's digits 4-7, `000`, its last eight -
nineteen digits (`Sqm.Domain.Identifiers.Iccid`, mirrored in `frontend/src/api/iccid.ts`). Every IMSI
in the feed carries MCC+MNC `43211` (312 million bindings, measured that day), so the rule runs
backwards exactly:

- **Search.** ICCID is offered with the IMSI's permission, and Auto reads 19 digits beginning `8998` as
  one. It is sent as the IMSI it came from, so the IMSI's permission check and audit apply unchanged.
  A 19-digit value that is not of that numbering is refused in the page.
- **Display.** Shown, titled as derived, under every IMSI the Explorer shows - SIM search results and
  summary, a number's bindings, the Relationships graph, the timeline, the Advanced grid - and on a
  model's identifiers. An IMSI that is not 15 digits, or arrives masked, shows none.
- **Exports.** The Explorer's CSV follows each IMSI column with `ICCID (derived from …)`; a risk list of
  SIMs carries one after the IMSI. Masked with it (`IdentifierMask.Iccid`, keeping `8998`, the MNC and
  the last four), because the ICCID gives the IMSI away.

It was listed and disabled before (the owner's decision of 2026-10-19: shown rather than hidden).

A user with `explorer.query` and no lookup permission opens straight into Advanced; one with lookup
permissions and no `explorer.query` sees no mode switch.

## What each view sends, and when

| View | Number | SIM | Handset | Audit action |
|---|---|---|---|---|
| Details | `POST /api/v1/lookup/msisdn` | `POST /api/v1/lookup/imsi/search` (and its history) | `POST /api/v1/explorer/entity` | as before, per endpoint |
| Relationships | `POST /api/v1/relationships/explore` | the same | the same | `relationships` |
| History | `POST /api/v1/timeline` | the same | the same | `timeline` |

A view sends its request **the first time it is opened for an identifier**, not when the identifier is
searched. Searching a number therefore writes one audit entry, not three; opening Relationships writes a
second. The audit log records what somebody looked at, not what a page fetched in case. Going back and
forth between views sends nothing more. Searching the same identifier again sends nothing.

`SimpleExplorer.spec.ts` holds this: made eager (every view mounted on search), three of its six tests
fail on the extra `/relationships/explore` and `/timeline` requests.

## What moved

| Was | Capability | Now |
|---|---|---|
| Subscriber lookup, `/lookup` | Every binding of a number, active and ended, with the device; national and international forms (`0913…`, `+98 913…`, `0098913…`) | Simple, MSISDN → **Details** (`SubscriberBindings.vue`) |
| IMSI search, `/lookup/imsi` | Complete IMSI or a prefix of at least ten digits; breadth shown before searching; filters (active only, device type, manufacturer, change dates); pages of 50; one SIM's dated history | Simple, IMSI. A complete IMSI → **Details** (`ImsiSearchPanel.vue`), history opening by itself as before. A prefix → the list with its filters and pages; choosing a SIM opens it |
| Relationship explorer, `/relationships` | Numbers, SIMs and handsets bound to any one of them; step from one to the next; withheld sections; masking | Simple → **Relationships** (`RelationshipGraph.vue`), for every type |
| — the same page | **The probable other IMEI of a dual-SIM handset**, with the rule's evidence and the caveat that a missing pair does not mean a single-SIM phone | **Relationships**, the "Same physical handset" section - and now from a handset too (below) |
| Explorer summary panel and timeline | Counts, first seen, network age, model, risk verdicts; the timeline | Handset → **Details** (the summary); every type → **History** (the timeline). Both now open to lookup permissions, not only `explorer.query` - their endpoints always checked per kind |

Following any link - a neighbour in Relationships, a SIM in a prefix list, a binding in the History -
opens that identifier in place, puts it in the search box as its own type, and adds a **Back** step. The
trail is page state, never the address or the browser history.

## Old links

| Old | Now |
|---|---|
| `/lookup` | redirects to `/explorer?kind=msisdn` |
| `/lookup/imsi` | redirects to `/explorer?kind=imsi` |
| `/relationships` | redirects to `/explorer` |
| route names `lookup`, `imsi-search`, `relationships` | kept, on the redirects |
| dashboard quick lookup, all three identifier targets | opens the Explorer with the value handed over in memory (`lib/prefill.ts`, never the URL). The "Number, SIM or handset" target opens on **Relationships**, which is what it opened before |

The three entries Lookup, IMSI and Relationships are gone from the navigation; Explorer is shown to
anyone who could see any of them.

## What behaves differently, on purpose

- **`/explorer` opens in Simple.** A bookmark to the query builder now needs `?mode=advanced`, or one
  click on the mode switch. The owner asked for Simple to be the default.
- **Ten digits under Auto is a number**, as the server's `DeviceSearchTerm` reads it. To search ten
  digits as the start of an IMSI, choose IMSI. Eleven to thirteen digits starting `43211` are read as
  an IMSI prefix, because no number has that shape.
- **Eight digits is a model (TAC)**, not an identifier: the page says so and links to the device page.
- **A prefix list's SIM opens the SIM**, with all three views, instead of unfolding its history inside
  the list. A complete IMSI still shows its history in Details, as before.
- **Back sends the lookup again**: it is a new request for an identifier shown before, and is audited as
  one.

## A defect the move exposed: no pair from a handset

Checked against the case that found the twin-TAC scheme (`14-relationship-explorer.md`), a search by
Galaxy A51 IMEI `35446411748626` showed "No pair could be shown" - though its partner
`35446511748626` carries the same two numbers, and a search by either number showed the pair. The
relationship endpoint looked for pairs only when the graph had other handsets, and a handset centre
never has: the old page had the same defect, unseen because the pair had only ever been checked from
a number. Fixed in `ClickHouseAnalyticsStore.Relationships.cs`; `RelationshipPairRealDataTests` holds
the pair from both IMEIs and from the shared number, and with the old gate put back its two handset
cases fail on an empty pair list.

## Verified

Frontend unit tests: `identifier.spec.ts` (21 - the server's reading rules, case by case) and
`SimpleExplorer.spec.ts` (7 - offered types per permission, refusal without a request, one request per
opened view, follow and Back with no identifier in the address, `?kind=` from an old link, the
quick-lookup hand-over, an ICCID searched as its IMSI).

ICCID, 2026-10-26: `iccid.spec.ts` (12) and the server's `IccidTests` hold the rule both ways, the
inputs that have none, and the mask; `ExplorerEndpointTests` and `RiskEndpointTests` hold the export
columns, masked and revealed. Each was made to fail first: the rule off by one digit on the server
(4 failures) and in the page (3), the export's ICCID left unmasked (1).

In the browser, on the real stack, 2026-10-19:

- `/lookup` lands on `/explorer?kind=msisdn` with MSISDN chosen; the navigation shows Explorer and no
  Lookup, IMSI or Relationships entries; ICCID was shown and could not be chosen (it can since
  2026-10-26 - "Who sees what").
- Handset `35446411748626` under Auto: Details is the summary (Galaxy A51, 3 bindings, 2 numbers,
  first seen 2026-02-08); Relationships (95 ms, 427,479 rows read) lists 2 numbers and 3 SIMs and the
  pair `35446511748626`, "2 phone numbers seen on both"; following the pair puts it in the box as an
  IMEI with "← Back to 35446411 748626", and Back returns. History shows the handset's timeline. The
  address held `?kind=imei` and never an identifier.
- IMSI prefix `4321139256`: 50 rows on the first page, the four filters, 793 ms, no views.
- `/explorer?mode=advanced`: the query builder with Build, Templates and My queries, data through
  2026-10-10.

Not verified in the browser: a user who holds only some of the lookup permissions - the check used
the administrator account, and no account with fewer permissions was created for it. The unit tests
cover what such a user is offered; the server's per-kind checks are the existing endpoints' and
unchanged.
