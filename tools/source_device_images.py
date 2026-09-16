"""
Populate the device catalogue's photographs from Wikimedia Commons.

WHY COMMONS AND NOT A BETTER-LOOKING SOURCE. GSMArena, manufacturer press pages and retailer
listings have uniform, flattering photography of almost every handset, and none of them carry a
licence this catalogue could cite. Commons files carry an explicit licence and a permanent URL,
which is exactly what `catalog.device_model_image.source_note` exists to hold. The cost is
coverage and consistency, measured rather than guessed - see the report this prints.

WHAT IT REFUSES TO DO, which is the important part. An earlier version of this took the top
search hit and was wrong in the most dangerous way available:

    Redmi Note 12S  ->  Redmi_Note_12_front.jpg          a different phone
    Redmi Note 11   ->  Redmi_Note_11_Pro_(Star_Blue).png a more expensive phone
    Galaxy A05      ->  Samsung_Galaxy_A05s_2024.jpg      a different phone
    Galaxy A15      ->  Logo_Samsung_Galaxy_A15.png       not a phone
    POCO M3         ->  Redmi_Note_9_4G.jpg               three phones in one lookup

A catalogue that shows a confident picture of the wrong handset is worse than one that shows a
placeholder, because nobody re-checks a picture that looks right. So a candidate is accepted only
when the FILE NAME names this model in whole tokens and adds no variant word the model does not
claim. Measured on the 200 most populous models: 60.5% have some image, 27% survive the guards.
The other third is the difference between a catalogue and a catalogue of plausible mistakes.

Usage:
    python tools/source_device_images.py --limit 300 --user admin [--apply]

Without --apply it reports what it would do and writes nothing.
"""

import argparse
import base64
import io
import json
import re
import subprocess
import sys
import time
import urllib.parse

UA = "QuadraSense-device-catalogue/1.0 (internal telecom device inventory)"
WIKI = "https://en.wikipedia.org/w/api.php"
COMMONS = "https://commons.wikimedia.org/w/api.php"

CH = ("http://localhost:18123/?database=sqm", "sqm_app", "sqm_dev")
PG = ("sqm-postgres", "sqm", "sqm")

BATCH = 25
PAUSE = 1.2
MAX_BYTES = 512 * 1024
MAX_EDGE = 600

VARIANTS = {"pro", "plus", "ultra", "max", "lite", "mini", "fe", "prime", "neo", "power"}
NOT_A_DEVICE = ("logo", "wordmark", "icon", "symbol", "chart", "map", "graph")


# ----------------------------------------------------------------- plumbing

def say(text):
    """Print without dying on a console that cannot encode the text.

    Commons file names are frequently not Latin-1 - the Redmi Note 13's lead image is named in
    Chinese - and a Windows console defaulting to cp1252 raises UnicodeEncodeError on them. A
    bulk import must not stop halfway because of how a terminal is configured.
    """
    encoding = getattr(sys.stdout, "encoding", None) or "utf-8"
    print(text.encode(encoding, errors="replace").decode(encoding, errors="replace"))


def curl_json(url):
    for attempt in range(4):
        out = subprocess.run(["curl", "-sS", "-m", "30", "-A", UA, url],
                             capture_output=True, text=True, encoding="utf-8")
        body = (out.stdout or "").strip()
        if body.startswith("{"):
            return json.loads(body)
        time.sleep(2 + attempt * 3)          # rate limited: back off, never record a false miss
    raise RuntimeError("no JSON after retries")


def curl_bytes(url):
    out = subprocess.run(["curl", "-sSL", "-m", "60", "-A", UA, url], capture_output=True)
    if out.returncode != 0 or not out.stdout:
        raise RuntimeError("download failed")
    return out.stdout


def clickhouse(sql):
    url, user, password = CH
    out = subprocess.run(["curl", "-sS", "-u", f"{user}:{password}", url, "--data-binary", sql],
                         capture_output=True, text=True, encoding="utf-8", check=True)
    return out.stdout


def psql(sql, quiet=True):
    """Run SQL, passing it on STDIN rather than as an argument.

    An insert here carries a base64 image, and a command line is not the place for half a
    megabyte of it: Windows caps a command at 32 KB, so the first large photograph would fail
    with an error about the argument list rather than about the image.
    """
    container, user, db = PG
    args = ["docker", "exec", "-i", container, "psql", "-U", user, "-d", db, "-v", "ON_ERROR_STOP=1"]
    if quiet:
        args += ["-t", "-A"]
    out = subprocess.run(args, input=sql, capture_output=True, text=True, encoding="utf-8")
    if out.returncode != 0:
        raise RuntimeError(out.stderr.strip())
    return out.stdout.strip()


# ----------------------------------------------------------------- matching

def tokens(text):
    return [t for t in re.split(r"[^a-z0-9]+", text.lower()) if t]


def clean(brand, model):
    name = re.sub(r"\s*\([^)]*\)", "", model)
    # "DS" is a dual-SIM suffix and drops out. The Nokia type code does NOT: for models
    # named only "TA-1557" it is the entire identity, and removing it left the bare word
    # "Nokia", which resolves to the company article and its photograph of the headquarters.
    name = re.sub(r"\bDS\b", "", name)
    name = re.sub(r"\b5g\b", "5G", name, flags=re.I)
    name = re.sub(r"\b4g\b", "4G", name, flags=re.I)
    name = re.sub(r"\s+", " ", name).strip()

    b = (brand or "").strip()
    if "iphone" in name.lower():
        name = name[name.lower().index("iphone"):]
    elif b and not name.lower().startswith(b.lower()):
        name = f"{b} {name}"
    if name.lower().startswith("galaxy "):
        name = "Samsung " + name
    return name


def file_depicts(model_title, filename, brand=""):
    """True only when the file names THIS model and no richer variant of it."""
    if not filename:
        return False
    low = filename.lower()
    if any(b in low for b in NOT_A_DEVICE):
        return False

    want = tokens(model_title)
    got = set(tokens(filename))

    # The brand is not required in the FILE name - it is already established by the page the file
    # hangs on, and Commons often omits it: the Galaxy A12's photograph is
    # Galaxy_A12_front_and_Back.png, with no "Samsung" anywhere in it. Requiring it rejected the
    # single most populous model on the network. What must match is everything that distinguishes
    # one model from the next.
    brand_tokens = set(tokens(brand))
    distinguishing = [t for t in want if t not in brand_tokens]

    # Nothing left once the brand is removed means the title was only a brand name, and the
    # file would match on that alone. Four Nokia models were given a picture of Nokia's
    # headquarters this way: every textual guard passed, because "nokia" really was in the
    # file name. It was caught by looking at the images, which is why that step is not
    # optional.
    if not distinguishing:
        return False

    if not all(t in got for t in distinguishing):
        return False
    return not ((got & VARIANTS) - set(want))


# ----------------------------------------------------------------- sources

def lead_images(titles):
    """{asked title: (thumb url, commons file name)} for up to 25 titles in one request."""
    data = curl_json(WIKI + "?" + urllib.parse.urlencode({
        "action": "query", "prop": "pageimages", "piprop": "thumbnail|name",
        "pithumbsize": str(MAX_EDGE), "redirects": "1",
        "titles": "|".join(titles), "format": "json",
    }))
    query = data.get("query", {})

    resolved = {e["from"]: e["to"] for e in query.get("normalized", [])}
    for e in query.get("redirects", []):
        for asked, became in list(resolved.items()):
            if became == e["from"]:
                resolved[asked] = e["to"]
        resolved.setdefault(e["from"], e["to"])

    pages = {p.get("title"): p for p in query.get("pages", {}).values()}

    result = {}
    for asked in titles:
        page = pages.get(resolved.get(asked, asked)) or {}
        thumb = page.get("thumbnail") or {}
        result[asked] = (thumb.get("source"), page.get("pageimage"))
    return result


def licence_of(filename):
    """The Commons licence and author, so provenance is recorded rather than implied."""
    try:
        data = curl_json(COMMONS + "?" + urllib.parse.urlencode({
            "action": "query", "prop": "imageinfo", "iiprop": "extmetadata",
            "titles": f"File:{filename}", "format": "json",
        }))
        for page in data.get("query", {}).get("pages", {}).values():
            meta = (page.get("imageinfo") or [{}])[0].get("extmetadata", {})
            licence = (meta.get("LicenseShortName") or {}).get("value", "unknown licence")
            author = re.sub(r"<[^>]+>", "", (meta.get("Artist") or {}).get("value", "")).strip()
            return licence, (author or "unknown author")
    except Exception:                                    # noqa: BLE001 - provenance is best effort
        pass
    return "unknown licence", "unknown author"


def to_stored_image(raw):
    """Normalise to something the schema accepts: <=512 KB, <=600px, PNG or JPEG."""
    from PIL import Image

    im = Image.open(io.BytesIO(raw))
    im.thumbnail((MAX_EDGE, MAX_EDGE), Image.LANCZOS)

    # Transparency is worth keeping - many of these are cut-outs on a transparent background,
    # and flattening them onto white looks wrong on a dark theme.
    if im.mode in ("RGBA", "LA", "P"):
        im = im.convert("RGBA")
        buf = io.BytesIO()
        im.save(buf, format="PNG", optimize=True)
        if buf.tell() <= MAX_BYTES:
            return buf.getvalue(), "image/png"
        im = im.convert("RGB")

    im = im.convert("RGB")
    for quality in (88, 80, 70, 60, 50):
        buf = io.BytesIO()
        im.save(buf, format="JPEG", quality=quality, optimize=True)
        if buf.tell() <= MAX_BYTES:
            return buf.getvalue(), "image/jpeg"
    raise RuntimeError("cannot fit under 512 KB")


# ----------------------------------------------------------------- main

def models(limit):
    sql = f"""
        SELECT coalesce(nullIf(t.brandName,''), t.manufacturer) AS brand,
               t.marketingName AS model,
               sum(d.bindings) AS bindings
        FROM sqm.agg_device_model AS d
        INNER JOIN sqm.tac AS t ON t.tac = d.tac
        WHERE d.seq = (SELECT max(seq) FROM sqm.mart_ready) AND t.marketingName != ''
        GROUP BY brand, model ORDER BY bindings DESC LIMIT {int(limit)} FORMAT TSV
    """
    rows = []
    for line in clickhouse(sql).splitlines():
        if not line.strip():
            continue
        brand, model, bindings = line.split("\t")
        rows.append((brand, model, int(bindings)))
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=200)
    ap.add_argument("--user", default="admin", help="username recorded as the uploader")
    ap.add_argument("--apply", action="store_true", help="write to the database")
    args = ap.parse_args()

    user_id = psql(f"SELECT id FROM auth.user_account WHERE username = '{args.user}' LIMIT 1")
    if not user_id:
        sys.exit(f"no such user: {args.user}")

    existing = set(psql("SELECT model_key FROM catalog.device_model_image").splitlines())
    existing = {k for k in existing if k}

    wanted = models(args.limit)
    print(f"models considered:   {len(wanted)}")
    print(f"already have a photo: {len(existing)}\n")

    candidates = []
    for start in range(0, len(wanted), BATCH):
        chunk = wanted[start:start + BATCH]
        titles = [clean(b, m) for b, m, _ in chunk]
        try:
            found = lead_images(titles)
        except Exception as exc:                         # noqa: BLE001
            # One refused batch must not end the run. Losing 25 candidates is a coverage dent;
            # losing the remaining 275 because of a rate limit is a wasted hour - and the first
            # attempt at 300 models did exactly that, silently.
            print(f"  batch failed, skipping 25: {exc}", file=sys.stderr)
            found = {}
        for (brand, model, bindings), title in zip(chunk, titles):
            url, filename = found.get(title, (None, None))
            candidates.append((brand, model, bindings, title, url, filename))
        print(f"  probed {min(start + BATCH, len(wanted))}/{len(wanted)}", file=sys.stderr)
        time.sleep(PAUSE)

    stored = skipped_have = rejected = no_image = failed = 0
    stored_bindings = 0

    for brand, model, bindings, title, url, filename in candidates:
        key = f"{brand.strip().lower()}|{model.strip().lower()}"
        key = re.sub(r"\s+", " ", key)

        if key in existing:
            skipped_have += 1
            continue
        if not url:
            no_image += 1
            continue
        if not file_depicts(title, filename or "", brand):
            rejected += 1
            say(f"  REJECT {model}  <-  {filename}")
            continue

        try:
            raw = curl_bytes(url)
            data, content_type = to_stored_image(raw)
            licence, author = licence_of(filename)
        except Exception as exc:                          # noqa: BLE001 - report and continue
            failed += 1
            say(f"  FAIL   {model}: {exc}")
            continue

        note = (f"Wikimedia Commons: File:{filename}; {licence}; {author}; "
                f"https://commons.wikimedia.org/wiki/File:{urllib.parse.quote(filename)}")

        say(f"  {'store ' if args.apply else 'would '}{model:32s} {filename}")

        if args.apply:
            payload = base64.b64encode(data).decode("ascii")
            psql(
                "INSERT INTO catalog.device_model_image "
                "(model_key, brand, marketing_name, content_type, bytes, sha256, "
                " source_note, uploaded_by, uploaded_at, updated_at) VALUES ("
                f"'{key.replace(chr(39), chr(39) * 2)}', "
                f"'{brand.replace(chr(39), chr(39) * 2)}', "
                f"'{model.replace(chr(39), chr(39) * 2)}', "
                f"'{content_type}', decode('{payload}', 'base64'), "
                f"sha256(decode('{payload}', 'base64')), "
                f"'{note.replace(chr(39), chr(39) * 2)}', {user_id}, now(), now()) "
                "ON CONFLICT (model_key) DO NOTHING")

        stored += 1
        stored_bindings += bindings
        time.sleep(0.4)

    total_bindings = sum(b for _, _, b in wanted)
    print(f"\n{'stored' if args.apply else 'would store'}: {stored}")
    print(f"already had:        {skipped_have}")
    print(f"no image on Commons:{no_image}")
    print(f"refused by guards:  {rejected}")
    print(f"failed to fetch:    {failed}")
    print(f"bindings covered by new images: {stored_bindings:,} of {total_bindings:,} "
          f"({100.0 * stored_bindings / total_bindings:.1f}% of the models considered)")
    if not args.apply:
        print("\nnothing was written; re-run with --apply")


if __name__ == "__main__":
    main()
