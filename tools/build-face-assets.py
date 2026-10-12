"""Convert the face-paint texture in Textures/Finished into the facial overlays Cummy ships.

    python tools/build-face-assets.py ["<face-paint png>"]

The source (default Textures/Finished/cum_decal_4 v1.1.png) is in the game's face-paint (decal) layout, as worn in
face paint 4: no alpha, black background, the brightness is both the coverage and the shading. Forehead art sits in
the upper half, mouth art in the lower half. This writes Cummy/Assets/<partId>/ for Forehead and Mouth:
  diffuse.png  RGBA at OUT_SIZE. Alpha ramps with brightness from a thin film at the faint edges to about
               ALPHA_MAX in the thick strands, so skin shows through the thin goo. The colour keeps the source's
               shading (darker in the creases, bright on the ridges, a faintly warm white), as the body art's colour
               does: dividing the brightness out of the colour, as this script once did, turns every pixel the same
               white and the layer reads as flat paint.
  normal.png   relief from the brightness taken as height, with the diffuse alpha, so the game's lighting and the
               compositor's gloss catch the strands
plus Cummy/Assets/face-regions.png, which splits a wearer's full-face image into the facial parts: every face-paint
pixel belongs to exactly one part (8-bit greyscale, pixel = Cummy's BodyPart number), grown outward from each part's
art; and Textures/Regions/face-preview.png, both parts over grey with their regions tinted, for a quick look.

Cummy maps these onto each wearer's own face at composite time. Needs Pillow.
"""
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "Textures" / "Finished" / "cum_decal_4 v1.1.png"
TARGET = ROOT / "Cummy" / "Assets"
PREVIEW = ROOT / "Textures" / "Regions" / "face-preview.png"

# Part id -> BodyPart value, top (forehead) to bottom (mouth) of the face-paint layout.
PARTS = {"fh": 10, "mo": 11}

OUT_SIZE = 1024
# Brightness at or below this is the export's background noise (a faint frame), not art.
NOISE = 12

# The shipped body art looks right in game, so the facial art copies its statistics: the source's brightness order is
# kept, but alpha and colour are remapped so their distributions match the body art's (quantile matching).
# The whole-body source art, the same pixels Cummy used to ship as Full Body's art.
BODY_REFERENCE = ROOT / "Textures" / "Finished" / "Full Body" / "Full Body.png"

# The thickest goo (above this quantile of the source's brightness) eases from the body-matched alpha up to
# THICK_ALPHA, for the few bright, nearly solid blobs; everything else stays a see-through film like the body's.
THICK_FROM = 0.92
THICK_ALPHA = 190

# Faintly warm white, multiplied into the body-matched tone.
TINT = (255, 250, 242)

# Relief: brightness blurred by HEIGHT_BLUR is the height; RELIEF scales the slope into the normal's red/green.
HEIGHT_BLUR = 2.5
RELIEF = 2.0


def smoothstep(t: float) -> float:
    t = min(1.0, max(0.0, t))
    return t * t * (3 - 2 * t)


def brightness(img: Image.Image) -> Image.Image:
    """The brightest channel per pixel: the source is colour over black, so this is how much art is there."""
    r, g, b = img.convert("RGB").split()
    return ImageChops.lighter(ImageChops.lighter(r, g), b)


def quantile_map(src_hist: list[int], dst_hist: list[int]) -> list[float]:
    """For each source value, its mid-rank quantile in <src_hist> looked up in <dst_hist>: a monotonic remap that
    gives the source the destination's distribution. Index 0 of either histogram is ignored (no art)."""
    src_total, dst_total = sum(src_hist[1:]), sum(dst_hist[1:])
    dst_cdf, seen = [], 0
    for v in range(256):
        seen += dst_hist[v] if v else 0
        dst_cdf.append(seen / dst_total)
    out, below = [0.0] * 256, 0
    for v in range(1, 256):
        p = (below + src_hist[v] / 2) / src_total
        below += src_hist[v]
        out[v] = next(d for d in range(1, 256) if dst_cdf[d] >= p)
    return out


def quantile_of(hist: list[int]) -> list[float]:
    """Mid-rank quantile (0..1) of every value in <hist>, ignoring index 0."""
    total, below, out = sum(hist[1:]), 0, [0.0] * 256
    for v in range(1, 256):
        out[v] = (below + hist[v] / 2) / total
        below += hist[v]
    return out


def diffuse_from(level: Image.Image, body: Image.Image) -> Image.Image:
    """RGBA art whose alpha and colour follow the shipped body art's distributions, in the order of <level>'s
    brightness (thick goo is the most opaque and brightest). Every pixel above NOISE gets some alpha, so coverage
    (and the region split built from it) is exactly the source's art."""
    covered = level.point(lambda v: 0 if v <= NOISE else v)
    src_hist = covered.histogram()
    body_alpha = body.getchannel("A")
    r, g, b, _ = body.split()
    body_tone = ImageChops.lighter(ImageChops.lighter(r, g), b)
    body_mask = body_alpha.point(lambda v: 255 if v else 0)

    alpha_of = quantile_map(src_hist, body_alpha.histogram())
    tone_of = quantile_map(src_hist, body_tone.histogram(mask=body_mask))
    rank = quantile_of(src_hist)

    def alpha(v: int) -> int:
        if v <= NOISE:
            return 0
        a = alpha_of[v]
        if rank[v] > THICK_FROM:
            a += (THICK_ALPHA - a) * smoothstep((rank[v] - THICK_FROM) / (1 - THICK_FROM))
        return max(1, round(a))

    shade = covered.point(lambda v: 0 if v <= NOISE else round(tone_of[v]))
    rgb = [shade.point(lambda v, c=c: v * c // 255) for c in TINT]
    return Image.merge("RGBA", (*rgb, covered.point(alpha)))


def relief_normal(level: Image.Image, alpha: Image.Image) -> Image.Image:
    """Tangent-space normal from <level> as height (Sobel slope, 127 = flat), carrying <alpha> as its coverage."""
    height = level.filter(ImageFilter.GaussianBlur(HEIGHT_BLUR))
    # Pillow divides a kernel's sum by `scale`; a Sobel sum is about 8 × the slope, so this gives 2 × RELIEF × slope
    # (the strength tuned against a lit preview).
    scale = 8 / RELIEF / 2
    nx = height.filter(ImageFilter.Kernel((3, 3), [1, 0, -1, 2, 0, -2, 1, 0, -1], scale=scale, offset=127))
    ny = height.filter(ImageFilter.Kernel((3, 3), [1, 2, 1, 0, 0, 0, -1, -2, -1], scale=scale, offset=127))
    return Image.merge("RGBA", (nx, ny, Image.new("L", level.size, 255), alpha))


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
        print(f"missing {BODY_REFERENCE}", file=sys.stderr)
        return 1
    body = Image.open(BODY_REFERENCE).convert("RGBA")

    art = Image.open(source).convert("RGB").resize((OUT_SIZE, OUT_SIZE), Image.Resampling.LANCZOS)
    level = brightness(art)
    diffuse = diffuse_from(level, body)
    normal = relief_normal(level, diffuse.getchannel("A"))
    cut = split_row(diffuse.getchannel("A"))
    halves = {"fh": (0, 0, OUT_SIZE, cut), "mo": (0, cut, OUT_SIZE, OUT_SIZE)}

    sheet = Image.new("RGBA", (OUT_SIZE * 2, OUT_SIZE), (90, 90, 90, 255))
    coverage = {}
    for i, (part_id, value) in enumerate(PARTS.items()):
        box = halves[part_id]
        part = Image.new("RGBA", diffuse.size, (0, 0, 0, 0))
        part.paste(diffuse.crop(box), box[:2])
        if not part.getchannel("A").getbbox():
            print(f"{part_id}: no art on its side of row {cut}", file=sys.stderr)
            return 1
        part_normal = Image.new("RGBA", normal.size, (127, 127, 255, 0))
        part_normal.paste(normal.crop(box), box[:2])
        out = TARGET / part_id
        out.mkdir(parents=True, exist_ok=True)
        part.save(out / "diffuse.png", optimize=True)
        part_normal.save(out / "normal.png", optimize=True)
        coverage[value] = part.getchannel("A")
        tile = Image.new("RGBA", part.size, (90, 90, 90, 255))
        tile.alpha_composite(part)
        sheet.paste(tile, (i * OUT_SIZE, 0))
        print(f"{source.name} -> {part_id}: alpha {alpha_stats(part.getchannel('A'))}")

    print(f"body reference: alpha {alpha_stats(body.getchannel('A'))}")

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
