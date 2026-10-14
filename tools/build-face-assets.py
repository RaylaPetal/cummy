"""Convert the staged face-paint art in Textures/Finished/Face into the Face overlay Cummy ships.

    python tools/build-face-assets.py import "<Coomface v1.0>/options (face paint 9)"
    python tools/build-face-assets.py

The art is Coomface v1.0's face paint 9, whose three options nest: Chin, Chin+Forehead and Face are Cummy's Face
stages 1, 2 and 3. `import` decodes the options' `_decal_9.tex` (DXT1/DXT3) to Textures/Finished/Face/Face - Stage N.png
(Textures/ is not tracked, so this records where they came from). The build then reads those PNGs: game face-paint
(decal) layout, no alpha, black background, the brightness is both the coverage and the shading.

It writes Cummy/Assets/mo/ (Face keeps Mouth's wire id, which is also its asset folder):
  diffuse.png  RGBA at OUT_SIZE, from stage 3. Alpha ramps with brightness from a thin film at the faint edges to about
               THICK_ALPHA in the thick strands, so skin shows through the thin goo. The colour keeps the source's
               shading (darker in the creases, bright on the ridges, a faintly warm white), as the body art's colour
               does: dividing the brightness out of the colour, as this script once did, turns every pixel the same
               white and the layer reads as flat paint.
  normal.png   relief from the brightness taken as height, with the diffuse alpha, so the game's lighting and the
               compositor's gloss catch the strands
  stages.png   8-bit greyscale, pixel = the first stage (1-3) that covers it, 0 = no art. Cummy shows stage N as the
               diffuse where stages.png is 1..N, as for the staged body parts (tools/build-assets.py).
plus Textures/Regions/face-preview.png, the three stages over grey, for a quick look.

Cummy maps these onto each wearer's own face at composite time. Needs Pillow.
"""
import io
import struct
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "Textures" / "Finished" / "Face"
TARGET = ROOT / "Cummy" / "Assets" / "mo"
PREVIEW = ROOT / "Textures" / "Regions" / "face-preview.png"

STAGES = 3
# Coomface option folder -> stage, for `import`.
IMPORT_OPTIONS = {"chin": 1, "chin+forehead": 2, "face": 3}
IMPORT_TEX = Path("chara") / "common" / "texture" / "decal_face" / "_decal_9.tex"

OUT_SIZE = 1024
# Brightness at or below this is the export's background noise (a faint frame), not art.
NOISE = 12
# Stage n's coverage may stick out of stage n+1's by at most this many pixels at OUT_SIZE (the Coomface options differ
# by a few anti-aliasing specks: about 31 px at 256 px). Those pixels are dropped from stage n.
MAX_STRAY = 1200

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

# .tex format -> (DDS FourCC, bytes per 4x4 block).
TEX_FORMATS = {0x3420: (b"DXT1", 8), 0x3430: (b"DXT3", 16), 0x3431: (b"DXT5", 16)}
TEX_HEADER = 80


def stage_path(n: int) -> Path:
    return SOURCE / f"Face - Stage {n}.png"


def decode_tex(path: Path) -> Image.Image:
    """The first mip of a block-compressed game .tex, through a DDS header so Pillow can decode it."""
    data = path.read_bytes()
    fmt, w, h = struct.unpack_from("<IHH", data, 4)
    if fmt not in TEX_FORMATS:
        raise ValueError(f"{path}: unsupported .tex format {fmt:#x}")
    fourcc, block = TEX_FORMATS[fmt]
    size = max(1, w // 4) * max(1, h // 4) * block
    header = struct.pack("<4sIIIIIII44sIIIIIIIIIIIII", b"DDS ", 124, 0x81007, h, w, size, 0, 1, b"\0" * 44,
                         32, 4, struct.unpack("<I", fourcc)[0], 0, 0, 0, 0, 0, 0x1000, 0, 0, 0, 0)
    return Image.open(io.BytesIO(header + data[TEX_HEADER:TEX_HEADER + size])).convert("RGB")


def import_options(options: Path) -> int:
    SOURCE.mkdir(parents=True, exist_ok=True)
    for folder, n in IMPORT_OPTIONS.items():
        tex = options / folder / IMPORT_TEX
        if not tex.is_file():
            print(f"missing {tex}", file=sys.stderr)
            return 1
        img = decode_tex(tex)
        img.save(stage_path(n))
        covered = brightness(img).point(lambda v: 255 if v > NOISE else 0).histogram()[255]
        print(f"{folder} -> {stage_path(n).name}: {img.size[0]}x{img.size[1]}, {covered} px of art")
    return 0


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
    is exactly the source's art."""
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


def stage_map(levels: list[Image.Image], diffuse_alpha: Image.Image) -> Image.Image:
    """stages.png: the first stage covering each pixel. Art covered by no stage's coverage (the last stage's own
    edges after resampling) is the last stage; stage n's specks outside stage n+1 are dropped from stage n."""
    masks = [level.point(lambda v: 255 if v > NOISE else 0) for level in levels]
    for n in range(STAGES - 1, 0, -1):            # innermost last: each mask clipped to the (clipped) next one
        stray = ImageChops.subtract(masks[n - 1], masks[n]).histogram()[255]
        if stray > MAX_STRAY:
            raise ValueError(f"stage {n} has {stray} px outside stage {n + 1}; stages must nest")
        print(f"stage {n}: {stray} px outside stage {n + 1} dropped")
        masks[n - 1] = ImageChops.darker(masks[n - 1], masks[n])
    stages = diffuse_alpha.point(lambda v: STAGES if v else 0)
    for n in range(STAGES - 1, 0, -1):
        stages.paste(n, mask=ImageChops.darker(masks[n - 1], diffuse_alpha.point(lambda v: 255 if v else 0)))
    return stages


def build() -> int:
    missing = [p for p in (*(stage_path(n) for n in range(1, STAGES + 1)), BODY_REFERENCE) if not p.is_file()]
    if missing:
        for p in missing:
            print(f"missing {p}", file=sys.stderr)
        print("run `import` first for the stage PNGs", file=sys.stderr)
        return 1
    body = Image.open(BODY_REFERENCE).convert("RGBA")

    levels = [brightness(Image.open(stage_path(n)).convert("RGB").resize((OUT_SIZE, OUT_SIZE), Image.Resampling.LANCZOS))
              for n in range(1, STAGES + 1)]
    diffuse = diffuse_from(levels[-1], body)
    normal = relief_normal(levels[-1], diffuse.getchannel("A"))
    stages = stage_map(levels, diffuse.getchannel("A"))

    TARGET.mkdir(parents=True, exist_ok=True)
    diffuse.save(TARGET / "diffuse.png", optimize=True)
    normal.save(TARGET / "normal.png", optimize=True)
    stages.save(TARGET / "stages.png", optimize=True)
    print(f"face: alpha {alpha_stats(diffuse.getchannel('A'))}")
    print(f"body reference: alpha {alpha_stats(body.getchannel('A'))}")

    sheet = Image.new("RGBA", (OUT_SIZE * STAGES, OUT_SIZE), (90, 90, 90, 255))
    for n in range(1, STAGES + 1):
        shown = diffuse.copy()
        shown.putalpha(ImageChops.multiply(diffuse.getchannel("A"),
                                           stages.point(lambda v, n=n: 255 if 0 < v <= n else 0)))
        covered = stages.point(lambda v, n=n: 255 if 0 < v <= n else 0).histogram()[255]
        print(f"stage {n}: {covered} px")
        tile = Image.new("RGBA", diffuse.size, (90, 90, 90, 255))
        tile.alpha_composite(shown)
        sheet.paste(tile, ((n - 1) * OUT_SIZE, 0))
    PREVIEW.parent.mkdir(parents=True, exist_ok=True)
    sheet.convert("RGB").resize((OUT_SIZE * STAGES // 2, OUT_SIZE // 2)).save(PREVIEW)
    print(f"wrote {TARGET}; preview: {PREVIEW}")
    return 0


def main() -> int:
    if len(sys.argv) == 3 and sys.argv[1] == "import":
        return import_options(Path(sys.argv[2]))
    if len(sys.argv) == 1:
        return build()
    print(__doc__, file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
