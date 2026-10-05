# ADR-015: New models, network age and data-quality signals

- **Status:** Accepted
- **Date:** 2026-10-05
- **Decided by:** the product owner delegated the open questions of Phase 5 ("decide them as you
  see fit", 2026-10-05); each choice below is recorded with the measurement that made it.

## Context

Phase 5 asks three things: which device models are new to the network, how long a model, handset,
SIM or number has been on it, and which data-quality problems the feed carries. Four facts shaped
the choices.

1. **The data has no launch, manufacturing or activation date.** Only the days a daily file named a
   binding, from 2026-01-26, plus the initial dump - a month of observations (2025-12-27 to
   2026-01-25) with no dates of its own. GSMA's `allocationDate` is when a TAC was allocated, not
   when a handset reached this network.
2. **The feed has defects that look like new models.** From 2026-09-15 IMEIs arrive shifted by a
   digit, which makes a fake TAC out of a real one. Counted naively, September shows 85,290 TACs
   first seen, against 13-16 thousand a month from March to August (measured 2026-10-05, P1).
3. **Answering "first seen" per request is a pass over the binding history.** Looking each of a
   day's handsets up in it read 450 million rows per eighth of a day (measured, P2), far past the
   Explorer's 30 s budget.
4. **Most of what looks like a quality problem is how this feed behaves.** On a 1/64 sample of the
   history, 71% of bindings with an add-to-remove period held it for a day or less (P3b).

## Decision

### New models: first seen in the daily files, from a per-model day table

A model (TAC) is **new** when a daily file first names one of its handsets, and it was not in the
initial dump. Only **countable** IMEIs count - 14 digits, not fourteen zeros, not the shifted shape -
so the September defect adds no fake models. By default the list shows TACs GSMA knows; the other
TACs are one filter away, never hidden.

The question is answered from `sqm.tac_day` (analytics migration 025): one row per day and TAC with
the handsets, SIMs and adds it carried - about 40,000 rows a day. The import writes the day after
its history (`TacDayStep`, 22-27 s at one thread, measured); day 1970-01-01 holds the dump. The 234
days already in the log were backfilled with `--refresh-tac-days` in 75 minutes, 0 failed. "First
seen" then is a GROUP BY over a few million rows.

### Network age: days in this data, never called an age

**Network age** is the days from first seen to the data-through day. For anything in the initial
dump it is a lower bound ("at least N days"), counted from 2026-01-26. It is shown for models, and
for the number, SIM and handset in the Explorer panel; the UI says every time that it is time in
this data, not the age of a handset.

### Data-quality signals: counted with each measures run, shown on their own page

Seventeen categories in three groups - identifier faults of current state, sequences the history
contradicts, and add-to-remove period lengths - defined in `QualityCategories.All` with a meaning
and a base for each. They are counted in the measures run of ADR-014 as a fourth table of chunks
(`sqm.quality_chunk`, analytics migration 026), so they are chunked by number, published under the
same rule (complete, no duplicate key, inputs unchanged) and rebuilt when a day or a GSMA version
changes. Bindings and numbers are exact; SIMs and IMEIs are `uniq()` estimates, labelled so.

They are served by `GET /api/v1/quality/signals` behind `import.view`, beside the per-day feed
quality, and shown on the Data quality page - never on the Risk page, and never as evidence about a
subscriber.

**A short period is shown, not flagged.** With 71% of periods a day or less, a "suspiciously short
lifetime" flag would flag the ordinary. The page shows the distribution of period lengths instead.

## Measurements

All 2026-10-05, on the real data (238 days, 453 million bindings in current state).

| | Measure | Value |
|---|---|---|
| P1 | TACs first seen per month, March-August | 13-16 thousand; 2.3-2.9 thousand GSMA-known |
| P1 | TACs first seen, September, all IMEIs | 85,290 - the shifted-IMEI defect |
| P2 | IMEIs in the initial dump | 88.6 million |
| P2 | IMEIs first seen per month | 5-6.5 million; September 7.7 million |
| P3 | Bindings with no IMEI ('000000') | 10.2 million |
| P3 | Malformed IMEI / fourteen zeros | 1.49 million / 16 thousand |
| P3 | Countable IMEI with a TAC GSMA does not know | 14.0 million |
| P3 | IMSI not 15 digits / number not 10 digits | 1.1 million / 1.1 million |
| P3 | Held since the dump, never mentioned by a daily file | 49.2 million of 122 million held |
| P3b | Adds of a binding already held (1/64 sample) | 3.7% of bindings |
| P3b | Removes of a binding not held (1/64 sample) | 7.2% of bindings |
| P3b | Bindings whose periods all ended within a day | 71% of those with a period |
| Cost | One quality chunk, history pass / current-state pass | about 260 s / 17 s |

## Alternatives rejected

- **First seen computed per request from the binding history.** 450 million rows per eighth of a
  day for the handset lookup alone (P2). The per-model day table answers the same question from a
  few million rows.
- **Keeping "handsets new to the network" per model and day.** The same history lookup, every
  import. Dropped: the device page already shows growth delivery by delivery from
  `agg_device_model`.
- **Network age from GSMA's allocationDate.** It dates the TAC, not this network. It is a useful
  second column (a model first seen today but allocated in 2019 is not a new model) and is proposed
  for the next phase rather than mixed into network age.
- **A separate idle task for the quality counts.** It would need its own chunking, publication rule
  and staleness check, which the measures run already has.
- **Flagging short lifetimes.** See P3b.

## Consequences

- A measures run takes longer by the quality chunks; the risk measures and the quality counts are
  published together or not at all.
- The quality counts depend on the active GSMA version (unknown TAC, shifted IMEI). Activating a
  version changes the run's inputs, so the next run recounts them.
- Until the first run with quality chunks is published, the page says so instead of showing zeros.
