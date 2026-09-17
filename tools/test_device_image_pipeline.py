"""
Tests for the device image sourcing pipeline. No network, no database.

Run:  python tools/test_device_image_pipeline.py

Plain asserts and a main, rather than pytest, because the repository has no Python test runner
and adding one to run twenty checks would be more infrastructure than the thing it tests.

Every image here is synthesised, so each case is exactly the shape it claims to be: a rejection
test that happened to pass for the wrong reason would be worse than no test.
"""

from __future__ import annotations

import io
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import source_device_images as pipeline           # noqa: E402

from PIL import Image, ImageDraw                  # noqa: E402

PASSED: list[str] = []
FAILED: list[str] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    (PASSED if condition else FAILED).append(f"{name}{' - ' + detail if detail else ''}")


def device(brand="Samsung", name="Galaxy A32", codes=("SM-A325F", "SM-A325F/DS"),
           manufacturer="Samsung Korea") -> pipeline.DeviceIdentity:
    return pipeline.DeviceIdentity(
        manufacturer=manufacturer, brand=brand, marketing_name=name,
        model_codes=tuple(codes), device_type="Smartphone", bindings=1000)


def candidate(title: str, domain="upload.wikimedia.org") -> pipeline.Candidate:
    return pipeline.Candidate(url=f"https://{domain}/x.jpg", domain=domain,
                              source_type="encyclopedic", title=title)


def render(width=1200, height=1200, *, product=(0.5, 0.75), background=(255, 255, 255),
           noise=False, fmt="PNG") -> bytes:
    """A synthetic product shot: a dark rounded body centred on a background."""
    image = Image.new("RGB", (width, height), background)
    draw = ImageDraw.Draw(image)

    if noise:
        for i in range(0, width, 7):
            draw.line([(i, 0), (i, height)], fill=((i * 13) % 255, (i * 7) % 255, (i * 3) % 255))

    pw, ph = int(width * product[0]), int(height * product[1])
    left, top = (width - pw) // 2, (height - ph) // 2
    draw.rounded_rectangle([left, top, left + pw, top + ph], radius=pw // 12, fill=(30, 30, 34))

    buffer = io.BytesIO()
    image.save(buffer, format=fmt)
    return buffer.getvalue()


# --------------------------------------------------------------------- identity

def test_identity() -> None:
    ok, note = pipeline.identity_verdict(device(), candidate("Samsung Galaxy A32 front.png"))
    check("exact model match is accepted", ok, note)

    ok, note = pipeline.identity_verdict(
        device(), candidate("Samsung Galaxy A32 SM-A325F front.png"))
    check("a model code in the file name is recognised as confirmation",
          ok and "code" in note, note)

    ok, note = pipeline.identity_verdict(
        device(name="Galaxy A32"), candidate("Samsung Galaxy A32 5G front.png"))
    check("variant mismatch is rejected (A32 must not take A32 5G)", not ok, note)

    ok, note = pipeline.identity_verdict(
        device(name="Galaxy A32 5G", codes=("SM-A326B",)),
        candidate("Samsung Galaxy A32 front.png"))
    check("the reverse variant mismatch is rejected too", not ok, note)

    ok, note = pipeline.identity_verdict(
        device(name="Redmi Note 11", brand="Redmi"),
        candidate("Redmi Note 11 Pro Star Blue.png"))
    check("a richer variant is rejected (Note 11 must not take Note 11 Pro)", not ok, note)

    ok, note = pipeline.identity_verdict(device(), candidate("Samsung Galaxy A52 front.png"))
    check("a different model is rejected", not ok, note)

    ok, note = pipeline.identity_verdict(
        device(brand="NOKIA", name="TA-1557", codes=("TA-1557",), manufacturer="HMD"),
        candidate("Nokia Headquarters Espoo.jpg"))
    check("a name that is only a brand cannot match anything", not ok, note)

    ok, note = pipeline.identity_verdict(device(), candidate(""))
    check("an unnamed file is rejected", not ok, note)


# --------------------------------------------------------------------- transport

def test_source_policy() -> None:
    try:
        pipeline.fetch("https://evil.example.com/phone.jpg")
        check("a domain outside the allowlist is refused", False, "no error raised")
    except pipeline.FetchError as exc:
        check("a domain outside the allowlist is refused", "allowlist" in str(exc), str(exc))

    for scheme in ("http://upload.wikimedia.org/x.jpg", "file:///etc/passwd",
                   "ftp://upload.wikimedia.org/x.jpg"):
        try:
            pipeline.fetch(scheme)
            check(f"{scheme.split(':')[0]} is refused", False, "no error raised")
        except pipeline.FetchError as exc:
            check(f"{scheme.split(':')[0]} is refused", "https" in str(exc), str(exc))

    try:
        pipeline.check_public_host("localhost")
        check("localhost is refused", False, "no error raised")
    except pipeline.FetchError as exc:
        check("localhost is refused", "non-public" in str(exc), str(exc))


def test_mime_sniffing() -> None:
    check("PNG is recognised from its magic bytes",
          pipeline.sniff(render(400, 400, fmt="PNG")) == "image/png")
    check("JPEG is recognised from its magic bytes",
          pipeline.sniff(render(400, 400, fmt="JPEG")) == "image/jpeg")
    check("an HTML error page is not mistaken for an image",
          pipeline.sniff(b"<!DOCTYPE html><title>429</title>") is None)
    check("random bytes are not an image", pipeline.sniff(b"\x00\x01\x02\x03nonsense") is None)
    check("an empty body is not an image", pipeline.sniff(b"") is None)


# --------------------------------------------------------------------- quality

def score_of(data: bytes, dev=None, title="Samsung Galaxy A32 front.png"):
    facts = pipeline.analyse(data)
    return pipeline.score_candidate(dev or device(), candidate(title), facts, "model name matched")


def test_quality_rules() -> None:
    good = score_of(render(1200, 1200, product=(0.55, 0.8)))
    check("a clean high-resolution render is accepted", good.rejected is None, good.rejected or "")
    check("and scores well", good.total >= 55, f"scored {good.total}")

    breakdown_terms = {t["term"] for t in good.breakdown}
    check("the score is explained term by term",
          {"exactModelMatch", "resolution", "cleanBackground", "productCoverage"} <= breakdown_terms,
          str(sorted(breakdown_terms)))

    thumb = score_of(render(200, 200))
    check("a thumbnail is rejected", thumb.rejected is not None and "thumbnail" in thumb.rejected,
          thumb.rejected or "")

    cropped = score_of(render(1200, 1200, product=(1.0, 1.0)))
    check("a device cropped to the edges is rejected",
          cropped.rejected is not None and "cropped" in cropped.rejected, cropped.rejected or "")

    # A busy image is caught, but by the EDGE rule rather than the background one: both use the
    # same "is this pixel background" test, so any image whose border is dirty also has content
    # reaching the border. The cleanliness threshold therefore rarely fires on its own - its real
    # work is as a graded score term, not as a rejection. Naming the test for what it proves
    # rather than for what it looks like it proves.
    busy = score_of(render(1200, 1200, product=(0.5, 0.7), noise=True))
    check("a photograph of a scene is rejected", busy.rejected is not None, busy.rejected or "")

    # The graded term, which is where cleanliness actually earns its place.
    clean = pipeline.analyse(render(1200, 1200, product=(0.5, 0.75)))
    dirty = pipeline.analyse(render(1200, 1200, product=(0.5, 0.75), noise=True))
    check("a clean backdrop measures cleaner than a busy one",
          clean["cleanliness"] > dirty["cleanliness"],
          f"{clean['cleanliness']:.2f} vs {dirty['cleanliness']:.2f}")

    tiny_subject = score_of(render(1200, 1200, product=(0.12, 0.12)))
    check("a product lost in the frame is rejected",
          tiny_subject.rejected is not None, tiny_subject.rejected or "")

    banner = score_of(render(1600, 400, product=(0.3, 0.7)))
    check("a banner-shaped image is rejected",
          banner.rejected is not None, banner.rejected or "")

    # Resolution must be rewarded, not merely tolerated.
    low = score_of(render(500, 500, product=(0.55, 0.8)))
    high = score_of(render(1400, 1400, product=(0.55, 0.8)))
    check("higher resolution scores higher", high.total > low.total,
          f"{high.total} vs {low.total}")


def test_bad_bytes() -> None:
    for name, data in (("truncated PNG", render(500, 500)[:120]),
                       ("not an image at all", b"this is plainly not an image"),
                       ("empty", b"")):
        try:
            pipeline.analyse(data)
            check(f"{name} is rejected by the decoder", False, "decoded without error")
        except Exception:                                # noqa: BLE001 - any failure is a rejection
            check(f"{name} is rejected by the decoder", True)


# --------------------------------------------------------------------- normalisation

def test_normalisation() -> None:
    data, mime = pipeline.normalise(render(1400, 900, product=(0.4, 0.8)))
    check("normalised output is WebP", mime == "image/webp", mime)
    check("normalised output fits the database ceiling",
          len(data) <= pipeline.MAX_STORED_BYTES, f"{len(data)} bytes")

    with Image.open(io.BytesIO(data)) as out:
        check("normalised onto a square canvas", out.size == (pipeline.CANVAS, pipeline.CANVAS),
              str(out.size))

        # Contain, never cover: the product must be whole and must not touch the edge.
        image = out.convert("RGBA")
        bbox = image.getchannel("A").getbbox()
        check("the product is inset, not bleeding off the canvas",
              bbox is not None and bbox[0] > 0 and bbox[1] > 0
              and bbox[2] < pipeline.CANVAS and bbox[3] < pipeline.CANVAS, str(bbox))

        width_share = (bbox[2] - bbox[0]) / pipeline.CANVAS
        check("the product fills most of the canvas without touching it",
              0.7 <= width_share <= 0.95, f"{width_share:.0%} wide")

    # Aspect ratio must survive: a 2:1 source must not come out square.
    tall = pipeline.normalise(render(600, 1200, product=(0.5, 0.9)))[0]
    with Image.open(io.BytesIO(tall)) as out:
        image = out.convert("RGBA")
        bbox = image.getchannel("A").getbbox()
        ratio = (bbox[2] - bbox[0]) / (bbox[3] - bbox[1])
        check("a tall product stays tall (no stretching)", ratio < 0.9, f"ratio {ratio:.2f}")


def test_model_key() -> None:
    check("the model key matches what the database expects",
          device().model_key == "samsung|galaxy a32", device().model_key)
    check("case and spacing do not make a different model",
          device(brand=" SAMSUNG ", name="Galaxy   A32").model_key == device().model_key)
    check("a variant is a different key",
          device(name="Galaxy A32 5G").model_key != device().model_key)


def main() -> int:
    for test in (test_identity, test_source_policy, test_mime_sniffing, test_quality_rules,
                 test_bad_bytes, test_normalisation, test_model_key):
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
