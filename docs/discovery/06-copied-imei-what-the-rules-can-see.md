# Discovery addendum — copied IMEIs: what the current rules can and cannot see

**Date:** 2026-10-19, on the data through 2026-10-10.
**Status:** Documentation only, by the product owner's instruction. Nothing here changes a rule, a
threshold or a measure, and nothing new is built. It says what the risk rules of ADR-014 detect
about one IMEI on several handsets, what they cannot, and what data would be needed to tell.
**Read with:** `docs/adr/ADR-014-risk-signals.md` (the rules, thresholds and measurements quoted
below), `docs/architecture/15-feed-quality.md` and `05-shifted-imei-verification.md` (feed defects
that look like copying).

---

## The question

A copied (cloned) IMEI is one identity carried by more than one physical handset. The claim to be
tested is never "this IMEI has had many SIMs" - phones are resold, shared and re-SIMmed - but "two
handsets used this IMEI **at the same time**". That distinction decides everything below.

## Three things that are easy to confuse

| | What it is | What this feed can say about it |
|---|---|---|
| **Historical count** | How many SIMs (or numbers) an IMEI has ever been bound to | Exactly that, as `sims_ever`, since the initial dump window. Says nothing about when, or whether at once. |
| **Recorded overlap** | Two or more bindings of the IMEI that the feed lists as active on the same day | That neither binding had been *removed* by the feed on that day. Not that both were in use: a binding stays active until the operator's file removes it, and a removal that never comes leaves a stale binding active indefinitely. |
| **Truly simultaneous use** | Two handsets presenting the same IMEI to the network at the same moment, typically in different places | Nothing. The feed has no time of day, no location, no registration or traffic events. |

The data is one file a day of `(msisdn, imsi, imei, add|remove)` rows. A binding's "active" state is
the last of those labels, not presence on the network: 27.7% of active subscribers' numbers have
never appeared in a daily file at all (the KPI definitions, `kpiMetrics.ts`), and in a clean week
69.4% of adds are on a pair the feed also removed that week, while 11.0% repeat a pair already added
that week (ADR-014, M11). So "active together" in this data is weaker evidence than it sounds.

## What the current rules measure

From `ClickHouseIngestionStore.RiskSnapshot.cs` (the risk snapshot, ADR-014) - all on **countable**
IMEIs only: 14 digits, not fourteen zeros, not the shifted shape.

| Rule | Measure | Threshold (more than) | Highest level |
|---|---|---:|---|
| SharedImeiSims30 | distinct SIMs with a clean **add** to the IMEI in 30 days (`sims_30`) | 283 | Risk signal, in a clean window |
| SharedImeiNumbers30 | distinct numbers among those adds (`numbers_30`) | 283 | Risk signal, in a clean window |
| SharedImeiSimsEver | distinct SIMs ever bound to the IMEI (`sims_ever`) | 1,564 | Anomaly - an all-time count accumulates ordinary churn |
| SharedImeiSimsNotRemoved | distinct SIMs whose binding to the IMEI is active and was touched by a dated file (`sims_not_removed_dated`) | 77 | Anomaly |

"Clean" sets aside adds on days the feed-quality monitor listed the SIM under several numbers, and
every window that overlaps days flagged for shifted IMEIs or multi-number SIMs is capped at Anomaly.
With the feed as it is, every recent window is capped (ADR-014, "Feed defects").

**None of the four measures time.** The windowed rules count adds inside 30 days, not overlaps; the
not-removed rule counts a state, not use. All four were cut at a review capacity of about 500 IMEIs
per list - they were designed to find handsets shared by *hundreds* of SIMs, not a second handset. At
that end the lists are increasingly not phones: of the IMEIs with more than 100 SIMs, 1,199 of 2,447
are smartphones; of those with more than 300, 111 of 541, the rest modems, feature phones, handhelds,
dongles and modules (ADR-014, M3 by device type).

## The two scenarios

The values below follow from the measures' definitions, worked through by hand; they are not measured
on real handsets.

### 1. One phone, 20 SIMs over a year, used one at a time

Someone who changes SIM every few weeks - travel SIMs, prepaid top-ups, a family handset.

| Measure | Ideal feed (every SIM removed when replaced) | Feed with missing removals |
|---|---:|---:|
| `sims_ever` | 20 | 20 |
| `sims_30` (worst month) | 2-3 | 2-3 |
| `sims_not_removed_dated` | 1 | up to 20 |
| Listed by any rule | no (20 < 1,564; 3 < 283; 1 or 20 < 77) | no |

Correctly not listed. But the right column shows the trap: **if the operator does not send removals,
this honest phone shows up to 20 "overlapping" SIMs** - more recorded overlap than the clone below.

### 2. One IMEI copied onto two handsets, each with its own SIM, both in use

| Measure | Value |
|---|---:|
| `sims_ever` | 2 |
| `sims_30` | 0-2, depending on when each SIM was added |
| `sims_not_removed_dated` | 2, while both bindings stand |
| Listed by any rule | no |

**Not detected, and the current rules cannot detect it.** Two SIMs on one IMEI is far below every
threshold, and lowering a threshold to 2 would list a large share of all handsets: every phone that
changed SIM without the old binding being removed has the same shape. Measured at the storage floor
(ADR-014 M8), 84,849 IMEIs already have more than five dated, not-removed SIMs; a two-SIM rule would
be a population count, not a signal.

What separates scenario 2 from scenario 1 is not the number of SIMs - scenario 1 has more - but that
two handsets were **on the network at the same time**. That is precisely what the feed does not record.

## False positives and false negatives of any rule built on this feed

**Would look like copying and is not:**

- Stale bindings: a SIM moved to another handset with no removal sent leaves both bindings active.
- Resold, refurbished, shared and shop handsets: many SIMs over time, honestly.
- Modems, routers and IoT modules that rotate SIMs: above 300 SIMs per IMEI four in five listed IMEIs
  are not smartphones (ADR-014, M3 by device type).
- Feed defects: the multi-number SIMs since 2026-07-27 and, above all, the shifted IMEIs since
  2026-09-15, which attach *other subscribers' handsets* to numbers for a day at a time (`04-…`,
  `05-…`). In their restored form those would read as one IMEI on many numbers. The rules exclude the
  shifted shape, so they are not counted - but any new rule must keep that exclusion.
- Manufacturer IMEI reuse: some low-cost handsets ship with duplicated or default IMEIs. That is many
  devices on one IMEI - real, but not a person copying one.

**Would be copying and not look like it:**

- Clones that never overlap in the recorded state (one SIM removed before the other is added).
- Clones arriving in the shifted form: excluded from every count by design (ADR-014 M12).
- Clones with removals reported promptly: the state shows one binding at a time.
- Anything below the thresholds - which, for two handsets, is everything.

## What daily data cannot settle

- **No time of day.** Two adds on the same day cannot be ordered, let alone shown to coincide.
- **No location or cell.** "Impossible travel" - one IMEI in two cities within minutes - is the
  strongest simple signal of a clone and needs both.
- **No presence.** Adds and removes are the operator's events, with semantics only the operator can
  state (`02-open-questions.md`); active does not mean attached.
- **No check digit or software version.** The feed carries 14-digit IMEIs, so a Luhn check (15th digit)
  is impossible, and IMEISV - which often differs between a genuine handset and a cloned one - is absent.
- **Defect days.** Every window since 2026-07-28 overlaps flagged days and is capped at Anomaly.

## What would make a valid detection possible

In order of value, all from the operator:

1. **Timestamped network registration events with the serving cell** - attach/location updates from
   the MME/AMF/VLR, or CDRs: (IMEI, IMSI, time, cell). Two IMSIs presenting one IMEI in overlapping
   time intervals, or in places a person cannot travel between in the time, is direct evidence.
2. **EIR CheckIMEI logs**, which record each time the network checked a handset identity, with time.
3. **IMEISV and the full 15/16-digit form**: the software version and check digit separate otherwise
   identical identities and expose invalid ones.
4. **Explicit removal semantics**: whether every SIM change produces a removal, and when. Without it,
   recorded overlap cannot be read at all.
5. **Device-reported model** (device management, or the GSMA TAC versus what the device reports): a
   Samsung TAC on a handset reporting another model is a strong flag.

With (1), a rule could be stated precisely: *an IMEI seen with two different IMSIs in overlapping
intervals, or in two cells further apart than the time between them allows.* That is a new measure
and a new rule - not a threshold change to the four above - and it is not built.

## Recommendation, not implemented

Keep the four shared-IMEI rules for what they are good at - handsets shared by hundreds of SIMs - and
do not lower their thresholds to chase copying: below a few dozen SIMs they list ordinary handsets
and stale bindings. Ask the operator for (1) and (4) above. Until then, the honest answer to "is this
IMEI copied?" from this system is "the data cannot say".
