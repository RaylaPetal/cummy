"""Build the body-part region map Cummy uses to split a wearer's full-body overlay into parts.

Every pixel of the Bibo UV atlas belongs to exactly one regional part. The map is seeded from the shipped overlays
in Cummy/Assets (run tools/build-assets.py first), so each part's region sits where its art does:

    python tools/build-regions.py generate

  1. Per part, connected pieces of art smaller than --speck (fraction of the atlas) are dropped as stray specks.
  2. The remaining art grows outward until every pixel is claimed by its nearest part.
  3. Writes Textures/Regions/regions.generated.png (one flat colour per part, see PALETTE) and
     regions.preview.png (the map over a Bibo skin, --skin).

Areas with no art nearby come out wrong, so the generated map is corrected once by hand: paint
over it in an image editor using only the PALETTE colours (no anti-aliasing) and save it as
Textures/Regions/regions.png. Then:

    python tools/build-regions.py check

validates the corrected map (palette colours only; each part's shipped art, specks aside, inside its own region),
writes a fresh preview, and writes Cummy/Assets/regions.png: 8-bit greyscale, pixel value = Cummy's BodyPart
number. Rerun check whenever the shipped art changes. Needs Pillow.
"""
import argparse
import sys
from collections import deque
from pathlib import Path

from PIL import Image, ImageChops, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "Cummy" / "Assets"
REGIONS = ROOT / "Textures" / "Regions"
DEFAULT_SKIN = (ROOT / "Textures" / "Adobe Substance 3D Painter Resource Pack - Unisex Textures and Merged Bodies"
                / "Textures" / "Bibo Based (Female)" / "Base" / "Smooth" / "bibo_mid_base.png")

SIZE = 4096
# Speck detection and region growth run on a downscaled copy (full resolution takes minutes); the full-resolution
# art is pasted back over the grown map so every art pixel keeps its own part.
WORK = 1024

# Wire id -> (Cummy BodyPart value, palette colour). Never renumber: the values are Cummy's BodyPart enum.
PALETTE = {
    "al": (0, (255, 0, 0)),
    "ar": (1, (0, 200, 0)),
    "bk": (2, (0, 80, 255)),
    "bt": (3, (255, 255, 0)),
    "ch": (4, (255, 0, 255)),
    "gr": (5, (0, 255, 255)),
    "ll": (6, (255, 128, 0)),
    "lr": (7, (150, 60, 255)),
    "st": (8, (255, 255, 255)),
    "hl": (12, (128, 0, 0)),
    "hr": (13, (0, 100, 0)),
    "fl": (14, (0, 0, 140)),
    "fr": (15, (128, 128, 0)),
    "bl": (16, (255, 150, 200)),
    "br": (17, (0, 128, 128)),
}
NONE = 255


def art_mask(part_id: str) -> Image.Image:
    """The part's shipped art coverage at SIZE, 255 where alpha > 0."""
    alpha = Image.open(ASSETS / part_id / "diffuse.png").getchannel("A")
    if alpha.size != (SIZE, SIZE):
        alpha = alpha.resize((SIZE, SIZE), Image.Resampling.NEAREST)
    return alpha.point(lambda v: 255 if v else 0)


def drop_specks(mask: Image.Image, speck: float) -> Image.Image:
    """mask without its connected pieces (8-connected, measured at WORK) smaller than speck × atlas."""
    small = mask.resize((WORK, WORK), Image.Resampling.BOX).point(lambda v: 255 if v else 0)
    n = WORK
    px = small.tobytes()
    keep = bytearray(n * n)
    seen = bytearray(n * n)
    min_size = speck * n * n
    for start in range(n * n):
        if not px[start] or seen[start]:
            continue
        piece = [start]
        seen[start] = 1
        queue = deque(piece)
        while queue:
            p = queue.popleft()
            x, y = p % n, p // n
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    nx, ny = x + dx, y + dy
                    if 0 <= nx < n and 0 <= ny < n:
                        q = ny * n + nx
                        if px[q] and not seen[q]:
                            seen[q] = 1
                            piece.append(q)
                            queue.append(q)
        if len(piece) >= min_size:
            for p in piece:
                keep[p] = 255
    keep_img = Image.frombytes("L", (n, n), bytes(keep)).resize((SIZE, SIZE), Image.Resampling.NEAREST)
    return ImageChops.multiply(mask, keep_img)


def seeds(speck: float) -> dict[str, Image.Image]:
    return {pid: drop_specks(art_mask(pid), speck) for pid in PALETTE}


def generate(speck: float) -> Image.Image:
    """BodyPart value per pixel, grown from the seeds until nothing is unclaimed."""
    found = seeds(speck)
    labels = Image.new("L", (WORK, WORK), 0)   # BodyPart value + 1; 0 = unclaimed
    for pid, seed in found.items():
        small = seed.resize((WORK, WORK), Image.Resampling.BOX).point(lambda v: 255 if v else 0)
        labels.paste(PALETTE[pid][0] + 1, mask=small)
    grow = ImageFilter.MaxFilter(3)
    while labels.histogram()[0]:
        grown = labels.filter(grow)
        if grown.histogram()[0] == labels.histogram()[0]:
            raise RuntimeError("region growth stalled (no art left after speck removal?)")
        unclaimed = labels.point(lambda v: 255 if v == 0 else 0)
        labels = Image.composite(grown, labels, unclaimed)
    full = labels.resize((SIZE, SIZE), Image.Resampling.NEAREST)
    for pid, seed in found.items():
        full.paste(PALETTE[pid][0] + 1, mask=seed)
    return full.point(lambda v: v - 1)


def to_colour(values: Image.Image) -> Image.Image:
    lut = [0, 0, 0] * 256
    for value, colour in PALETTE.values():
        lut[value * 3:value * 3 + 3] = colour
    pal = values.copy()
    pal.putpalette(lut)
    return pal.convert("RGB")


def from_colour(img: Image.Image) -> Image.Image | None:
    """Colour map -> BodyPart values; None (with a report) if any pixel is not a palette colour."""
    img = img.convert("RGB")
    if img.size != (SIZE, SIZE):
        print(f"region map must be {SIZE}x{SIZE}, is {img.size[0]}x{img.size[1]}", file=sys.stderr)
        return None
    by_colour = {colour: value for value, colour in PALETTE.values()}
    stray = [(n, c) for n, c in img.getcolors(SIZE * SIZE) if c not in by_colour]
    if stray:
        stray.sort(reverse=True)
        print(f"{sum(n for n, _ in stray)} pixels are not palette colours (anti-aliasing?), e.g. "
              + ", ".join(f"{c} x{n}" for n, c in stray[:5]), file=sys.stderr)
        return None
    r, g, b = img.split()
    # Palette channels are distinct enough that a weighted sum identifies each colour uniquely.
    key = {(c[0] // 5 + c[1] // 3 + c[2] // 7) % 256: v for c, v in by_colour.items()}
    if len(key) != len(by_colour):
        raise RuntimeError("palette key collision")
    lut_r = [v // 5 for v in range(256)]
    lut_g = [v // 3 for v in range(256)]
    lut_b = [v // 7 for v in range(256)]
    summed = ImageChops.add(ImageChops.add(r.point(lut_r), g.point(lut_g)), b.point(lut_b))
    return summed.point([key.get(v, NONE) for v in range(256)])


def preview(values: Image.Image, skin: Path, out: Path) -> None:
    colour = to_colour(values)
    if skin.is_file():
        base = Image.open(skin).convert("RGB").resize((SIZE, SIZE))
        colour = Image.blend(base, colour, 0.45)
    else:
        print(f"skin {skin} not found; preview shows the map alone", file=sys.stderr)
    colour.resize((1024, 1024), Image.Resampling.NEAREST).save(out, optimize=True)


def check(values: Image.Image, speck: float) -> bool:
    """Each part's art (specks aside) inside its own region. Where two parts' art overlap, a pixel can only belong
    to one of them, so art also covered by another part is not counted against either."""
    found = seeds(speck)
    ok = True
    for pid, seed in found.items():
        others = Image.new("L", (SIZE, SIZE), 0)
        for other, mask in found.items():
            if other != pid:
                others = ImageChops.lighter(others, mask)
        region = values.point(lambda v, want=PALETTE[pid][0]: 255 if v == want else 0)
        outside = ImageChops.subtract(seed, region)
        shared = ImageChops.multiply(outside, others).histogram()[255]
        stray = outside.histogram()[255] - shared
        if stray:
            print(f"{pid}: {stray} px of its art lie outside its region", file=sys.stderr)
            ok = False
        elif shared:
            print(f"{pid}: {shared} px of its art overlap another part's art and went to that part (ok)")
    return ok


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("mode", choices=["generate", "check"])
    parser.add_argument("--speck", type=float, default=0.0002,
                        help="art pieces smaller than this fraction of the atlas are ignored (default 0.0002)")
    parser.add_argument("--skin", type=Path, default=DEFAULT_SKIN, help="Bibo diffuse for the preview")
    parser.add_argument("--map", type=Path, default=REGIONS / "regions.png", help="corrected map to check")
    args = parser.parse_args()

    missing = [pid for pid in PALETTE if not (ASSETS / pid / "diffuse.png").is_file()]
    if missing:
        print(f"missing shipped art for {missing}; run tools/build-assets.py first", file=sys.stderr)
        return 1
    REGIONS.mkdir(parents=True, exist_ok=True)

    if args.mode == "generate":
        values = generate(args.speck)
        to_colour(values).save(REGIONS / "regions.generated.png", optimize=True)
        preview(values, args.skin, REGIONS / "regions.preview.png")
        print(f"wrote {REGIONS / 'regions.generated.png'} and regions.preview.png; correct it by hand and save "
              f"as {REGIONS / 'regions.png'}, then run check")
        return 0

    values = from_colour(Image.open(args.map))
    if values is None:
        return 1
    preview(values, args.skin, REGIONS / "regions.preview.png")
    if not check(values, args.speck):
        print(f"{args.map} failed the check; see regions.preview.png", file=sys.stderr)
        return 1
    values.save(ASSETS / "regions.png", optimize=True)
    print(f"ok - wrote {ASSETS / 'regions.png'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
