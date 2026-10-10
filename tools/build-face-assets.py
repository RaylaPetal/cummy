"""Convert [HLF] Cum Blush (by HighLowFlow) face-paint textures into the facial overlays Cummy ships.

    python tools/build-face-assets.py "<Penumbra mod folder>/[HLF] Cum Blush"

The mod replaces face paint 4 (chara/common/texture/decal_face/_decal_4.tex) with one variant per option. Those are
uncompressed B8G8R8A8 in the game's face-paint (decal) layout: alpha is 255 everywhere and the coverage is the
colour itself (black = nothing). For the Forehead and Mouth variants this writes Cummy/Assets/<partId>/:
  diffuse.png  RGBA matched to the body overlays' look (semi-transparent goo, not paint): the decal's brightness
               becomes the coverage, curved and scaled to the body art's opacity (peak ~55%), and the colour is the
               body art's warm off-white, a little brighter in the thick cores
  normal.png   flat (127, 127, 255) with the diffuse alpha, like the body overlays: the wet look comes from the
               translucency and the compositor's gloss, not from painted relief
plus Cummy/Assets/face-regions.png, which splits a wearer's full-face image into the facial parts: every face-paint
pixel belongs to exactly one part (8-bit greyscale, pixel = Cummy's BodyPart number), grown outward from each part's
art; and Textures/Regions/face-preview.png, both parts over grey with their regions tinted, for a quick look.

Cummy maps these onto each wearer's own face at composite time. Do not ship them without the author's permission.
Needs Pillow.
"""
import struct
import sys
from pathlib import Path

from PIL import Image, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
TARGET = ROOT / "Cummy" / "Assets"
PREVIEW = ROOT / "Textures" / "Regions" / "face-preview.png"
DECAL = Path("chara") / "common" / "texture" / "decal_face" / "_decal_4.tex"

# Mod option folder -> (Cummy part id, BodyPart value).
PARTS = {"forehead": ("fh", 10), "mouth": ("mo", 11)}

B8G8R8A8 = 0x1450

# Measured from the shipped body overlays (Cummy/Assets/<body part>/diffuse.png): alpha-weighted colour and the
# highest alpha. Facial goo should read as the same substance.
BODY_TINT = (221, 208, 204)
BODY_PEAK_ALPHA = 139
# > 1 thins the faint edges and mid-tones so the goo stays translucent; the body art averages ~36 alpha.
OPACITY_CURVE = 1.6
# How much brighter the thickest goo is than the thinnest (0 = flat tint).
CORE_LIFT = 0.15


def load_tex(path: Path) -> Image.Image:
    data = path.read_bytes()
    fmt, width, height = struct.unpack_from("<IHH", data, 4)
    if fmt != B8G8R8A8:
        raise ValueError(f"{path}: format {fmt:#x}, expected B8G8R8A8 ({B8G8R8A8:#x})")
    offset = struct.unpack_from("<I", data, 28)[0]
    return Image.frombytes("RGBA", (width, height), data[offset:offset + width * height * 4], "raw", "BGRA")


def goo(img: Image.Image) -> Image.Image:
    """Decal (brightness = coverage) -> semi-transparent goo matching the body overlays."""
    src = img.convert("RGB").tobytes()
    out = bytearray(len(src) // 3 * 4)
    for i in range(len(src) // 3):
        r, g, b = src[i * 3:i * 3 + 3]
        level = max(r, g, b) / 255
        if not level:
            continue
        alpha = round(BODY_PEAK_ALPHA * level ** OPACITY_CURVE)
        if not alpha:
            continue
        lift = 1 - CORE_LIFT + CORE_LIFT * level
        out[i * 4:i * 4 + 4] = bytes(min(255, round(c * lift + 255 * (1 - lift) * level)) for c in BODY_TINT) + bytes((alpha,))
    return Image.frombytes("RGBA", img.size, bytes(out))


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
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 1
    mod = Path(sys.argv[1])
    sheet = Image.new("RGBA", (2048, 1024), (90, 90, 90, 255))
    coverage = {}
    for i, (option, (part_id, value)) in enumerate(PARTS.items()):
        source = mod / "facial type" / option / DECAL
        if not source.is_file():
            print(f"missing {source}", file=sys.stderr)
            return 1
        diffuse = goo(load_tex(source))
        out = TARGET / part_id
        out.mkdir(parents=True, exist_ok=True)
        diffuse.save(out / "diffuse.png", optimize=True)
        flat_normal(diffuse.getchannel("A")).save(out / "normal.png", optimize=True)
        coverage[value] = diffuse.getchannel("A")
        tile = Image.new("RGBA", diffuse.size, (90, 90, 90, 255))
        tile.alpha_composite(diffuse)
        sheet.paste(tile, (i * 1024, 0))
        print(f"{option} -> {part_id}")
    labels = regions(coverage)
    labels.save(TARGET / "face-regions.png", optimize=True)
    tint = Image.new("RGBA", labels.size, (0, 0, 0, 0))
    tint.paste((60, 140, 255, 60), mask=labels.point(lambda v: 255 if v == 10 else 0))
    tint.paste((255, 140, 60, 60), mask=labels.point(lambda v: 255 if v == 11 else 0))
    sheet.alpha_composite(tint, (0, 0))
    sheet.alpha_composite(tint, (1024, 0))
    print(f"regions: {TARGET / 'face-regions.png'}")
    PREVIEW.parent.mkdir(parents=True, exist_ok=True)
    sheet.convert("RGB").resize((1024, 512)).save(PREVIEW)
    print(f"preview: {PREVIEW}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
