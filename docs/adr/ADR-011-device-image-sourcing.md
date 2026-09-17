# ADR-011 — Device images are proposed, scored and reviewed, never auto-applied

**Status:** Accepted, 2026-09-17.
**Amends** [ADR-010](ADR-010-device-images.md), which established the model-keyed image store and
the first sourcing run. The key and the store stand; the sourcing does not.

---

## Context

ADR-010's sourcing populated 87 models and the result was not good enough to ship. The catalogue
acquired photographs of shop shelves with price tags visible, a Wikipedia logo, and — until it was
caught by rendering the images and looking at them — a photograph of Nokia's headquarters building
standing in for four handsets.

**Four faults, each sufficient on its own:**

1. **The source was an encyclopedia, not a product catalogue.** The tool asked Wikipedia's
   `pageimages` API for the *lead image of an article*: whatever a volunteer had uploaded to
   illustrate it. There is no reason for that to be a product render and frequently it is not.
2. **There was no candidate set.** One lead image per model, so "the first result wins" was
   structural rather than a choice — nothing competed and nothing was scored.
3. **Every guard was textual.** Filename token matching with a variant guard checked *identity*
   competently, and never looked at a single pixel. Background, crop, resolution and composition
   went entirely unchecked.
4. **There was no lifecycle.** `catalog.device_model_image` had no status, so an image a script
   fetched and an image an administrator chose were the same row. 84 of the 87 were the former and
   nothing said so.

## Decision 1 — a candidate table, and the live image is never written by a script

Migration 009 adds `catalog.device_image_candidate` and a `status` on the live table. The pipeline
writes candidates and **has no code path to the live table**; promotion happens in
`POST /api/v1/devices/image-candidates/{id}/approve`, by a person holding `device.image.manage`,
in one transaction that copies the bytes across and marks them `verified` with the reviewer's id.

Existing rows were classified from the provenance note they already carried: the 84 Wikimedia ones
became `needs_review`, the 3 manual uploads stayed `verified`. The complaint that prompted this
work is therefore visible in the product rather than only in a conversation.

A rejected candidate is **kept, not deleted**. Its `(model_key, sha256)` is the unique index the
tool checks before proposing, so deleting a rejection would guarantee the reviewer saw it again on
the next run.

## Decision 2 — identity is the model, matching uses the model codes

The image stays keyed by `(brand, marketing_name)`, because that is what a picture depicts and
because `marketingName` already separates the variants that look different: `Galaxy A32` is
SM-A325x, `Galaxy A32 5G` is SM-A326x. The suffixes that remain — `/DS`, `/M`, `/N`, `/U` — are
region and dual-SIM codes for one physical product, and **Galaxy A51 has sixteen of them**. Keying
by model code would have produced sixteen copies of one render to keep in step.

The codes are carried into matching anyway, and a code found in a candidate's filename is recorded
as the strongest form of confirmation.

## Decision 3 — an explicit source allowlist, and an empty manufacturer tier

`tools/device_image_sources.json` lists the domains a candidate may come from. A candidate from
anywhere else is rejected **before a byte is downloaded**. The file is data, so adding a source is
a reviewed edit rather than a code change, and the pipeline never widens it at runtime.

**No manufacturer source is configured, and that is a finding rather than an omission.** Official
renders from `images.samsung.com`, `www.apple.com` and Xiaomi's CDN sit behind per-product paths
that cannot be derived from a TAC record; reaching them needs a licensed catalogue feed or a
per-vendor adapter agreed with the vendor. The provider interface and the config slot exist for
one. Until then the pipeline will not dress an encyclopedia photograph up as a product render —
it scores it as what it is, and most such photographs now fail.

## Decision 4 — a deterministic score, computed from the pixels, stored with its reasoning

Rejections are absolute and come first; a candidate that breaks one is never redeemed by scoring
well elsewhere.

| rejected when | measured as |
|---|---|
| thumbnail | either edge below 400px |
| scene rather than product | subject fills under 18% of the frame |
| cropped device | content touches 2 or more edges |
| not a studio backdrop | under 55% of the border is white or transparent |
| banner | aspect ratio outside 0.25–3.0 |
| wrong model or variant | filename token match with a variant guard |
| unusable transport | non-`https`, off-allowlist, non-200, redirect off the allowlist |
| not an image | magic bytes, never the `Content-Type` header |

| scored on | max |
|---|---:|
| exactModelMatch | 30 |
| sourceTrust (manufacturer 25, encyclopedic 7) | 25 |
| resolution (400px → 1000px) | 15 |
| cleanBackground | 10 |
| productCoverage | 8 |
| renderLike (distinct colours at 64×64) | 7 |
| transparency | 5 |

Every term is stored on the candidate with the measurement behind it, and the review screen shows
all of them. A reviewer being asked to change what customers see should not have to trust a number.

**An honest limit:** none of this is a classifier. There is no model deciding "watermark" or
"hand holding the device". Those are caught indirectly — a photograph of a scene fails on
background and coverage — and the code names each measure for what it actually computes rather
than for what it approximates. A watermark on an otherwise clean render would not be caught.

**A second honest limit:** the background rule and the crop rule overlap. Both use the same
"is this pixel background" test, so an image with a dirty border also has content reaching the
border, and the cleanliness threshold rarely fires alone. Its real work is as a graded score term.
The test that appeared to prove otherwise was renamed once that was noticed.

## Decision 5 — remote fetching treats the far end as hostile

`https` only; host on the allowlist before the request; DNS resolved and refused if it lands on a
private, loopback, link-local, reserved or multicast address; at most 3 redirects and the
**effective** URL re-checked against the allowlist afterwards; bounded time and bounded size;
media type from magic bytes and never from the header; `Image.MAX_IMAGE_PIXELS` set against
decompression bombs; the image re-encoded, which is what drops EXIF and anything riding with it.

DNS rebinding between the check and the fetch is not fully closed by this. The allowlist is what
bounds it, and that is stated rather than implied.

An error found while building this is worth recording: the fetch did not check the HTTP status,
so Wikimedia's 429 — answered with a 2,255-byte HTML page — was sniffed as no known format and
reported as *"bytes are not a supported image"*. True, and the wrong diagnosis entirely. The
status is now checked before the bytes, and a 429 backs off and retries rather than being recorded
as a bad image.

## Decision 6 — normalisation is contain, never cover

800×800, 8% padding, WebP, aspect ratio preserved, background trimmed first so the padding is
measured from the product rather than from whatever margin the source had. The product ends up
filling about 84% of the tile without touching an edge. `DeviceImage.vue` renders `object-contain`
with `object-position: center` everywhere it is used, so a device is never clipped to fill a tile.

## Consequences

- **Nothing changes in the catalogue without a person.** That is the point.
- The 84 unreviewed images are now visibly unreviewed, and the review screen shows them beside a
  proposed replacement.
- Coverage will be low for as long as the only source is Commons, because most of what Commons
  has for a given handset is a photograph of one on a desk or a shelf, and those now fail. **Low
  coverage with a placeholder is the intended outcome**, not a regression.
- `--apply` stages candidates; there is deliberately no flag in the tool that writes a live image.

## Files

`tools/source_device_images.py`, `tools/device_image_sources.json`,
`tools/test_device_image_pipeline.py`, `db/operational/migrations/009_device_image_review.sql`,
`Sqm.Contracts/Devices/DeviceImageCandidateContracts.cs`,
`Sqm.Application/Abstractions/IDeviceImageCandidateStore.cs`,
`Sqm.Infrastructure/Catalog/PostgresDeviceImageCandidateStore.cs`,
`Sqm.Api/Endpoints/DeviceImageReviewEndpoints.cs`,
`frontend/src/api/deviceImageCandidates.ts`,
`frontend/src/features/devices/DeviceImageReviewPage.vue`,
`frontend/src/features/devices/DeviceImage.vue`.
