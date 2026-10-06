"""
Stage a curated device image package as review candidates. Never replaces a live image.

WHAT A PACKAGE IS
-----------------
The product owner's TAC_Catalog_Images deliveries: a folder of images, image_sources.csv (one row
per catalogue product: where its image came from, its size, its SHA-256) and TAC_to_image.csv (one
row per TAC: which product image the package matched it to, and how). The package is keyed by TAC;
the catalogue keys images by MODEL - lower(brand)|lower(marketing name), whitespace collapsed,
exactly as Sqm.Domain.Devices.DeviceModelKey - so the first job is to regroup the package's TACs by
the model they belong to in OUR active GSMA version (sqm.tac), and the second is to say how much of
each model the package's mapping actually covers.

WHAT IT DOES WITH THEM
----------------------
Owner decisions of 2026-10-06, each enforced here:

  1. Every image is imported; none is discarded. A package image that fails a pixel rule is
     staged anyway, with the measurement as a WARNING the reviewer sees. ADR-011's rules rejected
     310 of the 1,279 package images outright, almost all wrongly: a render trimmed tight to the
     device reads as "cropped" to a rule written for web photographs.
  2. Every image is normalised to the standard of the images already verified, which fill 93-99%
     of a square frame: 800x800, the device trimmed of its background and scaled - up OR down - so
     its longest side is 752 px (3% margin each side), centred on transparency, WebP <= 512 KB.
     ADR-011's normalise() only ever shrinks and pads 8%, which is why the two did not match.
  3. Pixel rules are warnings, never rejections.
  4. (the review page - not this tool)
  5. A model the package covers only partly is staged with a partial_coverage warning, high when
     the TACs the image was matched to carry under half the model's active bindings: "Galaxy A14"
     has 200 TACs and the package maps one, with no bindings, to an image named "Galaxy A14 5G".

SAFETY
------
Default execution is a DRY RUN that writes nothing: it reads the package, reads ClickHouse and
reads PostgreSQL. `--apply` inserts rows into catalog.device_image_candidate with status
'needs_review' and nothing else. There is no code path in this tool that writes
catalog.device_model_image; promotion is the review endpoint's job, by a person holding
device.image.manage (ADR-011, Decision 1). tools/test_device_image_package.py checks that.

USAGE
-----
    python tools/import_device_image_package.py --package <dir>                       # dry run
    python tools/import_device_image_package.py --package <dir> --report out.json     # dry run + JSON
    python tools/import_device_image_package.py --package <dir> --model "Galaxy A1" --limit 5
    python tools/import_device_image_package.py --package <dir> --apply               # stages
"""

from __future__ import annotations

import argparse
import base64
import collections
import concurrent.futures
import csv
import dataclasses
import hashlib
import io
import json
import os
import re
import statistics
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from source_device_images import (  # noqa: E402 - the shared helpers sit next to this file
    CANVAS,
    LIMITS,
    MAX_STORED_BYTES,
    MIN_SOURCE_EDGE,
    PREFERRED_SOURCE_EDGE,
    STORED_FORMAT,
    STORED_MIME,
    WHITE_TOLERANCE,
    Candidate,
    DeviceIdentity,
    analyse,
    clickhouse,
    esc,
    host_of,
    identity_verdict,
    known_candidate_hashes,
    live_images,
    norm,
    psql,
    say,
    sniff,
)

# --------------------------------------------------------------------------- the standard

# The verified images fill 93-99% of the frame edge to edge. 3% each side puts the device at 94%.
MARGIN_FRACTION = 0.03
SUBJECT_EDGE = round(CANVAS * (1 - 2 * MARGIN_FRACTION))        # 752

# Enlarging past these makes a soft tile next to a sharp one. A warning, not a rejection: some
# package images are the only picture of the device there is, and a person decides.
UPSCALE_WARN = 1.5
UPSCALE_HIGH = 2.0

# The thresholds ADR-011 rejected on, kept as the points where a warning starts.
SMALL_SUBJECT = 0.18
CLEAN_BORDER = 0.55
ASPECT_RANGE = (0.25, 3.0)

SOURCE_TRUST = {"manufacturer": 25, "retailer": 15, "museum_archive": 12,
                "public_image_archive": 7}
OTHER_SOURCE_TRUST = 5

HIGH, INFO = "high", "info"

# Rows per psql call. Each row carries ~50-150 KB of WebP as base64; 40 keeps one statement to a
# few megabytes, and one INSERT statement is one transaction.
INSERT_BATCH = 40

ACCEPTED_SOURCE_MIME = {"image/png", "image/jpeg", "image/webp"}


# --------------------------------------------------------------------------- package records

@dataclasses.dataclass(frozen=True)
class Product:
    """One row of image_sources.csv: a catalogue product and the image the package holds for it."""

    product_id: str
    brand: str
    product_name: str
    image_file: str
    sha256: str
    source_page: str
    source_kind: str
    image_url: str
    identity_source: str
    match_scope: str
    qa_status: str


@dataclasses.dataclass(frozen=True)
class PackageTac:
    """One matched row of TAC_to_image.csv."""

    tac: str
    image_file: str
    product_id: str
    match_method: str


@dataclasses.dataclass(frozen=True)
class TacRow:
    """One TAC of OUR active GSMA version, with the fields the model key is made from."""

    tac: str
    brand: str
    marketing_name: str
    manufacturer: str
    model_code: str


def read_csv(path: str) -> list[dict]:
    # utf-8-sig: both files begin with a byte-order mark, which would otherwise become part of the
    # first column's name and make row["tac"] a KeyError.
    with open(path, encoding="utf-8-sig", newline="") as fh:
        return list(csv.DictReader(fh))


def read_package(package_dir: str) -> tuple[dict[str, Product], list[PackageTac]]:
    products: dict[str, Product] = {}
    for row in read_csv(os.path.join(package_dir, "image_sources.csv")):
        products[row["product_id"]] = Product(
            product_id=row["product_id"], brand=row["brand"], product_name=row["product_name"],
            image_file=row["image_file"], sha256=(row["sha256"] or "").strip().lower(),
            source_page=row["source_page"], source_kind=row["source_kind"],
            image_url=row["image_url"], identity_source=row["identity_source"],
            match_scope=row["match_scope"], qa_status=row["qa_status"])

    tacs = [PackageTac(tac=row["tac"].strip(), image_file=row["image_file"].strip(),
                       product_id=row["catalog_product_id"].strip(),
                       match_method=row["match_method"].strip())
            for row in read_csv(os.path.join(package_dir, "TAC_to_image.csv"))
            if (row.get("image_file") or "").strip()]
    return products, tacs


# --------------------------------------------------------------------------- model identity

def display(text: str) -> str:
    """Trimmed, whitespace collapsed - DeviceModelKey.DisplayBrand / DisplayName."""
    return re.sub(r"\s+", " ", (text or "").strip())


def model_key_of(brand: str, manufacturer: str, marketing_name: str) -> str | None:
    """
    DeviceModelKey.For, in Python. None when the record names no model.

    Brand falls back to manufacturer when it is blank, as in C#. The ClickHouse query already does
    coalesce(nullIf(brandName,''), manufacturer); a brand of only spaces gets past that and is
    caught here, so the two languages cannot key the same TAC differently.
    """
    name = norm(marketing_name)
    maker = norm(brand) or norm(manufacturer)
    if not name or not maker:
        return None
    return f"{maker}|{name}"


@dataclasses.dataclass
class Model:
    key: str
    brand: str
    marketing_name: str
    manufacturer: str
    model_codes: tuple[str, ...]
    tacs: tuple[str, ...]
    bindings: int


def group_models(tac_rows: list[TacRow], bindings: dict[str, int]) -> dict[str, Model]:
    """
    Our TACs grouped by model key, with the model's TAC count and active bindings.

    Spellings that differ only in case and spacing ("Samsung", "SAMSUNG ") are one model, because
    the key folds them; a different word ("Samsung Korea") is a different model, because the key
    does not. The display spelling is the one carrying the most bindings, then the most TACs.
    """
    tacs: dict[str, list[str]] = collections.defaultdict(list)
    spellings: dict[str, collections.Counter] = collections.defaultdict(collections.Counter)
    spelling_tacs: dict[str, collections.Counter] = collections.defaultdict(collections.Counter)
    makers: dict[str, collections.Counter] = collections.defaultdict(collections.Counter)
    codes: dict[str, set[str]] = collections.defaultdict(set)

    for row in tac_rows:
        key = model_key_of(row.brand, row.manufacturer, row.marketing_name)
        if key is None:
            continue
        brand = display(row.brand) or display(row.manufacturer)
        spelling = (brand, display(row.marketing_name))
        tacs[key].append(row.tac)
        spellings[key][spelling] += bindings.get(row.tac, 0)
        spelling_tacs[key][spelling] += 1
        makers[key][display(row.manufacturer)] += 1
        if row.model_code.strip():
            codes[key].add(row.model_code.strip())

    models: dict[str, Model] = {}
    for key, members in tacs.items():
        brand, name = min(spellings[key],
                          key=lambda s: (-spellings[key][s], -spelling_tacs[key][s], s))
        models[key] = Model(
            key=key, brand=brand, marketing_name=name,
            manufacturer=makers[key].most_common(1)[0][0],
            model_codes=tuple(sorted(codes[key])),
            tacs=tuple(sorted(set(members))),
            bindings=sum(bindings.get(t, 0) for t in set(members)))
    return models


# --------------------------------------------------------------------------- planning (pure)

@dataclasses.dataclass
class PlannedCandidate:
    """One (model, package image) pair: everything known before a pixel is decoded."""

    model: Model
    image_file: str
    product: Product
    match_methods: tuple[str, ...]
    mapped_tacs: int
    model_tacs: int
    mapped_bindings: int
    model_bindings: int
    images_for_model: int


@dataclasses.dataclass
class Plan:
    candidates: list[PlannedCandidate]
    stats: dict


def plan(package_tacs: list[PackageTac], products: dict[str, Product],
         tac_rows: list[TacRow], bindings: dict[str, int]) -> Plan:
    """
    Regroup the package's TAC -> image mapping by our model keys. No I/O, so it is testable.

    A model gets one candidate per DISTINCT image the package mapped any of its TACs to. Every
    figure the reviewer is shown about coverage is computed here, against our TACs and our
    bindings, never taken from the package: the package counts TACs in ITS file, which is a
    different GSMA export from the one the product runs on.
    """
    models = group_models(tac_rows, bindings)
    key_of_tac = {t: m.key for m in models.values() for t in m.tacs}

    by_image_file: dict[str, list[Product]] = collections.defaultdict(list)
    for product in products.values():
        by_image_file[product.image_file].append(product)

    # (model_key, image_file) -> the package rows behind it
    pairs: dict[tuple[str, str], list[PackageTac]] = collections.defaultdict(list)
    unknown_tacs = 0
    seen_tacs: set[str] = set()
    for row in package_tacs:
        if row.tac in seen_tacs:            # one TAC, one image: the package says so; trust nothing
            continue
        seen_tacs.add(row.tac)
        key = key_of_tac.get(row.tac)
        if key is None:
            unknown_tacs += 1
            continue
        pairs[(key, row.image_file)].append(row)

    images_per_model = collections.Counter(key for key, _ in pairs)

    candidates: list[PlannedCandidate] = []
    for (key, image_file), rows in pairs.items():
        model = models[key]

        # Several catalogue products can share one image file (the package stores identical bytes
        # once). The product named here is the one most of this model's TACs were matched to.
        votes = collections.Counter(r.product_id for r in rows if r.product_id in products)
        if votes:
            product = products[min(votes, key=lambda p: (-votes[p], p))]
        elif by_image_file.get(image_file):
            product = sorted(by_image_file[image_file], key=lambda p: p.product_id)[0]
        else:
            product = Product(product_id="", brand="", product_name="", image_file=image_file,
                              sha256="", source_page="", source_kind="", image_url="",
                              identity_source="", match_scope="", qa_status="")

        mapped = {r.tac for r in rows}
        candidates.append(PlannedCandidate(
            model=model, image_file=image_file, product=product,
            match_methods=tuple(sorted({r.match_method for r in rows if r.match_method})),
            mapped_tacs=len(mapped), model_tacs=len(model.tacs),
            mapped_bindings=sum(bindings.get(t, 0) for t in mapped),
            model_bindings=model.bindings,
            images_for_model=images_per_model[key]))

    candidates.sort(key=lambda c: (-c.model_bindings, c.model.key, -c.mapped_bindings,
                                   -c.mapped_tacs, c.image_file))

    referenced = {c.image_file for c in candidates}
    stats = {
        "packageTacsWithImage": len(seen_tacs),
        "packageTacsNotInOurGsma": unknown_tacs,
        "packageImages": len({p.image_file for p in products.values()}),
        "imagesWithAModel": len(referenced),
        "imagesWithoutAModel": sorted({p.image_file for p in products.values()} - referenced),
    }
    return Plan(candidates=candidates, stats=stats)


# --------------------------------------------------------------------------- normalisation

def subject_bbox(image):
    """
    Where the device is: opaque pixels when the source has transparency, otherwise what is not
    near-white. Exactly the trim of source_device_images.normalise(), and for the reason given
    there - an opaque image's alpha box is the whole image, so a JPEG needs the white test.
    """
    from PIL import Image, ImageChops

    if image.getchannel("A").getextrema()[0] < 255:
        return image.getchannel("A").getbbox()
    white = Image.new("RGB", image.size, (255, 255, 255))
    distance = ImageChops.difference(image.convert("RGB"), white).convert("L")
    return distance.point(lambda v: 255 if v > WHITE_TOLERANCE else 0).getbbox()


def normalise_to_standard(data: bytes) -> tuple[bytes, str, float]:
    """
    800x800, device trimmed and scaled so its longest side is 752 px, centred on transparency.

    Returns (WebP bytes, media type, the scale factor applied). The factor is above 1 when the
    device was enlarged, which is what the low_resolution warning reads.

    Unlike normalise(), this scales UP as well as down: a 600 px source must fill the same 94% of
    the tile as a 3000 px one, or the catalogue grid shows devices at visibly different sizes.

    The device is pasted WITHOUT a mask. canvas.paste(img, box, img) blends the alpha channel by
    itself and squares it - an anti-aliased edge pixel at alpha 128 lands at 64, a soft shadow
    loses three quarters of its opacity. Onto a fully transparent canvas a straight copy is the
    correct composite.
    """
    from PIL import Image, ImageOps

    Image.MAX_IMAGE_PIXELS = LIMITS["max_decoded_pixels"]

    with Image.open(io.BytesIO(data)) as raw:
        raw.load()
        image = ImageOps.exif_transpose(raw.convert("RGBA"))

    bbox = subject_bbox(image)
    if bbox:
        image = image.crop(bbox)

    scale = SUBJECT_EDGE / max(image.width, image.height)
    if image.width >= image.height:
        size = (SUBJECT_EDGE, max(1, round(image.height * scale)))
    else:
        size = (max(1, round(image.width * scale)), SUBJECT_EDGE)
    image = image.resize(size, Image.LANCZOS)

    canvas = Image.new("RGBA", (CANVAS, CANVAS), (255, 255, 255, 0))
    canvas.paste(image, ((CANVAS - image.width) // 2, (CANVAS - image.height) // 2))

    for quality in (90, 82, 74, 66, 58):
        buffer = io.BytesIO()
        canvas.save(buffer, format=STORED_FORMAT, quality=quality, method=6)
        if buffer.tell() <= MAX_STORED_BYTES:
            return buffer.getvalue(), STORED_MIME, scale

    raise ValueError("cannot fit the normalised image under 512 KB")


@dataclasses.dataclass
class ImageResult:
    """One package image, read, checked, measured and normalised. error is set when it failed."""

    image_file: str
    error: str | None = None
    width: int = 0
    height: int = 0
    original_bytes: int = 0
    facts: dict = dataclasses.field(default_factory=dict)
    stored: bytes = b""
    mime: str = ""
    upscale: float = 1.0
    digest: str = ""
    expected_sha: str = ""
    actual_sha: str = ""

    @property
    def sha_ok(self) -> bool:
        return bool(self.expected_sha) and self.expected_sha == self.actual_sha


def process_image(package_dir: str, image_file: str, expected_sha: str) -> ImageResult:
    """Top level so a process pool can run it. Never raises: a failure comes back as .error."""
    result = ImageResult(image_file=image_file, expected_sha=(expected_sha or "").lower())
    try:
        with open(os.path.join(package_dir, *image_file.split("/")), "rb") as fh:
            data = fh.read()
        result.original_bytes = len(data)
        result.actual_sha = hashlib.sha256(data).hexdigest()

        # Only the three decoders the catalogue accepts are ever handed package bytes.
        if sniff(data) not in ACCEPTED_SOURCE_MIME:
            result.error = f"not a PNG, JPEG or WebP by its magic bytes ({data[:12]!r})"
            return result

        result.facts = analyse(data)
        result.width, result.height = result.facts["width"], result.facts["height"]
        result.stored, result.mime, result.upscale = normalise_to_standard(data)
        result.digest = hashlib.sha256(result.stored).hexdigest()
    except Exception as exc:                            # noqa: BLE001 - reported, never fatal
        result.error = f"{type(exc).__name__}: {exc}"
    return result


# --------------------------------------------------------------------------- warnings (pure)

def warning(code: str, severity: str, detail: str) -> dict:
    return {"code": code, "severity": severity, "detail": detail}


def image_warnings(image: ImageResult) -> list[dict]:
    """What the pixels say. Warnings only - ADR-011's rejections, demoted (owner, 2026-10-06)."""
    out: list[dict] = []
    facts = image.facts
    w, h = image.width, image.height

    if not image.sha_ok:
        out.append(warning(
            "checksum_mismatch", HIGH,
            f"the file's SHA-256 {image.actual_sha[:12]}... does not match image_sources.csv "
            f"({image.expected_sha[:12] or 'no checksum listed'}...) - the file is not the one "
            "the package describes"))

    if min(w, h) < MIN_SOURCE_EDGE or image.upscale > UPSCALE_WARN:
        out.append(warning(
            "low_resolution", HIGH if image.upscale >= UPSCALE_HIGH else INFO,
            f"source {w}x{h}; the device was scaled {image.upscale:.2f}x to reach "
            f"{SUBJECT_EDGE} px - check it is not soft"))

    edges = facts.get("edges_touched", 0)
    if edges >= 2:
        out.append(warning(
            "touches_edges", INFO,
            f"content touches {edges} of 4 source edges: trimmed tight to the device, or "
            "cropped - check the whole device is in frame"))

    coverage = facts.get("coverage", 0.0)
    if coverage < SMALL_SUBJECT:
        out.append(warning(
            "small_subject", INFO,
            f"the subject fills {coverage:.0%} of the source frame - check it is a product shot, "
            "not a scene"))

    clean = facts.get("cleanliness", 0.0)
    if clean < CLEAN_BORDER:
        out.append(warning(
            "background", INFO,
            f"{clean:.0%} of the source border is white or transparent - a tight trim reads this "
            "way too; check the background"))

    aspect = w / h if h else 0.0
    if aspect < ASPECT_RANGE[0] or aspect > ASPECT_RANGE[1]:
        out.append(warning(
            "aspect", INFO,
            f"source aspect ratio {aspect:.2f} - check it is one device, not a banner or a "
            "sheet of views"))
    return out


def device_of(planned: PlannedCandidate) -> DeviceIdentity:
    m = planned.model
    return DeviceIdentity(manufacturer=m.manufacturer, brand=m.brand,
                          marketing_name=m.marketing_name, model_codes=m.model_codes,
                          device_type="", bindings=m.bindings)


def identity_of(planned: PlannedCandidate) -> tuple[bool, str]:
    """ADR-011's identity guard, applied to the product name the package gave the image."""
    return identity_verdict(device_of(planned),
                            Candidate(url="", domain="", source_type="package",
                                      title=planned.product.product_name))


def mapping_warnings(planned: PlannedCandidate, live_status: str | None) -> list[dict]:
    """What the package's mapping, and the image already live, say."""
    out: list[dict] = []

    ok, note = identity_of(planned)
    if not ok and note.startswith("names a different variant"):
        out.append(warning(
            "different_variant", HIGH,
            f"the package calls this image '{planned.product.product_name}', which {note} "
            f"from '{planned.model.marketing_name}'"))

    if planned.mapped_tacs < planned.model_tacs:
        severe = (planned.model_bindings > 0
                  and planned.mapped_bindings < 0.5 * planned.model_bindings)
        out.append(warning(
            "partial_coverage", HIGH if severe else INFO,
            f"the package maps this image to {planned.mapped_tacs:,} of "
            f"{planned.model_tacs:,} TACs of the model - {planned.mapped_bindings:,} of "
            f"{planned.model_bindings:,} active bindings"))

    if planned.images_for_model > 1:
        out.append(warning(
            "several_images", INFO,
            f"the package has {planned.images_for_model} images for this model - approve one"))

    if live_status == "verified":
        out.append(warning(
            "replaces_verified", HIGH,
            "a reviewer verified the image shown for this model now - approving this replaces it"))
    elif live_status == "needs_review":
        out.append(warning(
            "replaces_unreviewed", INFO,
            "replaces the unreviewed image shown for this model now"))
    return out


def candidate_warnings(planned: PlannedCandidate, image: ImageResult,
                       live_status: str | None) -> list[dict]:
    found = image_warnings(image) + mapping_warnings(planned, live_status)
    # High first, then in rule order, so the review card leads with what matters most.
    return sorted(found, key=lambda w: 0 if w["severity"] == HIGH else 1)


# --------------------------------------------------------------------------- score (advice only)

def score(planned: PlannedCandidate, image: ImageResult) -> tuple[int, list[dict]]:
    """
    score_candidate()'s terms, with the package's provenance in place of the allowlist and
    without any rejection. Advice for ordering and for the reviewer, never a gate.
    """
    facts = image.facts
    breakdown: list[dict] = []

    def add(term: str, points: int, detail: str) -> None:
        breakdown.append({"term": term, "points": points, "detail": detail})

    kind = planned.product.source_kind or "unknown"
    add("sourceTrust", SOURCE_TRUST.get(kind, OTHER_SOURCE_TRUST),
        f"{kind} source ({host_of(planned.product.source_page) or 'no source page'})")

    by_code = any("model_code" in m for m in planned.match_methods)
    add("exactModelMatch", 30 if by_code else 20,
        "package matched by " + (", ".join(planned.match_methods) or "an unrecorded method"))

    edge = min(image.width, image.height)
    points = 15 if edge >= PREFERRED_SOURCE_EDGE else max(
        0, round(15 * (edge - MIN_SOURCE_EDGE) / (PREFERRED_SOURCE_EDGE - MIN_SOURCE_EDGE)))
    add("resolution", points, f"{image.width}x{image.height} source")

    clean = facts.get("cleanliness", 0.0)
    add("cleanBackground", round(10 * max(0.0, (clean - CLEAN_BORDER) / (1 - CLEAN_BORDER))),
        f"{clean:.0%} of the border is white or clear")

    coverage = facts.get("coverage", 0.0)
    add("productCoverage", 8 if 0.45 <= coverage <= 0.92 else 4,
        f"product fills {coverage:.0%} of the frame")

    colours = facts.get("colours", 0)
    add("renderLike", 7 if colours <= 900 else (3 if colours <= 2500 else 0),
        f"{colours} distinct colours at 64x64")

    if facts.get("has_alpha"):
        add("transparency", 5, "carries an alpha channel")

    return sum(t["points"] for t in breakdown), breakdown


def evidence_of(planned: PlannedCandidate, image: ImageResult, package: str) -> dict:
    p = planned.product
    return {
        "package": package,
        "productName": p.product_name,
        "productId": p.product_id,
        "matchMethods": list(planned.match_methods),
        "mappedTacs": planned.mapped_tacs,
        "modelTacs": planned.model_tacs,
        "mappedBindings": planned.mapped_bindings,
        "modelBindings": planned.model_bindings,
        "sourceKind": p.source_kind,
        "sourcePage": p.source_page,
        "imageUrl": p.image_url,
        "identitySource": p.identity_source,
        "matchScope": p.match_scope,
        "qaStatus": p.qa_status,
        "packageFile": planned.image_file,
        "upscale": round(image.upscale, 4),
        "originalSha256Ok": image.sha_ok,
    }


@dataclasses.dataclass
class Row:
    """One catalog.device_image_candidate row, ready to insert."""

    planned: PlannedCandidate
    image: ImageResult
    warnings: list[dict]
    quality_score: int
    breakdown: list[dict]
    evidence: dict
    package: str

    @property
    def model_key(self) -> str:
        return self.planned.model.key


def build_row(planned: PlannedCandidate, image: ImageResult, live_status: str | None,
              package: str) -> Row:
    total, breakdown = score(planned, image)
    return Row(planned=planned, image=image,
               warnings=candidate_warnings(planned, image, live_status),
               quality_score=total, breakdown=breakdown,
               evidence=evidence_of(planned, image, package), package=package)


# --------------------------------------------------------------------------- writing

def insert_sql(rows: list[Row]) -> str:
    """
    One INSERT for a batch: one statement, one transaction. The ONLY write this tool can make, and
    it is to the candidate table. RETURNING id is how the count of rows actually inserted is known
    when ON CONFLICT skips some.
    """
    values = []
    for row in rows:
        p, img, m = row.planned, row.image, row.planned.model
        values.append(
            "('{key}', '{brand}', '{name}', 'needs_review', '{mime}', "
            "decode('{payload}', 'base64'), decode('{digest}', 'hex'), "
            "'{stype}', '{sdomain}', '{surl}', {w}, {h}, {obytes}, {score}, "
            "'{breakdown}'::jsonb, {bindings}, '{warnings}'::jsonb, '{evidence}'::jsonb, "
            "'{package}')".format(
                key=esc(m.key), brand=esc(m.brand), name=esc(m.marketing_name),
                mime=esc(img.mime), payload=base64.b64encode(img.stored).decode("ascii"),
                digest=img.digest,
                stype=esc(p.product.source_kind or "package"),
                sdomain=esc(host_of(p.product.source_page)),
                surl=esc(p.product.source_page),
                w=int(img.width), h=int(img.height), obytes=int(img.original_bytes),
                score=int(row.quality_score), breakdown=esc(json.dumps(row.breakdown)),
                bindings=max(0, int(p.model_bindings)), warnings=esc(json.dumps(row.warnings)),
                evidence=esc(json.dumps(row.evidence)), package=esc(row.package)))

    return ("INSERT INTO catalog.device_image_candidate\n"
            "    (model_key, brand, marketing_name, status, content_type, bytes, sha256,\n"
            "     source_type, source_domain, source_url,\n"
            "     original_width, original_height, original_bytes,\n"
            "     quality_score, score_breakdown, bindings, warnings, evidence, package)\n"
            "VALUES\n    " + ",\n    ".join(values) + "\n"
            "ON CONFLICT (model_key, sha256) DO NOTHING\n"
            "RETURNING id;\n")


def returned_ids(output: str) -> list[int]:
    """
    The ids psql printed, cross-checked against its own command tag.

    With -t -A psql prints one id per line AND the tag ("INSERT 0 3"), so a count of lines would
    be one too many - parse the digits and make the tag agree.
    """
    ids = [int(line) for line in output.splitlines() if line.strip().isdigit()]
    tag = re.search(r"^INSERT 0 (\d+)$", output, re.MULTILINE)
    if tag and int(tag.group(1)) != len(ids):
        raise RuntimeError(f"psql reported {tag.group(1)} rows inserted but returned "
                           f"{len(ids)} ids")
    return ids


def insert_rows(rows: list[Row]) -> tuple[list[int], int, list[str]]:
    """(ids inserted, rows in batches that failed, one error per failed batch)."""
    inserted: list[int] = []
    failed_rows = 0
    errors: list[str] = []
    for start in range(0, len(rows), INSERT_BATCH):
        batch = rows[start:start + INSERT_BATCH]
        try:
            inserted.extend(returned_ids(psql(insert_sql(batch))))
        except Exception as exc:                        # noqa: BLE001 - report, carry on
            failed_rows += len(batch)
            errors.append(f"rows {start + 1}-{start + len(batch)}: {exc}"[:400])
        say(f"  inserted {len(inserted):,} so far "
            f"({min(start + INSERT_BATCH, len(rows)):,} of {len(rows):,} sent)")
    return inserted, failed_rows, errors


# --------------------------------------------------------------------------- reading the stack

_TSV_ESCAPES = {"\\": "\\", "t": "\t", "n": "\n", "r": "\r", "0": "\0", "b": "\b", "f": "\f",
                "'": "'"}


def tsv_field(text: str) -> str:
    """ClickHouse escapes backslash, tab and newline in TSV; undo it before keying anything."""
    return re.sub(r"\\(.)", lambda m: _TSV_ESCAPES.get(m.group(1), m.group(1)), text)


def tsv_rows(sql: str, columns: int) -> list[list[str]]:
    out: list[list[str]] = []
    for line in clickhouse(sql).splitlines():
        if not line:
            continue
        parts = line.split("\t")
        if len(parts) != columns:
            raise RuntimeError(f"ClickHouse answered something other than {columns} columns: "
                               f"{line[:200]}")
        out.append([tsv_field(p) for p in parts])
    return out


def load_tac_rows() -> list[TacRow]:
    rows = tsv_rows("""
        SELECT t.tac, coalesce(nullIf(t.brandName,''), t.manufacturer), t.marketingName,
               t.manufacturer, t.modelName
        FROM sqm.tac AS t
        WHERE t.marketingName != ''
        FORMAT TSV""", 5)
    return [TacRow(tac=r[0], brand=r[1], marketing_name=r[2], manufacturer=r[3], model_code=r[4])
            for r in rows]


def load_bindings() -> dict[str, int]:
    rows = tsv_rows("""
        SELECT tac, sum(bindings)
        FROM sqm.agg_device_model
        WHERE seq = (SELECT max(seq) FROM sqm.mart_ready)
        GROUP BY tac
        FORMAT TSV""", 2)
    return {r[0]: int(r[1]) for r in rows}


# --------------------------------------------------------------------------- the run

def process_images(package_dir: str, wanted: dict[str, str], workers: int) -> dict[str, ImageResult]:
    """
    Decode, measure and normalise each image once, however many models share it.

    In parallel because WebP method 6 - the encoder setting normalise() uses - takes ~3 s for an
    800x800 RGBA tile, and the package holds 1,279 images.
    """
    results: dict[str, ImageResult] = {}
    started = time.time()
    if workers <= 1:
        for i, (image_file, sha) in enumerate(sorted(wanted.items()), 1):
            results[image_file] = process_image(package_dir, image_file, sha)
            if i % 100 == 0:
                say(f"  {i:,} of {len(wanted):,} images ({time.time() - started:.0f} s)")
        return results

    with concurrent.futures.ProcessPoolExecutor(max_workers=workers) as pool:
        futures = {pool.submit(process_image, package_dir, f, sha): f
                   for f, sha in sorted(wanted.items())}
        for i, future in enumerate(concurrent.futures.as_completed(futures), 1):
            results[futures[future]] = future.result()
            if i % 100 == 0 or i == len(futures):
                say(f"  {i:,} of {len(futures):,} images ({time.time() - started:.0f} s)")
    return results


def summarise(rows: list[Row], already: set[tuple[str, str]], live: dict[str, tuple[str, str]],
              plan_stats: dict, failed: dict[str, str], package: str, mode: str) -> dict:
    by_code: dict[str, collections.Counter] = collections.defaultdict(collections.Counter)
    for row in rows:
        for w in row.warnings:
            by_code[w["code"]][w["severity"]] += 1

    upscales = sorted({r.image.image_file: r.image.upscale for r in rows}.values())
    models = {r.model_key: r.planned.model_bindings for r in rows}
    staged = [r for r in rows if (r.model_key, r.image.digest) in already]
    same_as_live = [r for r in rows if live.get(r.model_key, ("", ""))[1] == r.image.digest]

    return {
        "package": package,
        "mode": mode,
        "models": len(models),
        "modelsOnNetwork": sum(1 for b in models.values() if b > 0),
        "candidates": len(rows),
        "candidatesWithAnyWarning": sum(1 for r in rows if r.warnings),
        "candidatesWithHighWarning": sum(1 for r in rows if any(w["severity"] == HIGH
                                                                for w in r.warnings)),
        "alreadyStaged": len(staged),
        "identicalToLive": len(same_as_live),
        "imagesProcessed": len(upscales) + len(failed),
        "imagesFailed": failed,
        "warnings": {code: {"total": sum(c.values()), "high": c[HIGH], "info": c[INFO]}
                     for code, c in sorted(by_code.items())},
        "upscale": {
            "min": round(upscales[0], 3) if upscales else None,
            "median": round(statistics.median(upscales), 3) if upscales else None,
            "max": round(upscales[-1], 3) if upscales else None,
            "enlarged": sum(1 for u in upscales if u > 1),
            "over1_5": sum(1 for u in upscales if u > UPSCALE_WARN),
            "atLeast2": sum(1 for u in upscales if u >= UPSCALE_HIGH),
        },
        **{k: v for k, v in plan_stats.items() if k != "imagesWithoutAModel"},
        "imagesWithoutAModelCount": len(plan_stats.get("imagesWithoutAModel", [])),
        "imagesWithoutAModel": plan_stats.get("imagesWithoutAModel", []),
    }


def report_entry(row: Row, already: set[tuple[str, str]]) -> dict:
    ok, note = identity_of(row.planned)
    return {
        "modelKey": row.model_key,
        "brand": row.planned.model.brand,
        "marketingName": row.planned.model.marketing_name,
        "imageFile": row.planned.image_file,
        "productName": row.planned.product.product_name,
        "bindings": row.planned.model_bindings,
        "mappedTacs": row.planned.mapped_tacs,
        "modelTacs": row.planned.model_tacs,
        "mappedBindings": row.planned.mapped_bindings,
        "qualityScore": row.quality_score,
        "source": f"{row.image.width}x{row.image.height}",
        "upscale": round(row.image.upscale, 4),
        "storedBytes": len(row.image.stored),
        "identity": {"ok": ok, "note": note},
        "alreadyStaged": (row.model_key, row.image.digest) in already,
        "warnings": row.warnings,
    }


def run(args: argparse.Namespace) -> int:
    package_dir = os.path.abspath(args.package)
    package = os.path.basename(os.path.normpath(package_dir))
    mode = "apply" if args.apply else "dry-run"

    say(f"package:  {package_dir}")
    say(f"mode:     {'APPLY - stages candidates, never touches a live image' if args.apply else 'DRY RUN - writes nothing'}")

    products, package_tacs = read_package(package_dir)
    say(f"package:  {len(products):,} products, {len(package_tacs):,} TACs with an image")

    tac_rows = load_tac_rows()
    bindings = load_bindings()
    say(f"ours:     {len(tac_rows):,} named TACs, {len(bindings):,} TACs with active bindings")

    the_plan = plan(package_tacs, products, tac_rows, bindings)
    candidates = the_plan.candidates

    if args.model:
        needle = args.model.lower()
        candidates = [c for c in candidates if needle in c.model.marketing_name.lower()]
    if args.limit:
        keep = set(list(dict.fromkeys(c.model.key for c in candidates))[:args.limit])
        candidates = [c for c in candidates if c.model.key in keep]

    wanted: dict[str, str] = {}
    for c in candidates:
        wanted.setdefault(c.image_file, c.product.sha256)
    say(f"plan:     {len({c.model.key for c in candidates}):,} models, "
        f"{len(candidates):,} candidates, {len(wanted):,} images to normalise")

    live = live_images()
    already = known_candidate_hashes()
    say(f"live:     {len(live):,} images "
        f"({sum(1 for s, _ in live.values() if s == 'verified')} verified); "
        f"{len(already):,} candidates already staged")
    say("")

    images = process_images(package_dir, wanted, args.workers)
    failed = {f: r.error for f, r in images.items() if r.error}

    rows: list[Row] = []
    seen: set[tuple[str, str]] = set()
    for c in candidates:
        image = images[c.image_file]
        if image.error:
            continue
        # Two files of one model normalising to identical bytes would be one candidate.
        if (c.model.key, image.digest) in seen:
            continue
        seen.add((c.model.key, image.digest))
        rows.append(build_row(c, image, live.get(c.model.key, (None, ""))[0], package))

    summary = summarise(rows, already, live, the_plan.stats, failed, package, mode)

    say("")
    say(f"models:              {summary['models']:,} ({summary['modelsOnNetwork']:,} on the network)")
    say(f"candidates:          {summary['candidates']:,} "
        f"({summary['candidatesWithAnyWarning']:,} with a warning, "
        f"{summary['candidatesWithHighWarning']:,} with a high one)")
    say(f"already staged:      {summary['alreadyStaged']:,}")
    say(f"identical to live:   {summary['identicalToLive']:,}")
    say(f"package TACs not in our GSMA version: {summary['packageTacsNotInOurGsma']:,}")
    say(f"package images with no model of ours: {summary['imagesWithoutAModelCount']:,}")
    say(f"images that failed:  {len(failed):,}")
    for image_file, error in list(failed.items())[:10]:
        say(f"    {image_file}: {error}")
    u = summary["upscale"]
    say(f"scale applied:       min {u['min']}, median {u['median']}, max {u['max']}; "
        f"{u['enlarged']:,} enlarged, {u['over1_5']:,} past 1.5x, {u['atLeast2']:,} at 2x or more")
    say("")
    say("warnings              total    high    info")
    for code, counts in summary["warnings"].items():
        say(f"  {code:<20}{counts['total']:>6,}{counts['high']:>8,}{counts['info']:>8,}")
    say("")
    say("top 10 by bindings")
    for row in rows[:10]:
        codes = ", ".join(f"{w['code']}{'!' if w['severity'] == HIGH else ''}"
                          for w in row.warnings) or "no warnings"
        say(f"  {row.planned.model_bindings:>12,}  {row.planned.model.brand} "
            f"{row.planned.model.marketing_name}  <- {row.planned.product.product_name}")
        say(f"  {'':>12}  {row.planned.mapped_tacs}/{row.planned.model_tacs} TACs, "
            f"x{row.image.upscale:.2f}, score {row.quality_score}: {codes}")

    exit_code = 1 if failed else 0

    if args.apply:
        pending = [r for r in rows if (r.model_key, r.image.digest) not in already]
        say("")
        say(f"staging {len(pending):,} candidates ({len(rows) - len(pending):,} already staged)")
        inserted, failed_rows, errors = insert_rows(pending)
        summary["inserted"] = len(inserted)
        summary["insertErrors"] = errors
        # Already staged by a concurrent run between the read above and the insert.
        summary["skippedByConflict"] = len(pending) - len(inserted) - failed_rows
        say(f"inserted:            {len(inserted):,}")
        say(f"skipped (conflict):  {summary['skippedByConflict']:,}")
        for error in errors:
            say(f"FAILED batch {error}")
        if errors:
            exit_code = 1
        say("\nCandidates are staged. No live image has been changed - approve them on the "
            "Device image review page.")
    else:
        say("\nNothing was written. Re-run with --apply to stage these for review.")

    if args.report:
        report = {"summary": summary, "candidates": [report_entry(r, already) for r in rows]}
        with open(args.report, "w", encoding="utf-8") as fh:
            json.dump(report, fh, indent=2, ensure_ascii=False)
        say(f"report:   {args.report}")

    return exit_code


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Stage a curated device image package for review. Never replaces a live image.")
    parser.add_argument("--package", required=True, help="the package folder (holds images/, "
                        "image_sources.csv and TAC_to_image.csv)")
    parser.add_argument("--apply", action="store_true",
                        help="stage CANDIDATES. The default is a dry run that writes nothing; "
                             "approval is a separate, human step in the review page.")
    parser.add_argument("--model", help="substring of the marketing name, e.g. 'Galaxy A1'")
    parser.add_argument("--limit", type=int, default=0,
                        help="only the N models with the most bindings (after --model)")
    parser.add_argument("--report", help="write a JSON summary and every candidate to this path")
    parser.add_argument("--workers", type=int, default=max(1, min(8, (os.cpu_count() or 2) - 2)),
                        help="processes normalising images in parallel")
    return run(parser.parse_args())


if __name__ == "__main__":
    sys.exit(main())
