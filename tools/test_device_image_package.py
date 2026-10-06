"""
Tests for the image package importer. No network, no database.

Run:  python tools/test_device_image_package.py

Same style as test_device_image_pipeline.py: plain checks and a main. Every image is synthesised
so each case is exactly the shape it claims, and every warning rule is tested on BOTH sides of its
threshold - a rule that fires everywhere passes a "does it fire" test just as well as a correct one.
"""

from __future__ import annotations

import hashlib
import io
import os
import re
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import import_device_image_package as pkg         # noqa: E402
import source_device_images as pipeline           # noqa: E402

from PIL import Image, ImageDraw                  # noqa: E402

PASSED: list[str] = []
FAILED: list[str] = []

EDGE = pkg.SUBJECT_EDGE                           # 752


def check(name: str, condition: bool, detail: str = "") -> None:
    (PASSED if condition else FAILED).append(f"{name}{' - ' + detail if detail else ''}")


# --------------------------------------------------------------------- synthetic images

def encode(image: Image.Image, fmt: str) -> bytes:
    buffer = io.BytesIO()
    image.save(buffer, format=fmt)
    return buffer.getvalue()


def on_white(width: int, height: int, box: tuple[int, int, int, int], fmt="PNG") -> bytes:
    """A dark rounded device occupying `box` (inclusive pixel coordinates) on white."""
    image = Image.new("RGB", (width, height), (255, 255, 255))
    radius = max(2, min(box[2] - box[0], box[3] - box[1]) // 10)
    ImageDraw.Draw(image).rounded_rectangle(box, radius=radius, fill=(30, 30, 34))
    return encode(image, fmt)


def output(data: bytes) -> Image.Image:
    with Image.open(io.BytesIO(data)) as out:
        out.load()
        return out.convert("RGBA")


def device_box(image: Image.Image) -> tuple[int, int, int, int]:
    """Where the dark device is in a normalised tile: flattened on white, darker than mid-grey."""
    flat = Image.new("RGB", image.size, (255, 255, 255))
    flat.paste(image, mask=image.getchannel("A"))
    return flat.convert("L").point(lambda v: 255 if v < 128 else 0).getbbox()


def margins(box) -> tuple[int, int, int, int]:
    """left, top, right, bottom margins of a bbox on the canvas."""
    return box[0], box[1], pkg.CANVAS - box[2], pkg.CANVAS - box[3]


# --------------------------------------------------------------------- normalisation

def test_small_tall_source() -> None:
    # 240x532, the size of the smallest package images (an Honor). Device 192x500 inside a margin.
    data, mime, scale = pkg.normalise_to_standard(on_white(240, 532, (24, 16, 215, 515)))
    tile = output(data)
    box = device_box(tile)
    width, height = box[2] - box[0], box[3] - box[1]
    left, top, right, bottom = margins(box)

    check("a small tall source becomes an 800x800 tile", tile.size == (800, 800), str(tile.size))
    check("its device is scaled UP to 752 px tall", abs(height - EDGE) <= 1,
          f"{width}x{height}, scale {scale:.3f}")
    check("and keeps its proportions", abs(width / height - 192 / 500) < 0.01, f"{width}x{height}")
    check("and is centred", abs(left - right) <= 1 and abs(top - bottom) <= 1,
          f"margins l{left} t{top} r{right} b{bottom}")
    check("the scale reported is the scale applied", abs(scale - EDGE / 500) < 1e-9, f"{scale}")
    check("the tile is WebP within the database ceiling",
          mime == "image/webp" and len(data) <= pipeline.MAX_STORED_BYTES, f"{mime} {len(data)}")


def test_wide_source() -> None:
    data, _, scale = pkg.normalise_to_standard(on_white(1600, 400, (80, 40, 1519, 359)))
    box = device_box(output(data))
    width, height = box[2] - box[0], box[3] - box[1]
    left, top, right, bottom = margins(box)
    check("a wide 1600x400 source is scaled DOWN to 752 px wide", abs(width - EDGE) <= 1,
          f"{width}x{height}, scale {scale:.3f}")
    check("a wide source is centred vertically too", abs(top - bottom) <= 1 and abs(left - right) <= 1,
          f"margins l{left} t{top} r{right} b{bottom}")
    check("a downscale reports a factor below 1", scale < 1, f"{scale:.3f}")


def test_jpeg_margin_trimmed() -> None:
    # Device 560x720 on a 1400x900 white JPEG. Untrimmed, the whole photograph would be fitted
    # and the device would come out ~387 px tall.
    source = on_white(1400, 900, (420, 90, 979, 809), fmt="JPEG")
    data, _, scale = pkg.normalise_to_standard(source)
    box = device_box(output(data))
    height = box[3] - box[1]
    check("a JPEG's white margin is trimmed before scaling", EDGE - 8 <= height <= EDGE,
          f"device {box[2] - box[0]}x{height}, scale {scale:.3f}")


def test_transparency_survives() -> None:
    image = Image.new("RGBA", (600, 900), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.rectangle((100, 50, 499, 749), fill=(30, 30, 34, 255))         # the body
    draw.rectangle((100, 760, 499, 849), fill=(90, 90, 90, 128))        # a soft shadow beneath
    data, _, _ = pkg.normalise_to_standard(encode(image, "PNG"))
    tile = output(data)
    alpha = tile.getchannel("A")

    check("the background stays transparent", alpha.getpixel((5, 5)) == 0
          and alpha.getpixel((400, 795)) == 0, f"{alpha.getpixel((5, 5))}")
    check("the device stays opaque", alpha.getpixel((400, 300)) == 255, f"{alpha.getpixel((400, 300))}")

    # Shadow rows sit at source y 760-849 of a 50-849 subject: 752 px tall, 24 px margin.
    y = 24 + round((805 - 50) * EDGE / 800)
    shadow = alpha.getpixel((400, y))
    check("a half-transparent shadow keeps its opacity (not squared)", 118 <= shadow <= 138,
          f"alpha {shadow} at y={y}, want ~128")

    opaque = pkg.normalise_to_standard(on_white(600, 900, (100, 50, 499, 749), fmt="JPEG"))[0]
    check("an opaque source still gets a transparent canvas",
          output(opaque).getchannel("A").getpixel((2, 2)) == 0)


# --------------------------------------------------------------------- warnings

def image_result(width=1200, height=1200, upscale=1.0, edges=0, coverage=0.5, clean=1.0,
                 sha="a" * 64, expected="a" * 64) -> pkg.ImageResult:
    return pkg.ImageResult(
        image_file="images/x/y.png", width=width, height=height, upscale=upscale,
        facts={"width": width, "height": height, "edges_touched": edges, "coverage": coverage,
               "cleanliness": clean, "colours": 300, "has_alpha": True},
        stored=b"x", mime="image/webp", digest="b" * 64, original_bytes=1000,
        actual_sha=sha, expected_sha=expected)


def codes(found: list[dict]) -> dict[str, str]:
    return {w["code"]: w["severity"] for w in found}


def test_image_warning_boundaries() -> None:
    clean = pkg.image_warnings(image_result())
    check("a clean, sharp, full-frame image has no pixel warnings", clean == [], str(clean))

    w = codes(pkg.image_warnings(image_result(width=399, height=1200)))
    check("low_resolution fires below 400 px on an edge", w.get("low_resolution") == "info", str(w))
    w = codes(pkg.image_warnings(image_result(width=400, height=1200)))
    check("low_resolution is quiet at 400 px", "low_resolution" not in w, str(w))
    w = codes(pkg.image_warnings(image_result(upscale=1.5)))
    check("low_resolution is quiet at exactly 1.5x", "low_resolution" not in w, str(w))
    w = codes(pkg.image_warnings(image_result(upscale=1.51)))
    check("low_resolution fires past 1.5x, as info", w.get("low_resolution") == "info", str(w))
    w = codes(pkg.image_warnings(image_result(upscale=1.99)))
    check("low_resolution is still info just under 2x", w.get("low_resolution") == "info", str(w))
    w = codes(pkg.image_warnings(image_result(upscale=2.0)))
    check("low_resolution is high at 2x", w.get("low_resolution") == "high", str(w))

    w = codes(pkg.image_warnings(image_result(edges=1)))
    check("touches_edges is quiet at one edge", "touches_edges" not in w, str(w))
    found = pkg.image_warnings(image_result(edges=2))
    w = codes(found)
    detail = next((x["detail"] for x in found if x["code"] == "touches_edges"), "")
    check("touches_edges fires at two edges, as info", w.get("touches_edges") == "info", str(w))
    check("and says it may only be a tight trim",
          "trimmed tight to the device, or cropped - check the whole device is in frame" in detail,
          detail)

    w = codes(pkg.image_warnings(image_result(coverage=0.18)))
    check("small_subject is quiet at 18%", "small_subject" not in w, str(w))
    w = codes(pkg.image_warnings(image_result(coverage=0.179)))
    check("small_subject fires below 18%", w.get("small_subject") == "info", str(w))

    w = codes(pkg.image_warnings(image_result(clean=0.55)))
    check("background is quiet at 55% clean", "background" not in w, str(w))
    w = codes(pkg.image_warnings(image_result(clean=0.549)))
    check("background fires below 55% clean", w.get("background") == "info", str(w))

    w = codes(pkg.image_warnings(image_result(width=400, height=1600)))
    check("aspect is quiet at 0.25", "aspect" not in w, str(w))
    w = codes(pkg.image_warnings(image_result(width=399, height=1600)))
    check("aspect fires below 0.25", w.get("aspect") == "info", str(w))
    w = codes(pkg.image_warnings(image_result(width=1200, height=400)))
    check("aspect is quiet at 3.0", "aspect" not in w, str(w))
    w = codes(pkg.image_warnings(image_result(width=1201, height=400)))
    check("aspect fires above 3.0", w.get("aspect") == "info", str(w))

    w = codes(pkg.image_warnings(image_result(sha="c" * 64)))
    check("checksum_mismatch fires, high, when the file is not the one listed",
          w.get("checksum_mismatch") == "high", str(w))
    w = codes(pkg.image_warnings(image_result(expected="")))
    check("checksum_mismatch fires when no checksum is listed", "checksum_mismatch" in w, str(w))


def product(name="Galaxy A14", kind="manufacturer", pid="samsung__a14", image="images/s/a.png"):
    return pkg.Product(product_id=pid, brand="samsung", product_name=name, image_file=image,
                       sha256="a" * 64, source_page="https://www.samsungmobilepress.com/x",
                       source_kind=kind, image_url="https://cdn.example/x.png",
                       identity_source="https://github.com/x", match_scope="product_family_representative",
                       qa_status="visual_and_file_checks_passed")


def planned(name="Galaxy A14", product_name="Galaxy A14", mapped=10, total=10, mapped_b=100,
            total_b=100, images=1, methods=("official_model_code",), kind="manufacturer"):
    model = pkg.Model(key=f"samsung|{name.lower()}", brand="Samsung", marketing_name=name,
                      manufacturer="Samsung Korea", model_codes=("SM-A145F",),
                      tacs=tuple(f"{i:08d}" for i in range(total)), bindings=total_b)
    return pkg.PlannedCandidate(model=model, image_file="images/s/a.png",
                                product=product(product_name, kind), match_methods=methods,
                                mapped_tacs=mapped, model_tacs=total, mapped_bindings=mapped_b,
                                model_bindings=total_b, images_for_model=images)


def test_mapping_warning_boundaries() -> None:
    w = codes(pkg.mapping_warnings(planned(), None))
    check("a model fully covered by one image, nothing live, has no mapping warnings", w == {}, str(w))

    w = codes(pkg.mapping_warnings(planned(mapped=9, total=10, mapped_b=50, total_b=100), None))
    check("partial_coverage fires on 9 of 10 TACs, info when they hold exactly half the bindings",
          w.get("partial_coverage") == "info", str(w))
    w = codes(pkg.mapping_warnings(planned(mapped=9, total=10, mapped_b=49, total_b=100), None))
    check("partial_coverage is high when the mapped TACs hold under half the bindings",
          w.get("partial_coverage") == "high", str(w))
    w = codes(pkg.mapping_warnings(planned(mapped=1, total=10, mapped_b=0, total_b=0), None))
    check("partial_coverage stays info for a model with no bindings at all",
          w.get("partial_coverage") == "info", str(w))

    found = pkg.mapping_warnings(planned(mapped=1, total=200, mapped_b=0, total_b=1645449), None)
    detail = next((x["detail"] for x in found if x["code"] == "partial_coverage"), "")
    check("partial_coverage says how much of the model the image covers",
          detail == "the package maps this image to 1 of 200 TACs of the model - "
                    "0 of 1,645,449 active bindings", detail)

    w = codes(pkg.mapping_warnings(planned(product_name="Galaxy A14 5G"), None))
    check("different_variant fires, high, for a '5G' image on the non-5G model",
          w.get("different_variant") == "high", str(w))
    w = codes(pkg.mapping_warnings(planned(product_name="Samsung Galaxy A14"), None))
    check("different_variant is quiet when the product name is the model", "different_variant" not in w,
          str(w))
    w = codes(pkg.mapping_warnings(planned(product_name="Galaxy A15"), None))
    check("different_variant is only about variants, not other identity failures",
          "different_variant" not in w, str(w))

    w = codes(pkg.mapping_warnings(planned(images=2), None))
    check("several_images fires when the model has two package images", w.get("several_images") == "info",
          str(w))

    w = codes(pkg.mapping_warnings(planned(), "verified"))
    check("replaces_verified fires, high, over a verified live image",
          w.get("replaces_verified") == "high" and "replaces_unreviewed" not in w, str(w))
    w = codes(pkg.mapping_warnings(planned(), "needs_review"))
    check("replaces_unreviewed fires, info, over an unreviewed live image",
          w.get("replaces_unreviewed") == "info" and "replaces_verified" not in w, str(w))

    combined = pkg.candidate_warnings(planned(mapped=1, total=10, mapped_b=0, total_b=10, images=2),
                                      image_result(edges=3), None)
    check("high warnings are listed first",
          [x["severity"] for x in combined] == sorted((x["severity"] for x in combined),
                                                      key=lambda s: s != "high"),
          str([(x["code"], x["severity"]) for x in combined]))
    check("every warning has a code, a known severity and a detail",
          all(set(x) == {"code", "severity", "detail"} and x["severity"] in ("high", "info")
              and x["detail"] for x in combined), str(combined))


# --------------------------------------------------------------------- planning

def tac(t, brand, name, maker="Samsung Korea", code="SM-A145F"):
    return pkg.TacRow(tac=t, brand=brand, marketing_name=name, manufacturer=maker, model_code=code)


def ptac(t, image, pid="samsung__a14", method="official_model_code"):
    return pkg.PackageTac(tac=t, image_file=image, product_id=pid, match_method=method)


def test_grouping_by_model_key() -> None:
    ours = [
        tac("00000001", "Samsung", "Galaxy A14"),
        tac("00000002", "SAMSUNG ", "Galaxy  A14", code="SM-A145F/DS"),
        tac("00000003", "Samsung Korea", "Galaxy A14"),            # another word: another model
        tac("00000004", "  ", "Galaxy A14", maker="Samsung"),      # blank brand -> manufacturer
        tac("00000005", "Samsung", "Galaxy A14 5G", code="SM-A146B"),
        tac("00000006", "Samsung", "Galaxy A14"),                  # the package never maps it
    ]
    bindings = {"00000001": 100, "00000002": 50, "00000003": 7, "00000004": 0,
                "00000005": 10, "00000006": 300}
    products = {"samsung__a14": product(), "samsung__a145g": product("Galaxy A14 5G",
                                                                       pid="samsung__a145g",
                                                                       image="images/s/b.png")}
    package = [
        ptac("00000001", "images/s/a.png"),
        ptac("00000002", "images/s/a.png", method="exact_product_name"),
        ptac("00000003", "images/s/a.png"),
        ptac("00000005", "images/s/b.png", pid="samsung__a145g"),
        ptac("99999999", "images/s/a.png"),                        # not in our GSMA version
    ]
    result = pkg.plan(package, products, ours, bindings)
    by_key = {c.model.key: c for c in result.candidates}

    check("spellings that differ only in case and spacing are one model",
          "samsung|galaxy a14" in by_key and by_key["samsung|galaxy a14"].mapped_tacs == 2,
          str({k: c.mapped_tacs for k, c in by_key.items()}))
    check("a different brand word is a different model",
          set(by_key) == {"samsung|galaxy a14", "samsung korea|galaxy a14", "samsung|galaxy a14 5g"},
          str(sorted(by_key)))

    a14 = by_key["samsung|galaxy a14"]
    check("a blank brand falls back to the manufacturer and joins the model",
          "00000004" in a14.model.tacs and a14.model_tacs == 4, str(a14.model.tacs))
    check("the model's bindings are all of its TACs', mapped or not",
          a14.model_bindings == 450 and a14.mapped_bindings == 150,
          f"{a14.mapped_bindings} of {a14.model_bindings}")
    check("the display spelling is the one carrying the most bindings",
          (a14.model.brand, a14.model.marketing_name) == ("Samsung", "Galaxy A14"),
          f"{a14.model.brand!r} {a14.model.marketing_name!r}")
    check("match methods are gathered from every mapped TAC",
          a14.match_methods == ("exact_product_name", "official_model_code"), str(a14.match_methods))
    check("model codes come from our TAC records",
          set(a14.model.model_codes) == {"SM-A145F", "SM-A145F/DS"}, str(a14.model.model_codes))
    check("a package TAC absent from our GSMA version is counted, not staged",
          result.stats["packageTacsNotInOurGsma"] == 1, str(result.stats))

    w = codes(pkg.mapping_warnings(a14, None))
    check("the merged model is flagged as partly covered, high (150 of 450 bindings)",
          w.get("partial_coverage") == "high", str(w))

    check("candidates are ordered by the model's bindings",
          [c.model_bindings for c in result.candidates]
          == sorted((c.model_bindings for c in result.candidates), reverse=True),
          str([c.model_bindings for c in result.candidates]))


def test_two_images_two_candidates() -> None:
    ours = [tac("00000011", "Nokia", "6300", maker="HMD"), tac("00000012", "Nokia", "6300", maker="HMD")]
    products = {"nokia__6300": product("Nokia 6300", pid="nokia__6300", image="images/n/a.png"),
                "nokia__63004g": product("Nokia 6300 4G", pid="nokia__63004g", image="images/n/b.png")}
    package = [ptac("00000011", "images/n/a.png", pid="nokia__6300"),
               ptac("00000012", "images/n/b.png", pid="nokia__63004g")]
    result = pkg.plan(package, products, ours, {"00000011": 5, "00000012": 5})

    check("a model with two package images yields two candidates",
          len(result.candidates) == 2 and {c.image_file for c in result.candidates}
          == {"images/n/a.png", "images/n/b.png"}, str([c.image_file for c in result.candidates]))
    flags = [codes(pkg.mapping_warnings(c, None)).get("several_images") for c in result.candidates]
    check("and several_images is on both", flags == ["info", "info"], str(flags))
    names = {c.image_file: c.product.product_name for c in result.candidates}
    check("each candidate names the product its own TACs were matched to",
          names == {"images/n/a.png": "Nokia 6300", "images/n/b.png": "Nokia 6300 4G"}, str(names))


# --------------------------------------------------------------------- score, rows, writing

def test_score_is_advice() -> None:
    small = image_result(width=240, height=532, upscale=1.5, coverage=0.9, edges=4, clean=0.1)
    total, breakdown = pkg.score(planned(), small)
    terms = {t["term"]: t["points"] for t in breakdown}
    check("a thumbnail-sized, edge-touching image is scored, never rejected",
          total > 0 and terms.get("resolution") == 0, str(terms))
    check("a model-code match is worth 30",
          terms.get("exactModelMatch") == 30, str(terms))
    _, breakdown = pkg.score(planned(methods=("exact_product_name",)), small)
    check("a name match is worth 20",
          {t["term"]: t["points"] for t in breakdown}.get("exactModelMatch") == 20)
    trust = {kind: {t["term"]: t["points"] for t in pkg.score(planned(kind=kind), small)[1]}["sourceTrust"]
             for kind in ("manufacturer", "retailer", "museum_archive", "public_image_archive", "blog")}
    check("source trust follows the source kind",
          trust == {"manufacturer": 25, "retailer": 15, "museum_archive": 12,
                    "public_image_archive": 7, "blog": 5}, str(trust))
    _, breakdown = pkg.score(planned(), image_result(width=1000, height=1400))
    check("resolution is full marks from 1000 px",
          {t["term"]: t["points"] for t in breakdown}["resolution"] == 15)
    check("every score term carries its reasoning", all(t["detail"] for t in breakdown))


def test_process_image_and_checksum() -> None:
    with tempfile.TemporaryDirectory() as root:
        os.makedirs(os.path.join(root, "images", "x"))
        data = on_white(700, 1000, (100, 60, 599, 939))
        with open(os.path.join(root, "images", "x", "p.png"), "wb") as fh:
            fh.write(data)
        with open(os.path.join(root, "images", "x", "bad.png"), "wb") as fh:
            fh.write(b"<!DOCTYPE html><title>404</title>")
        right = hashlib.sha256(data).hexdigest()

        good = pkg.process_image(root, "images/x/p.png", right)
        check("a package image is read, measured and normalised",
              good.error is None and good.mime == "image/webp" and good.stored
              and good.digest == hashlib.sha256(good.stored).hexdigest()
              and (good.width, good.height) == (700, 1000) and good.original_bytes == len(data),
              good.error or f"{good.width}x{good.height}")
        check("a matching checksum is recognised", good.sha_ok and
              "checksum_mismatch" not in codes(pkg.image_warnings(good)))

        wrong = pkg.process_image(root, "images/x/p.png", "0" * 64)
        check("a checksum mismatch is a high warning and the image is still staged",
              wrong.error is None and wrong.stored
              and codes(pkg.image_warnings(wrong)).get("checksum_mismatch") == "high")

        broken = pkg.process_image(root, "images/x/bad.png", "0" * 64)
        check("a file that is not an image is reported, not raised",
              broken.error is not None and not broken.stored, broken.error or "")
        missing = pkg.process_image(root, "images/x/nope.png", "0" * 64)
        check("a missing file is reported, not raised", missing.error is not None, missing.error or "")

        row = pkg.build_row(planned(), good, None, "TAC_Catalog_Images_TEST")
        expected_keys = {"package", "productName", "productId", "matchMethods", "mappedTacs",
                         "modelTacs", "mappedBindings", "modelBindings", "sourceKind", "sourcePage",
                         "imageUrl", "identitySource", "matchScope", "qaStatus", "packageFile",
                         "upscale", "originalSha256Ok"}
        check("evidence carries the agreed camelCase keys", set(row.evidence) == expected_keys,
              str(sorted(set(row.evidence) ^ expected_keys)))
        check("evidence records the scale applied and the checksum verdict",
              row.evidence["upscale"] == round(good.upscale, 4)
              and row.evidence["originalSha256Ok"] is True, str(row.evidence))

        sql = pkg.insert_sql([row])
        check("the insert targets the candidate table only",
              re.findall(r"INSERT INTO ([\w.]+)", sql) == ["catalog.device_image_candidate"], sql[:120])
        check("the insert skips what is already staged and returns ids",
              "ON CONFLICT (model_key, sha256) DO NOTHING" in sql and "RETURNING id" in sql)
        check("the insert stages for review, with the package's provenance",
              "'needs_review'" in sql and "'manufacturer'" in sql
              and "'www.samsungmobilepress.com'" in sql and "'TAC_Catalog_Images_TEST'" in sql)


def test_returned_ids() -> None:
    check("psql's RETURNING output is parsed without counting the command tag",
          pkg.returned_ids("BEGIN\n7\n8\nINSERT 0 2\nCOMMIT") == [7, 8])
    check("ids skipped by ON CONFLICT are not counted", pkg.returned_ids("7\nINSERT 0 1") == [7])
    try:
        pkg.returned_ids("7\nINSERT 0 2")
        check("a tag that disagrees with the ids is an error", False, "no error raised")
    except RuntimeError:
        check("a tag that disagrees with the ids is an error", True)


def test_no_path_to_the_live_table() -> None:
    with open(pkg.__file__, encoding="utf-8") as fh:
        source = fh.read()
    writes = re.findall(r"(?is)\b(insert\s+into|update|delete\s+from|truncate|merge\s+into)\s+"
                        r"(?:table\s+)?catalog\.device_model_image\b", source)
    check("the importer has no statement that writes catalog.device_model_image", writes == [],
          str(writes))
    targets = set(re.findall(r"(?i)insert\s+into\s+([\w.]+)", source))
    check("the only table it inserts into is the candidate table",
          targets == {"catalog.device_image_candidate"}, str(targets))


def test_model_key_and_tsv() -> None:
    check("the model key is DeviceModelKey's",
          pkg.model_key_of(" SAMSUNG ", "x", "Galaxy   A32") == "samsung|galaxy a32")
    check("and the same as the sourcing tool's",
          pkg.model_key_of("Samsung", "x", "Galaxy A32")
          == pipeline.DeviceIdentity("x", "Samsung", "Galaxy A32", (), "", 0).model_key)
    check("a blank brand falls back to the manufacturer",
          pkg.model_key_of("   ", "Nokia", "6300") == "nokia|6300")
    check("no marketing name, no model", pkg.model_key_of("Nokia", "Nokia", "  ") is None)
    check("ClickHouse TSV escapes are undone",
          pkg.tsv_field(r"a\tb\\c\nd") == "a\tb\\c\nd", repr(pkg.tsv_field(r"a\tb\\c\nd")))


def main() -> int:
    for test in (test_small_tall_source, test_wide_source, test_jpeg_margin_trimmed,
                 test_transparency_survives, test_image_warning_boundaries,
                 test_mapping_warning_boundaries, test_grouping_by_model_key,
                 test_two_images_two_candidates, test_score_is_advice,
                 test_process_image_and_checksum, test_returned_ids,
                 test_no_path_to_the_live_table, test_model_key_and_tsv):
        try:
            test()
        except Exception as exc:                         # noqa: BLE001
            FAILED.append(f"{test.__name__} raised {exc!r}")

    for line in PASSED:
        print(f"  PASS  {line}")
    for line in FAILED:
        print(f"  FAIL  {line}")

    print(f"\n{len(PASSED)} passed, {len(FAILED)} failed")
    return 1 if FAILED else 0


if __name__ == "__main__":
    sys.exit(main())
