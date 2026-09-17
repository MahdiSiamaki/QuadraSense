"""
Propose device images for review. Never replaces a live image.

WHAT THIS REPLACED, AND WHY
---------------------------
The previous version asked Wikipedia for the *lead image of an article* and stored it. Four
faults, each sufficient on its own:

  1. The source was an encyclopedia, not a product catalogue. A lead image is whatever a
     volunteer uploaded - which is how the catalogue acquired shop shelves with price tags
     visible, and how four Nokia models acquired a photograph of Nokia's headquarters.
  2. There was no candidate set. One lead image per model means the first result wins by
     construction; nothing competes and nothing is scored.
  3. Every guard was textual. Filename tokens and variant words check identity and never look
     at a single pixel, so background, crop, resolution and composition went unchecked.
  4. There was no lifecycle. An auto-sourced image and a human-chosen one were the same row, so
     84 of 87 stored images were never reviewed and nothing said so.

This version fixes all four: an allowlisted source set, a candidate SET per model, a
deterministic score computed from the decoded pixels with its reasoning stored alongside, and a
staging table a reviewer approves from. It writes to catalog.device_image_candidate and NEVER to
catalog.device_model_image.

SAFETY
------
Default execution is a dry run. `--apply` writes CANDIDATES - it does not touch a live image, and
there is deliberately no flag in this tool that does. Replacement happens only through the review
endpoint, by a person holding device.image.manage.

USAGE
-----
    python tools/source_device_images.py --limit 20                  # dry run, prints a report
    python tools/source_device_images.py --limit 20 --apply          # stages candidates
    python tools/source_device_images.py --manufacturer Samsung --apply
    python tools/source_device_images.py --model "Galaxy A32" --apply
    python tools/source_device_images.py --only-missing --limit 50 --apply
"""

from __future__ import annotations

import argparse
import base64
import dataclasses
import hashlib
import io
import ipaddress
import json
import os
import re
import socket
import subprocess
import sys
import time
import urllib.parse

# --------------------------------------------------------------------------- configuration

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCES_FILE = os.path.join(HERE, "device_image_sources.json")

UA = "QuadraSense-device-catalogue/2.0 (internal telecom device inventory)"
COMMONS_API = "https://commons.wikimedia.org/w/api.php"

CLICKHOUSE = ("http://localhost:18123/?database=sqm", "sqm_app", "sqm_dev")
POSTGRES = ("sqm-postgres", "sqm", "sqm")

# Presentation target. 800x800 with the product filling ~84% leaves the 6-10% breathing space a
# catalogue tile needs on every side, whatever shape the source was.
CANVAS = 800
PADDING_FRACTION = 0.08
STORED_FORMAT = "WEBP"
STORED_MIME = "image/webp"
MAX_STORED_BYTES = 512 * 1024          # matches the database CHECK

# A source below this on either edge is a thumbnail. Upscaling one produces a soft, obviously
# second-rate tile next to a real render, which is worse than the placeholder.
MIN_SOURCE_EDGE = 400
PREFERRED_SOURCE_EDGE = 1000

# What width to ask the file host for. Above PREFERRED_SOURCE_EDGE so nothing is lost to the
# resolution score, and far below a typical Commons original, which is frequently 20 megapixels.
RENDITION_WIDTH = 1400

# Wikimedia answers 429 when asked too quickly, and answers it with an HTML page rather than an
# error the transport reports. Retrying politely is the difference between "this model has no
# usable image" and "we asked too fast".
MAX_ATTEMPTS = 4
RETRY_BACKOFF_SECONDS = 6
POLITE_DELAY_SECONDS = 1.5

ACCEPTED_MIME = {"image/png", "image/jpeg", "image/webp"}
MAGIC = {
    b"\x89PNG\r\n\x1a\n": "image/png",
    b"\xff\xd8\xff": "image/jpeg",
}


def load_config() -> dict:
    with open(SOURCES_FILE, encoding="utf-8") as fh:
        return json.load(fh)


CONFIG = load_config()
ALLOWED = CONFIG["sources"]
LIMITS = CONFIG["limits"]


# --------------------------------------------------------------------------- plumbing

def say(text: str) -> None:
    """
    Print without dying on a console that cannot encode the text, and without buffering.

    flush=True because this runs for minutes and its output is how anybody knows it is alive.
    Piped to a file or a pager, Python buffers stdout in 8 KB blocks - which looked exactly like
    a hung process the first time this was run in the background.
    """
    encoding = getattr(sys.stdout, "encoding", None) or "utf-8"
    print(text.encode(encoding, errors="replace").decode(encoding, errors="replace"), flush=True)


def clickhouse(sql: str) -> str:
    url, user, password = CLICKHOUSE
    out = subprocess.run(["curl", "-sS", "-u", f"{user}:{password}", url, "--data-binary", sql],
                         capture_output=True, text=True, encoding="utf-8", check=True)
    return out.stdout


def psql(sql: str) -> str:
    container, user, db = POSTGRES
    out = subprocess.run(
        ["docker", "exec", "-i", container, "psql", "-U", user, "-d", db,
         "-v", "ON_ERROR_STOP=1", "-t", "-A"],
        input=sql, capture_output=True, text=True, encoding="utf-8")
    if out.returncode != 0:
        raise RuntimeError(out.stderr.strip())
    return out.stdout.strip()


def esc(text: str) -> str:
    return (text or "").replace("'", "''")


# --------------------------------------------------------------------------- identity

VARIANTS = {"pro", "plus", "ultra", "max", "lite", "mini", "fe", "prime", "neo", "power",
            "5g", "4g", "lte"}


def norm(text: str) -> str:
    return re.sub(r"\s+", " ", (text or "").strip()).lower()


def tokens(text: str) -> list[str]:
    return [t for t in re.split(r"[^a-z0-9]+", (text or "").lower()) if t]


@dataclasses.dataclass(frozen=True)
class DeviceIdentity:
    """
    Enough of a device to tell it from its neighbours.

    The IMAGE is keyed by (brand, marketing name) because that is what a picture depicts: the
    GSMA record gives Galaxy A51 sixteen model codes - SM-A515F, SM-A515F/DS, SM-A515U and so on -
    which are region and dual-SIM variants of one physical product, and sixteen copies of one
    render would be sixteen things to keep in step.

    The model CODES are carried anyway, because they are what proves a variant is not being
    confused. marketingName already separates the ones that matter - Galaxy A32 is SM-A325x,
    Galaxy A32 5G is SM-A326x - and the codes let a candidate be checked against both.
    """

    manufacturer: str
    brand: str
    marketing_name: str
    model_codes: tuple[str, ...]
    device_type: str
    bindings: int

    @property
    def model_key(self) -> str:
        return f"{norm(self.brand)}|{norm(self.marketing_name)}"

    @property
    def search_title(self) -> str:
        name = re.sub(r"\s*\([^)]*\)", "", self.marketing_name)
        if "iphone" in name.lower():
            name = name[name.lower().index("iphone"):]
        elif self.brand and not name.lower().startswith(self.brand.lower()):
            name = f"{self.brand} {name}"
        if name.lower().startswith("galaxy "):
            name = "Samsung " + name
        return re.sub(r"\s+", " ", name).strip()

    def describe(self) -> str:
        codes = ", ".join(self.model_codes[:3])
        more = f" (+{len(self.model_codes) - 3} more)" if len(self.model_codes) > 3 else ""
        return f"{self.brand} {self.marketing_name}\n           {codes}{more}"


def load_devices(limit: int, manufacturer: str | None, model: str | None,
                 only_missing: bool) -> list[DeviceIdentity]:
    where = ["d.seq = (SELECT max(seq) FROM sqm.mart_ready)", "t.marketingName != ''"]
    if manufacturer:
        safe = esc(manufacturer)
        where.append(
            f"(lower(coalesce(nullIf(t.brandName,''), t.manufacturer)) = lower('{safe}')"
            f" OR lower(t.manufacturer) LIKE lower('%{safe}%'))")
    if model:
        where.append(f"lower(t.marketingName) LIKE lower('%{esc(model)}%')")

    sql = f"""
        SELECT any(t.manufacturer)                              AS manufacturer,
               coalesce(nullIf(t.brandName,''), t.manufacturer) AS brand,
               t.marketingName                                  AS marketing_name,
               arrayStringConcat(groupUniqArray(20)(t.modelName), '~') AS model_codes,
               any(t.deviceType)                                AS device_type,
               sum(d.bindings)                                  AS bindings
        FROM sqm.agg_device_model AS d
        INNER JOIN sqm.tac AS t ON t.tac = d.tac
        WHERE {' AND '.join(where)}
        GROUP BY brand, marketing_name
        ORDER BY bindings DESC
        LIMIT {int(limit) * 4}
        FORMAT TSV
    """

    devices: list[DeviceIdentity] = []
    for line in clickhouse(sql).splitlines():
        if not line.strip():
            continue
        maker, brand, name, codes, device_type, bindings = line.split("\t")
        devices.append(DeviceIdentity(
            manufacturer=maker, brand=brand, marketing_name=name,
            model_codes=tuple(c for c in codes.split("~") if c),
            device_type=device_type, bindings=int(bindings)))

    if only_missing:
        have = {k for k in psql("SELECT model_key FROM catalog.device_model_image").splitlines() if k}
        devices = [d for d in devices if d.model_key not in have]

    return devices[:limit]


def live_images() -> dict[str, tuple[str, str]]:
    """model_key -> (status, sha256 hex) for whatever is currently being served."""
    out: dict[str, tuple[str, str]] = {}
    rows = psql("SELECT model_key || E'\\t' || status || E'\\t' || encode(sha256,'hex') "
                "FROM catalog.device_model_image").splitlines()
    for row in rows:
        parts = row.strip().split("\t")
        if len(parts) == 3:
            out[parts[0]] = (parts[1], parts[2])
    return out


def known_candidate_hashes() -> set[tuple[str, str]]:
    """
    (model_key, sha256) already proposed, in ANY state.

    Rejected ones are included on purpose: a candidate a reviewer has turned down must not
    reappear on the next run. Only a genuinely different image is a new proposal.
    """
    out = set()
    rows = psql("SELECT model_key || E'\\t' || encode(sha256,'hex') "
                "FROM catalog.device_image_candidate").splitlines()
    for row in rows:
        parts = row.strip().split("\t")
        if len(parts) == 2:
            out.add((parts[0], parts[1]))
    return out


# --------------------------------------------------------------------------- safe fetching

class FetchError(Exception):
    """A candidate could not be retrieved safely. Always a rejection, never a retry loop."""


def host_of(url: str) -> str:
    return (urllib.parse.urlparse(url).hostname or "").lower()


def check_public_host(host: str) -> None:
    """
    Refuse anything that resolves inside the network this runs on.

    The allowlist already limits us to a few public hosts, so this is the second lock rather than
    the first: it stops an allowlisted name that has been pointed somewhere private, and it is
    what makes following a redirect safe at all. Rebinding between this check and the fetch is
    not fully closed by it - the allowlist is what bounds that.
    """
    try:
        infos = socket.getaddrinfo(host, 443, proto=socket.IPPROTO_TCP)
    except OSError as exc:
        raise FetchError(f"cannot resolve {host}: {exc}") from exc

    for info in infos:
        address = ipaddress.ip_address(info[4][0])
        if (address.is_private or address.is_loopback or address.is_link_local
                or address.is_reserved or address.is_multicast):
            raise FetchError(f"{host} resolves to a non-public address ({address})")


def fetch(url: str, *, expect_json: bool = False):
    """
    Download one thing, treating the far end as hostile.

    https only, allowlisted host, public address, bounded redirects, bounded time, bounded size.
    The effective URL is re-checked after redirects, because a redirect to a host nobody vetted is
    exactly the trick this guards against.
    """
    if not url.lower().startswith("https://"):
        raise FetchError(f"not https: {url[:60]}")

    host = host_of(url)
    if host not in ALLOWED:
        raise FetchError(f"domain not allowlisted: {host}")
    check_public_host(host)

    marker = b"\n---META---"
    command = [
        "curl", "-sS", "--proto", "=https", "--proto-redir", "=https",
        "--location", "--max-redirs", str(LIMITS["max_redirects"]),
        "--max-time", str(LIMITS["timeout_seconds"]),
        "--max-filesize", str(LIMITS["max_download_bytes"]),
        "-A", UA,
        "-w", marker.decode() + "%{http_code} %{url_effective}",
        url,
    ]

    last_error = "not attempted"

    for attempt in range(MAX_ATTEMPTS):
        time.sleep(POLITE_DELAY_SECONDS)
        out = subprocess.run(command, capture_output=True)
        if out.returncode != 0:
            raise FetchError(
                f"transport failed ({out.returncode}): "
                f"{out.stderr.decode(errors='replace')[:120]}")

        body, separator, meta = out.stdout.rpartition(marker)
        if not separator:
            raise FetchError("no response metadata; cannot confirm the status")

        status_text, _, effective = meta.decode(errors="replace").partition(" ")
        status = int(status_text) if status_text.isdigit() else 0

        # The status is checked BEFORE the bytes. Without this a rate-limited request looked like
        # a corrupt image: Wikimedia answers 429 with a 2,255-byte HTML page, which sniffed as no
        # known format and was reported as "bytes are not a supported image" - true, and the
        # wrong diagnosis entirely.
        if status == 429 or 500 <= status < 600:
            last_error = f"HTTP {status} from {host}"
            time.sleep(RETRY_BACKOFF_SECONDS * (attempt + 1))
            continue

        if status != 200:
            raise FetchError(f"HTTP {status} from {host}")

        final_host = host_of(effective) if effective else host
        if final_host and final_host not in ALLOWED:
            raise FetchError(f"redirected off the allowlist to {final_host}")

        if len(body) > LIMITS["max_download_bytes"]:
            raise FetchError("larger than the download ceiling")

        if expect_json:
            return json.loads(body.decode("utf-8"))
        return body, final_host or host

    raise FetchError(f"{last_error} after {MAX_ATTEMPTS} attempts")


def sniff(data: bytes) -> str | None:
    """
    The media type from the bytes, never from the header the server claimed.

    WebP needs both ends checked: RIFF alone also begins a WAV file.
    """
    for signature, mime in MAGIC.items():
        if data.startswith(signature):
            return mime
    if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return "image/webp"
    return None


# --------------------------------------------------------------------------- providers

@dataclasses.dataclass
class Candidate:
    url: str
    domain: str
    source_type: str
    title: str
    width: int = 0
    height: int = 0
    mime: str = ""
    licence: str = ""
    author: str = ""


def commons_candidates(device: DeviceIdentity, want: int = 8) -> list[Candidate]:
    """
    A SET of files from Commons that name this model, not one article's lead image.

    Searching the File namespace rather than reading an article is the structural fix for "the
    first result wins": it returns several files with their real dimensions and media types,
    which is what a score needs something to choose between.
    """
    # intitle:, not a free-text phrase. Commons full-text search matches file DESCRIPTIONS, and
    # searching '"Redmi Note 13"' that way returned a house in Volgograd, a hotel in Nairobi and
    # a heatwave in Hackney - every one correctly rejected by the identity guard, and every one a
    # wasted download. Searching the file NAME is also what the guard checks, so the two agree.
    search = fetch(COMMONS_API + "?" + urllib.parse.urlencode({
        "action": "query", "list": "search",
        "srsearch": f'intitle:"{device.search_title}"',
        "srnamespace": "6", "srlimit": str(want), "format": "json",
    }), expect_json=True)

    titles = [hit["title"] for hit in search.get("query", {}).get("search", [])]
    if not titles:
        return []

    # Ask for a rendition at the width we actually want, not the original. A Commons original is
    # often a 20-megapixel photograph, and we normalise to 800px square - downloading tens of
    # megabytes to throw away 95% of them is what got this IP rate-limited by the file host in the
    # first place. RENDITION_WIDTH stays above PREFERRED_SOURCE_EDGE so the resolution score is
    # unaffected.
    info = fetch(COMMONS_API + "?" + urllib.parse.urlencode({
        "action": "query", "prop": "imageinfo",
        "iiprop": "url|size|mime|extmetadata",
        "iiurlwidth": str(RENDITION_WIDTH),
        "titles": "|".join(titles[:20]), "format": "json",
    }), expect_json=True)

    out: list[Candidate] = []
    for page in info.get("query", {}).get("pages", {}).values():
        details = (page.get("imageinfo") or [{}])[0]

        # The rendition when the server made one, the original when it did not. Its dimensions
        # are the ones scored, because they are the ones that will be normalised.
        url = details.get("thumburl") or details.get("url")
        if not url:
            continue
        meta = details.get("extmetadata", {})
        out.append(Candidate(
            url=url, domain=host_of(url), source_type="encyclopedic",
            title=page.get("title", "").removeprefix("File:"),
            # The ORIGINAL dimensions, never the rendition's. Commons echoes the width that was
            # ASKED for even when the file is smaller and it can only upscale: requesting 1400
            # from a 195x400 file reports thumbwidth 1400x2872. Recording that would let a
            # thumbnail claim to be high-resolution. Scoring uses the decoded bytes regardless,
            # so this is provenance rather than a second line of defence - but a provenance field
            # that lies is worse than none.
            width=int(details.get("width") or 0),
            height=int(details.get("height") or 0),
            mime=details.get("mime", ""),
            licence=(meta.get("LicenseShortName") or {}).get("value", "unknown licence"),
            author=re.sub(r"<[^>]+>", "", (meta.get("Artist") or {}).get("value", "")).strip()))
    return out


def manufacturer_candidates(device: DeviceIdentity) -> list[Candidate]:
    """
    Official product renders, when a provider for the brand is configured.

    None is configured, and the configuration file says why: the official renders for Samsung,
    Apple and Xiaomi sit behind per-product paths that cannot be derived from a TAC record, so
    reaching them needs a licensed catalogue feed or a per-vendor adapter agreed with the vendor.
    This is the seam for one. It returns nothing rather than substituting something that is not a
    product render and calling it one.
    """
    configured = CONFIG.get("manufacturer_candidates", {})
    rule = configured.get(norm(device.brand)) or configured.get(norm(device.manufacturer))
    if not rule:
        return []

    out: list[Candidate] = []
    for code in device.model_codes[:4]:
        url = rule["template"].format(code=urllib.parse.quote(code.replace("/", "-")))
        if host_of(url) not in ALLOWED:
            continue
        out.append(Candidate(url=url, domain=host_of(url), source_type="manufacturer", title=code))
    return out


# --------------------------------------------------------------------------- identity matching

def identity_verdict(device: DeviceIdentity, candidate: Candidate) -> tuple[bool, str]:
    """
    Does this file name THIS model, and no richer variant of it?

    Kept from the previous version because it is the part that worked: token-exact matching with
    a variant guard is what stopped Redmi Note 12S being given Redmi Note 12's photograph, and
    Galaxy A05 being given the A05s.
    """
    name = candidate.title or ""
    if not name:
        return False, "the file has no name to match against"

    haystack = set(tokens(name))
    brand_tokens = set(tokens(device.brand)) | set(tokens(device.manufacturer))
    wanted = [t for t in tokens(device.search_title) if t not in brand_tokens]

    if not wanted:
        return False, "the model name is only a brand, so nothing distinguishes it"

    missing = [t for t in wanted if t not in haystack]
    if missing:
        return False, f"does not name this model (missing {', '.join(missing)})"

    extra = (haystack & VARIANTS) - set(tokens(device.search_title))
    if extra:
        return False, f"names a different variant ({', '.join(sorted(extra))})"

    codes = {norm(c).replace("/", "") for c in device.model_codes}
    flat = norm(name).replace("/", "").replace("-", "").replace(" ", "")
    confirmed = any(c.replace("-", "") in flat for c in codes if len(c) > 5)

    return True, "model code confirmed in the file name" if confirmed else "model name matched"


# --------------------------------------------------------------------------- pixel analysis

def analyse(data: bytes) -> dict:
    """
    What the pixels say, in terms a score can use and a person can check.

    Every measure is deterministic and cheap, and each maps to something a catalogue image has to
    be: shot on a clean background, whole, not a thumbnail, not a photograph of a scene. None of
    it is a classifier - there is no model here deciding "lifestyle photo" - so each measure is
    named for what it actually computes, and the limits of that are in the docs.
    """
    from PIL import Image, ImageOps

    Image.MAX_IMAGE_PIXELS = LIMITS["max_decoded_pixels"]

    with Image.open(io.BytesIO(data)) as raw:
        raw.load()
        image = raw.convert("RGBA")

    try:
        image = ImageOps.exif_transpose(image)
    except Exception:                                   # noqa: BLE001 - orientation is best effort
        pass

    width, height = image.size
    pixels = image.load()

    def is_background(px) -> bool:
        r, g, b, a = px
        return a < 24 or (r > 238 and g > 238 and b > 238)

    # Border cleanliness: a studio render sits on white or on nothing. A shelf photograph does not.
    band = max(2, int(min(width, height) * 0.02))
    border_total = border_clean = 0
    for x in range(0, width, max(1, width // 120)):
        for y in list(range(band)) + list(range(max(band, height - band), height)):
            border_total += 1
            border_clean += is_background(pixels[x, y])
    for y in range(0, height, max(1, height // 120)):
        for x in list(range(band)) + list(range(max(band, width - band), width)):
            border_total += 1
            border_clean += is_background(pixels[x, y])

    cleanliness = border_clean / border_total if border_total else 0.0

    # Where the product actually is.
    step = max(1, min(width, height) // 240)
    min_x, min_y, max_x, max_y = width, height, -1, -1
    for x in range(0, width, step):
        for y in range(0, height, step):
            if not is_background(pixels[x, y]):
                min_x, max_x = min(min_x, x), max(max_x, x)
                min_y, max_y = min(min_y, y), max(max_y, y)

    if max_x < 0:
        return {"width": width, "height": height, "cleanliness": cleanliness,
                "coverage": 0.0, "edges_touched": 4, "colours": 0, "has_alpha": False}

    coverage = ((max_x - min_x) * (max_y - min_y)) / float(width * height)

    margin_x, margin_y = width * 0.01, height * 0.01
    edges_touched = sum([
        min_x <= margin_x, min_y <= margin_y,
        max_x >= width - margin_x, max_y >= height - margin_y,
    ])

    # Colour complexity separates a render from a scene: a product on white has few distinct
    # quantised colours, a photograph of a shop has many.
    colours = len(set(image.convert("RGB").resize((64, 64)).getdata()))
    has_alpha = image.getchannel("A").getextrema()[0] < 250

    return {"width": width, "height": height, "cleanliness": cleanliness, "coverage": coverage,
            "edges_touched": edges_touched, "colours": colours, "has_alpha": has_alpha}


# --------------------------------------------------------------------------- scoring

@dataclasses.dataclass
class Score:
    total: int
    breakdown: list[dict]
    rejected: str | None = None


def score_candidate(device: DeviceIdentity, candidate: Candidate, facts: dict,
                    identity_note: str) -> Score:
    """
    A deterministic score with every term recorded.

    Rejections come first and are absolute: a candidate that breaks one is never redeemed by
    scoring well elsewhere, because a beautiful picture of the wrong phone is the failure this
    whole exercise exists to prevent.
    """
    breakdown: list[dict] = []

    def add(name: str, points: int, detail: str) -> None:
        breakdown.append({"term": name, "points": points, "detail": detail})

    if min(facts["width"], facts["height"]) < MIN_SOURCE_EDGE:
        return Score(0, breakdown, f"below {MIN_SOURCE_EDGE}px on an edge "
                                   f"({facts['width']}x{facts['height']}) - a thumbnail")

    if facts["coverage"] < 0.18:
        return Score(0, breakdown, f"product fills only {facts['coverage']:.0%} of the frame "
                                   "- a scene, not a product shot")

    if facts["edges_touched"] >= 2:
        return Score(0, breakdown, f"content touches {facts['edges_touched']} edges "
                                   "- the device is cropped")

    if facts["cleanliness"] < 0.55:
        return Score(0, breakdown, f"background only {facts['cleanliness']:.0%} clean "
                                   "- not a studio backdrop")

    aspect = facts["width"] / facts["height"] if facts["height"] else 0
    if aspect > 3 or aspect < 0.25:
        return Score(0, breakdown, f"aspect ratio {aspect:.2f} - a banner, not a product shot")

    rule = ALLOWED.get(candidate.domain, {})
    trust = int(rule.get("trust", 0))
    add("sourceTrust", 25 if rule.get("type") == "manufacturer" else round(trust * 0.2),
        f"{rule.get('type', 'unknown')} source ({candidate.domain}, trust {trust})")

    add("exactModelMatch", 30, identity_note)

    edge = min(facts["width"], facts["height"])
    add("resolution",
        15 if edge >= PREFERRED_SOURCE_EDGE
        else round(15 * (edge - MIN_SOURCE_EDGE) / (PREFERRED_SOURCE_EDGE - MIN_SOURCE_EDGE)),
        f"{facts['width']}x{facts['height']} source")

    add("cleanBackground", round(10 * max(0.0, (facts["cleanliness"] - 0.55) / 0.45)),
        f"{facts['cleanliness']:.0%} of the border is white or clear")

    add("productCoverage", 8 if 0.45 <= facts["coverage"] <= 0.92 else 4,
        f"product fills {facts['coverage']:.0%} of the frame")

    add("renderLike", 7 if facts["colours"] <= 900 else (3 if facts["colours"] <= 2500 else 0),
        f"{facts['colours']} distinct colours at 64x64")

    if facts["has_alpha"]:
        add("transparency", 5, "carries an alpha channel")

    return Score(sum(t["points"] for t in breakdown), breakdown)


# --------------------------------------------------------------------------- normalisation

def normalise(data: bytes) -> tuple[bytes, str]:
    """
    One canvas, one format, contain-fit, never cropped and never stretched.

    800x800 with 8% padding means the product occupies about 84% of the tile. Re-encoding drops
    the source metadata, which is how EXIF and anything else riding along leaves.
    """
    from PIL import Image, ImageOps

    Image.MAX_IMAGE_PIXELS = LIMITS["max_decoded_pixels"]

    with Image.open(io.BytesIO(data)) as raw:
        raw.load()
        image = ImageOps.exif_transpose(raw.convert("RGBA"))

    # Trim the existing background so the padding below is measured from the product itself
    # rather than from whatever margin the source happened to have.
    alpha_bbox = image.getchannel("A").getbbox()
    white = Image.new("RGBA", image.size, (255, 255, 255, 255))
    bbox = alpha_bbox or Image.alpha_composite(white, image).convert("RGB").getbbox()
    if bbox:
        image = image.crop(bbox)

    inner = int(CANVAS * (1 - 2 * PADDING_FRACTION))
    image.thumbnail((inner, inner), Image.LANCZOS)

    canvas = Image.new("RGBA", (CANVAS, CANVAS), (255, 255, 255, 0))
    canvas.paste(image, ((CANVAS - image.width) // 2, (CANVAS - image.height) // 2), image)

    for quality in (90, 82, 74, 66, 58):
        buffer = io.BytesIO()
        canvas.save(buffer, format=STORED_FORMAT, quality=quality, method=6)
        if buffer.tell() <= MAX_STORED_BYTES:
            return buffer.getvalue(), STORED_MIME

    raise FetchError("cannot fit the normalised image under 512 KB")


# --------------------------------------------------------------------------- pipeline

def evaluate(device: DeviceIdentity):
    """Gather candidates for one device, score them all, return the winner and the reasons."""
    notes: list[str] = []
    pool: list[Candidate] = []

    for provider in (manufacturer_candidates, commons_candidates):
        try:
            pool.extend(provider(device))
        except FetchError as exc:
            notes.append(f"{provider.__name__}: {exc}")
        except Exception as exc:                        # noqa: BLE001 - one device must not stop a run
            notes.append(f"{provider.__name__}: {exc}")

    if not pool:
        return None, None, None, notes or ["no candidate from any allowlisted source"]

    best = None

    for candidate in pool:
        ok, identity_note = identity_verdict(device, candidate)
        if not ok:
            notes.append(f"{candidate.title}: {identity_note}")
            continue

        if candidate.mime and candidate.mime not in ACCEPTED_MIME:
            notes.append(f"{candidate.title}: media type {candidate.mime} not accepted")
            continue

        try:
            data, domain = fetch(candidate.url)
        except FetchError as exc:
            notes.append(f"{candidate.title}: {exc}")
            continue

        actual = sniff(data)
        if actual not in ACCEPTED_MIME:
            notes.append(f"{candidate.title}: bytes are not a supported image (sniffed {actual})")
            continue

        try:
            facts = analyse(data)
        except Exception as exc:                        # noqa: BLE001 - a corrupt image is a rejection
            notes.append(f"{candidate.title}: cannot decode ({exc})")
            continue

        candidate.domain = domain
        candidate.width, candidate.height, candidate.mime = facts["width"], facts["height"], actual
        score = score_candidate(device, candidate, facts, identity_note)

        if score.rejected:
            notes.append(f"{candidate.title}: {score.rejected}")
            continue

        if best is None or score.total > best[1].total:
            best = (candidate, score, data)

    if best is None:
        return None, None, None, notes

    return best[0], best[1], best[2], notes


def insert_candidate(device: DeviceIdentity, candidate: Candidate, score: Score,
                     data: bytes, mime: str, digest: str) -> None:
    payload = base64.b64encode(data).decode("ascii")
    psql(f"""
        INSERT INTO catalog.device_image_candidate
            (model_key, brand, marketing_name, status, content_type, bytes, sha256,
             source_type, source_domain, source_url,
             original_width, original_height, original_bytes,
             quality_score, score_breakdown)
        VALUES (
            '{esc(device.model_key)}', '{esc(device.brand)}', '{esc(device.marketing_name)}',
            'needs_review', '{mime}', decode('{payload}', 'base64'), decode('{digest}', 'hex'),
            '{esc(candidate.source_type)}', '{esc(candidate.domain)}', '{esc(candidate.url)}',
            {candidate.width}, {candidate.height}, {len(data)},
            {score.total}, '{esc(json.dumps(score.breakdown))}'::jsonb)
        ON CONFLICT (model_key, sha256) DO NOTHING
    """)


def run(args: argparse.Namespace) -> int:
    devices = load_devices(args.limit, args.manufacturer, args.model, args.only_missing)
    live = live_images()
    seen = known_candidate_hashes()

    say(f"devices selected:  {len(devices)}")
    say(f"live images:       {len(live)} "
        f"({sum(1 for s, _ in live.values() if s == 'verified')} verified)")
    say(f"mode:              "
        f"{'APPLY - stages candidates, never touches a live image' if args.apply else 'DRY RUN - writes nothing'}")
    say("")

    created = skipped = rejected = failed = 0

    for device in devices:
        status = live.get(device.model_key, ("missing", ""))[0]

        say(f"DEVICE:    {device.describe()}")
        say(f"CURRENT:   {status}")

        if status == "verified" and not args.include_unverified:
            say("ACTION:    skipped")
            say("REASON:    a reviewer has verified this image; pass --include-unverified "
                "to propose against it anyway")
            say("")
            skipped += 1
            continue

        candidate, score, data, notes = evaluate(device)

        if candidate is None or score is None or data is None:
            say("ACTION:    rejected")
            say(f"REASON:    {notes[0] if notes else 'no usable candidate'}")
            for note in notes[1:4]:
                say(f"           {note}")
            say("")
            rejected += 1
            continue

        try:
            stored, mime = normalise(data)
        except Exception as exc:                        # noqa: BLE001
            say("ACTION:    failed")
            say(f"REASON:    {exc}")
            say("")
            failed += 1
            continue

        digest = hashlib.sha256(stored).hexdigest()

        if (device.model_key, digest) in seen:
            say("ACTION:    skipped")
            say("REASON:    this exact image has been proposed before - already reviewed, "
                "or already waiting")
            say("")
            skipped += 1
            continue

        if live.get(device.model_key, ("", ""))[1] == digest:
            say("ACTION:    skipped")
            say("REASON:    identical to the image already being served")
            say("")
            skipped += 1
            continue

        say(f"CANDIDATE: {candidate.title}")
        say(f"           {candidate.domain} · {candidate.width}x{candidate.height} "
            f"· {candidate.source_type} · {candidate.licence or 'licence unknown'}")
        say(f"SCORE:     {score.total}")
        for term in score.breakdown:
            say(f"           {term['term']:>16}: {term['points']:+3d}  {term['detail']}")

        if args.apply:
            insert_candidate(device, candidate, score, stored, mime, digest)
            say("ACTION:    candidate-created")
        else:
            say("ACTION:    candidate-created (dry run - nothing written)")

        say("REASON:    awaiting review")
        say("")
        created += 1
        time.sleep(0.3)

    say("-" * 62)
    say(f"{'created' if args.apply else 'would create'}: {created}")
    say(f"skipped:  {skipped}")
    say(f"rejected: {rejected}")
    say(f"failed:   {failed}")
    if args.apply:
        say("\nCandidates are staged. No live image has been changed - approve them on the "
            "Device image review page.")
    else:
        say("\nNothing was written. Re-run with --apply to stage these for review.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Propose device images for review. Never replaces a live image.")
    parser.add_argument("--limit", type=int, default=20)
    parser.add_argument("--manufacturer", help="brand or manufacturer, e.g. Samsung")
    parser.add_argument("--model", help="substring of the marketing name, e.g. 'Galaxy A32'")
    parser.add_argument("--only-missing", action="store_true",
                        help="only devices with no live image at all")
    parser.add_argument("--include-unverified", action="store_true",
                        help="also propose against images that exist but nobody has verified")
    parser.add_argument("--dry-run", action="store_true",
                        help="the default; accepted so it can be written explicitly")
    parser.add_argument("--apply", action="store_true",
                        help="stage CANDIDATES. Does not touch a live image - approval is a "
                             "separate, human step in the review page.")
    args = parser.parse_args()

    if args.dry_run:
        args.apply = False

    return run(args)


if __name__ == "__main__":
    sys.exit(main())
