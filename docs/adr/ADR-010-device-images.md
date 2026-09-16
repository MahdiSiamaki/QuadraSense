# ADR-010 — A device photograph belongs to a model, and where the photographs come from

**Status:** Accepted, 2026-09-17.
**Supersedes** the image portion of [ADR-009](ADR-009-device-module.md), which keyed images by TAC.

---

## Context

The Devices module shipped with photographs keyed by TAC and uploaded by administrators. The first
real upload showed why the key was wrong. An administrator uploaded a Xiaomi product photograph of
a Redmi Note 12S to TAC `86033006`, and the phone appeared on exactly one device page. The other
seventeen showed the drawn placeholder.

A marketing name is not one TAC. Measured against the active GSMA version:

| model | TACs | bindings |
|---|---:|---:|
| Redmi Note 12S | 18 | 747,395 |
| Redmi Note 13 | 38 | 1,462,065 |
| Galaxy A12 | **184** | 2,930,884 |

Covering one Samsung meant 184 uploads of identical bytes, and replacing it later meant finding
all 184 again or leaving the rest silently stale.

## Decision 1 — key images by `(brand, marketing name)`

`catalog.device_model_image` replaces `catalog.device_image`. The key is the brand and marketing
name, lowercased and whitespace-collapsed, joined by a pipe: `redmi|redmi note 12s`. The API
resolves TAC → model on read and write, so the route stays `/devices/{tac}/image` and the frontend
is unchanged.

**Brand rather than manufacturer** because the GSMA record carries both and they are different
words: manufacturer is `Xiaomi Communications Co Ltd` where brand is `Redmi`, and the brand is what
is printed on the handset. Manufacturer is the fallback when brand is blank.

**The key folds case and whitespace and nothing else.** Every further normalisation that looks
helpful merges two real products — `Galaxy A05` with `Galaxy A05s`, `Redmi Note 11` with
`Redmi Note 11 Pro`, `Redmi Note 12` with `Redmi Note 12S`. `DeviceModelKeyTests` pins each of
those pairs apart.

A TAC with no marketing name — the unknown-device bucket, unregistered TACs — resolves to no model
and keeps the placeholder. That is the absence of a device rather than a nameless one.

## Decision 2 — source photographs from Wikimedia Commons, under guards, and verify by eye

The product owner chose to source images from the web rather than supply them, having been told
that product photography is copyrighted and that the decision was theirs. Commons is used because
it is the only source whose licence can be written down: every file carries an explicit licence
and a permanent URL, both recorded in `source_note`. GSMArena, manufacturer press pages and
retailer listings have better and far more uniform photography, and no licence this catalogue
could cite.

**Coverage, measured on the 200 most populous models:** 60.5% have some Commons image; **27%
survive the identity guards**. The gap is not waste — it is the difference between a catalogue and
a catalogue of plausible mistakes.

### What the guards refuse, and why they exist

Taking the top search hit was wrong in the most dangerous way available:

| model | proposed file | what it actually is |
|---|---|---|
| Redmi Note 12S | `Redmi_Note_12_front.jpg` | a different phone |
| Redmi Note 11 | `Redmi_Note_11_Pro_(Star_Blue).png` | a more expensive phone |
| Galaxy A05 | `Samsung_Galaxy_A05s_2024.jpg` | a different phone |
| Galaxy A15 | `Logo_Samsung_Galaxy_A15.png` | not a phone |
| POCO M3 | `Redmi_Note_9_4G.jpg` | three phones in one lookup |

So a candidate is accepted only when the **file name** — stronger evidence than the page it hangs
on — names this model in whole tokens and adds no variant word the model does not claim. Whole
tokens because `a05` must not match `a05s`; the variant list because `Redmi Note 11` must not
take `Redmi Note 11 Pro`.

**The guards were not sufficient on their own.** Four Nokia models named only by type code
(`TA-1557`) had that code stripped as noise, leaving the bare word "Nokia", which resolves to the
company's article — and they were given a photograph of **Nokia's headquarters building**. Every
textual guard passed, because "nokia" really was in the file name. It was caught by rendering the
stored images to a contact sheet and looking at them, and the run is not considered complete until
that has been done.

### Result

**87 models carry a photograph, covering 33.8 million bindings — 30.9% of the enriched
population.** Quality is honestly mixed: some are clean product shots or vector illustrations,
several are amateur photographs of shop shelves with price tags visible. They are the right
devices, which is the property that was defended.

## Consequences

- One upload covers every TAC of a model, and one replacement updates all of them.
- The long tail stays on the placeholder. 26,884 models are named on this network and 24,757 of
  them hold under 1,000 bindings between them; sourcing images for those is neither possible nor
  worth it.
- Provenance is recorded per image, so any picture can be traced to its file, licence and author.
- `tools/source_device_images.py` is a tool a person runs, not a step the platform takes. It
  writes nothing without `--apply`.
- If uniform studio photography is ever wanted, it needs a licensed source or supplied files;
  no guard on a free-image search can produce it.
