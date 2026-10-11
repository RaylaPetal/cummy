"""Convert the face-paint texture in Textures/Finished into the facial overlays Cummy ships.

    python tools/build-face-assets.py ["<face-paint png>"]

The source (default Textures/Finished/cum_decal_4 v1.1.png) is in the game's face-paint (decal) layout, as worn in
face paint 4: no alpha, black background, the colour itself is the coverage. Forehead art sits in the upper half,
mouth art in the lower half. This writes Cummy/Assets/<partId>/ for Forehead and Mouth:
  diffuse.png  RGBA at OUT_SIZE: the source read as colour premultiplied over black, so the brightest channel
               gives the coverage and the colour is divided back out; the coverage is then scaled so the strongest
               facial pixel is as opaque as the strongest pixel of the shipped body art (Full Body), keeping facial
               layers as translucent as body layers instead of solid white
  normal.png   flat (127, 127, 255) with the diffuse alpha, like the body overlays: the wet look comes from the
               translucency and the compositor's gloss, not from painted relief
plus Cummy/Assets/face-regions.png, which splits a wearer's full-face image into the facial parts: every face-paint
pixel belongs to exactly one part (8-bit greyscale, pixel = Cummy's BodyPart number), grown outward from each part's
art; and Textures/Regions/face-preview.png, both parts over grey with their regions tinted, for a quick look.

Cummy maps these onto each wearer's own face at composite time. Needs Pillow.
"""
import sys
from pathlib import Path

from PIL import Image, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "Textures" / "Finished" / "cum_decal_4 v1.1.png"
TARGET = ROOT / "Cummy" / "Assets"
PREVIEW = ROOT / "Textures" / "Regions" / "face-preview.png"

# Part id -> BodyPart value, top (forehead) to bottom (mouth) of the face-paint layout.
PARTS = {"fh": 10, "mo": 11}

OUT_SIZE = 1024
# Brightness at or below this is the export's background noise (a faint frame), not art.
NOISE = 12

# Shipped body art whose strongest alpha caps the facial art's (Full Body covers every body region).
BODY_REFERENCE = TARGET / "fb" / "diffuse.png"


def unpremultiply(img: Image.Image, max_alpha: int = 255) -> Image.Image:
    """Colour over black (brightness = coverage) -> RGBA with the colour divided back out and the coverage scaled so
    full brightness becomes <max_alpha>."""
    src = img.convert("RGB").tobytes()
    out = bytearray(len(src) // 3 * 4)
    for i in range(len(src) // 3):
        r, g, b = src[i * 3:i * 3 + 3]
        level = max(r, g, b)
        if level <= NOISE:
            continue
        alpha = max(1, level * max_alpha // 255)
        out[i * 4:i * 4 + 4] = bytes((r * 255 // level, g * 255 // level, b * 255 // level, alpha))
    return Image.frombytes("RGBA", img.size, bytes(out))


def alpha_stats(alpha: Image.Image) -> str:
    """Max and 50/90/99th percentile of the covered pixels' alpha."""
    hist = alpha.histogram()
    covered = sum(hist[1:])
    if not covered:
        return "no coverage"
    picks, seen = {}, 0
    for value in range(1, 256):
        seen += hist[value]
        for pct in (50, 90, 99):
            if pct not in picks and seen >= covered * pct / 100:
                picks[pct] = value
    top = max(v for v in range(256) if hist[v])
    return f"max {top}, p50 {picks[50]}, p90 {picks[90]}, p99 {picks[99]}"


def split_row(alpha: Image.Image) -> int:
    """The emptiest row in the middle third: the gap between the forehead and mouth art."""
    w, h = alpha.size
    px = alpha.load()
    rows = range(h // 3, 2 * h // 3)
    return min(rows, key=lambda y: (sum(1 for x in range(w) if px[x, y]), abs(y - h // 2)))


def flat_normal(alpha: Image.Image) -> Image.Image:
    """No added relief (the body overlays' normals are flat too); alpha marks the coverage for the compositor."""
    flat = Image.new("RGB", alpha.size, (127, 127, 255))
    flat.putalpha(alpha)
    return flat


def regions(coverage: dict[int, Image.Image]) -> Image.Image:
    """BodyPart value per pixel: each part's art, grown outward until every pixel is claimed."""
    size = next(iter(coverage.values())).size
    labels = Image.new("L", size, 0)                 # value + 1; 0 = unclaimed
    for value, alpha in coverage.items():
        labels.paste(value + 1, mask=alpha.point(lambda v: 255 if v else 0))
    grow = ImageFilter.MaxFilter(3)
    while labels.histogram()[0]:
        grown = labels.filter(grow)
        if grown.histogram()[0] == labels.histogram()[0]:
            raise RuntimeError("region growth stalled")
        labels = Image.composite(grown, labels, labels.point(lambda v: 255 if v == 0 else 0))
    for value, alpha in coverage.items():            # art keeps its own part even where two parts' art touch
        labels.paste(value + 1, mask=alpha.point(lambda v: 255 if v else 0))
    return labels.point(lambda v: v - 1)


def main() -> int:
    if len(sys.argv) > 2:
        print(__doc__, file=sys.stderr)
        return 1
    source = Path(sys.argv[1]) if len(sys.argv) == 2 else SOURCE
    if not source.is_file():
        print(f"missing {source}", file=sys.stderr)
        return 1

    if not BODY_REFERENCE.is_file():
        print(f"missing {BODY_REFERENCE} (run build-assets.py first)", file=sys.stderr)
        return 1
    body_alpha = Image.open(BODY_REFERENCE).getchannel("A")
    body_max = body_alpha.getextrema()[1]

    art = Image.open(source).convert("RGB").resize((OUT_SIZE, OUT_SIZE), Image.Resampling.LANCZOS)
    diffuse = unpremultiply(art, body_max)
    cut = split_row(diffuse.getchannel("A"))
    halves = {"fh": (0, 0, OUT_SIZE, cut), "mo": (0, cut, OUT_SIZE, OUT_SIZE)}

    sheet = Image.new("RGBA", (OUT_SIZE * 2, OUT_SIZE), (90, 90, 90, 255))
    coverage = {}
    for i, (part_id, value) in enumerate(PARTS.items()):
        part = Image.new("RGBA", diffuse.size, (0, 0, 0, 0))
        box = halves[part_id]
        part.paste(diffuse.crop(box), box[:2])
        if not part.getchannel("A").getbbox():
            print(f"{part_id}: no art on its side of row {cut}", file=sys.stderr)
            return 1
        out = TARGET / part_id
        out.mkdir(parents=True, exist_ok=True)
        part.save(out / "diffuse.png", optimize=True)
        flat_normal(part.getchannel("A")).save(out / "normal.png", optimize=True)
        coverage[value] = part.getchannel("A")
        tile = Image.new("RGBA", part.size, (90, 90, 90, 255))
        tile.alpha_composite(part)
        sheet.paste(tile, (i * OUT_SIZE, 0))
        print(f"{source.name} -> {part_id}: alpha {alpha_stats(part.getchannel('A'))}")

    print(f"body reference fb: alpha {alpha_stats(body_alpha)}")

    labels = regions(coverage)
    labels.save(TARGET / "face-regions.png", optimize=True)
    tint = Image.new("RGBA", labels.size, (0, 0, 0, 0))
    tint.paste((60, 140, 255, 60), mask=labels.point(lambda v: 255 if v == PARTS["fh"] else 0))
    tint.paste((255, 140, 60, 60), mask=labels.point(lambda v: 255 if v == PARTS["mo"] else 0))
    sheet.alpha_composite(tint, (0, 0))
    sheet.alpha_composite(tint, (OUT_SIZE, 0))
    print(f"regions: {TARGET / 'face-regions.png'} (split at row {cut})")
    PREVIEW.parent.mkdir(parents=True, exist_ok=True)
    sheet.convert("RGB").resize((OUT_SIZE, OUT_SIZE // 2)).save(PREVIEW)
    print(f"preview: {PREVIEW}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
