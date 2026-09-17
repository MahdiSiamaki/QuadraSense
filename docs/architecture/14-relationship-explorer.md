# Relationship Explorer — what is a fact and what is inferred

**Status:** Built 2026-09-17. Every figure measured against the running system.
**Read after:** `02-glossary.md`.

---

## What it does

Start from a phone number, a SIM or a handset; see everything connected to it; click any of them
to move there. `POST /api/v1/relationships/explore`, and a page at `/relationships`.

Length alone decides what was typed — 10 digits a number, 14 a handset, 15 a SIM — using the same
`DeviceSearchTerm` the Devices search uses, so an identifier cannot mean one thing on one page and
something else on another.

## Almost all of it is a fact, not an inference

The grain of this dataset is the binding, `(msisdn, imsi, imei)`. Every link the explorer draws
between the three is therefore already recorded; the work is reaching it from whichever identifier
the operator holds. Each direction reads the copy of current state sorted for it —
`binding_current` by MSISDN, `binding_by_imsi` by IMSI, `binding_by_imei` by IMEI — which turns a
315-million-row scan into a primary-key read.

Measured on handset `86453906970786`: two bindings, one number, two SIMs (one active, one ended
2026-04-15 — a SIM swap, visible because a binding is kept after it ends).

## The one derived section, and why it is narrow

A dual-SIM phone carries **two IMEIs**, one per radio — the GSMA record confirms it for 76.9% of
bindings (`imeiQuantity = 2`). Nothing in the feed says which two belong together.

What can be *shown* is a pair where the two serials are adjacent within one TAC **and** the same
phone number has been seen on both. Adjacency alone is not evidence: within a popular TAC roughly
a quarter of serials sit next to another live handset simply because the model sold well. The
shared subscriber is what makes it a pair, and it is decisive:

| serial distance | pairs sharing a number (TAC 86453906, 151,113 handsets) |
|---|---:|
| **1** | **4,760** |
| 2 | 0 |
| 3 | 1 |
| 1000 | 0 |

That is a signature, not a tendency. Nothing but one physical handset produces it.

## Why it will not show every real pair

Stated on the page itself, because absence here is not evidence of absence in the world.

**A second radio only appears if a SIM of this network has used it.** A binding requires an IMSI
from this operator, so a dual-SIM phone whose other slot holds a competitor's SIM has an IMEI that
is simply not in this dataset.

**Manufacturers do not allocate alike.** The same test on TAC 35004012 (Galaxy A54 5G, also
`imeiQuantity = 2`, also two SIM slots) finds **zero** pairs at distance 1. Samsung does not number
the second IMEI adjacently. And of 1,435 Galaxy A54 numbers holding two or more active handsets,
**98% are two different models** — a device replacement, not a second radio.

So a handset showing no pair is **not** shown to be single-SIM. The product owner's plan is to
improve coverage with probe data from other network nodes; until that exists the rule stays strict,
because a wrong pairing is worse than a missing one — nobody re-checks a link that looks right.

## Permission is per identifier kind, not per page

A relationship has two ends and seeing it means seeing both:

| section | needs |
|---|---|
| centre is a number | `lookup.subscriber` |
| centre is a SIM | `lookup.imsi` |
| centre is a handset | `lookup.imei` |
| phone numbers listed | `lookup.subscriber` |
| SIMs listed | `lookup.imsi` |
| handsets listed, and pairing | `lookup.imei` |

A section the caller may not see is **named as withheld**, not returned empty — an empty list
reads as "this number has no devices", which is a different and false statement.

The identifier travels in the POST body, never a URL, so it stays out of access logs, proxy logs
and browser history. Every call is audited including refusals, with counts rather than the
neighbouring identifiers: repeating them would make the audit log a second copy of the data it
exists to police. The permission checks run inside the handler, which puts them outside the
pipeline's automatic audit — the trap `DeviceEndpoints` documents, where a check made this way once
left a refusal unrecorded.

## Limits

- **500 bindings** per identifier, and the caller is told when the cap bit. One SIM in this data
  carries 1,216 handsets; that row is real and worth seeing, but not by rendering it whole.
- **40 handsets** are probed for a pair, each contributing two candidate serials to one IN list
  against a table sorted by IMEI.
