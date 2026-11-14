"""Convert the Substance Painter exports in Textures/Finished into the overlays Cummy ships.

    python tools/build-assets.py

For each body part folder it writes Cummy/Assets/<partId>/:
  diffuse.png  RGBA straight from <Part>/<Part>.png (its alpha is the coverage)
  normal.png   RGB from <Part>/Bibo_Normal_DirectX.png, alpha copied from the diffuse alpha
  stages.png   staged parts only: 8-bit greyscale arrival field, built from the part's own final art. 0-240 = how
               early the pixel arrives (stage n shows it up to about 80n, fading out across that edge), 255 = no art.

The stages spread along the strokes: the field is the distance inside the art's coverage from seeds at its thickest
points (bigger splats start earlier), scaled so stage 1 and 2 cover STAGE_SHARES of the art. A cut therefore never
forms straight lines or boxes, and Cummy fades a stroke out where a stage edge crosses it (CompositeCache.LoadArt).
Roughness/Specular_level are constant in the export, so gloss is not shipped as art: the compositor writes fixed
gloss values under the diffuse coverage. Needs Pillow, numpy and scipy.
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import dijkstra

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "Textures" / "Finished"
TARGET = ROOT / "Cummy" / "Assets"

STAGES = 3
# Art counts as covered above this alpha (the exports have faint noise at 1-2).
COVERED = 2
# Share of a part's covered pixels fully shown at stages 1 and 2 (the stage 1/2 edges of the field).
STAGE_SHARES = (0.37, 0.68)
# The build fails if a part's measured shares fall outside these ranges.
SHARE_LIMITS = ((0.30, 0.45), (0.60, 0.75))
# Field values: stage n ends at STAGE_STEP * n; NO_ART marks pixels without art. Must match CompositeCache.
STAGE_STEP = 80
NO_ART = 255
# The field is computed at this size, then upsampled.
FIELD_SIZE = 1024
# Seeds: local thickness maxima (in a SEED_WINDOW square) at least SEED_PEAK of their splat's thickest point.
SEED_WINDOW = 9
SEED_PEAK = 0.8
# Bigger splats start earlier: each splat's distances are reduced by HEAD_START times its peak thickness.
HEAD_START = 3.0

# Folder name in Textures/Finished -> (wire id, staged). Ids must match Cummy's BodyPart ids.
# The arm and hand folders are named from the viewer's side: "Arm Left" is painted on the character's right arm, so
# it ships as Arm Right (and so on). The leg, leg-back and feet folders are named from the character's side.
PARTS = {
    "Arm Left": ("ar", True),
    "Arm Right": ("al", True),
    "Back": ("bk", True),
    "Butt": ("bt", True),
    "Chest": ("ch", True),
    "Groin": ("gr", True),
    "Leg Left": ("ll", True),
    "Leg Right": ("lr", True),
    "Stomach": ("st", True),
    "Hand Left": ("hr", True),
    "Hand Right": ("hl", True),
    "Feet Left": ("fl", True),
    "Feet Right": ("fr", True),
    "Leg Left Back": ("bl", True),
    "Right Leg Back": ("br", True),
}


# Character-side pairs (left id, right id). On the Bibo atlas the character's left side is the right half, so each left
# part's art must sit right of its right counterpart; a folder mapped to the wrong side fails the build.
SIDES = [("al", "ar"), ("hl", "hr"), ("ll", "lr"), ("bl", "br"), ("fl", "fr")]


def centre_x(part_id: str) -> float:
    alpha = Image.open(TARGET / part_id / "diffuse.png").getchannel("A").resize((256, 256))
    columns = [sum(1 for y in range(256) if alpha.getpixel((x, y)) > COVERED) for x in range(256)]
    return sum(x * n for x, n in enumerate(columns)) / max(1, sum(columns))


def check_sides() -> list[str]:
    return [f"{left} sits left of {right} on the atlas; their folders are swapped"
            for left, right in SIDES if centre_x(left) <= centre_x(right)]


def geodesic_field(mask: np.ndarray) -> np.ndarray:
    """Distance inside mask from its thickest points, less each splat's head start (inf outside the mask)."""
    h, w = mask.shape
    index = -np.ones(mask.shape, np.int64)
    ys, xs = np.nonzero(mask)
    index[ys, xs] = np.arange(len(ys))
    rows, cols, costs = [], [], []
    for dy, dx, cost in ((0, 1, 1.0), (1, 0, 1.0), (1, 1, 2 ** 0.5), (1, -1, 2 ** 0.5)):
        a = index[0:h - dy, max(0, -dx):w - max(0, dx)]
        b = index[dy:h, max(0, dx):w - max(0, -dx)]
        linked = (a >= 0) & (b >= 0)
        rows.append(a[linked])
        cols.append(b[linked])
        costs.append(np.full(linked.sum(), cost))
    graph = coo_matrix((np.concatenate(costs), (np.concatenate(rows), np.concatenate(cols))),
                       shape=(len(ys), len(ys))).tocsr()

    thickness = ndimage.distance_transform_edt(mask)
    splats, count = ndimage.label(mask, structure=np.ones((3, 3)))
    peak = np.zeros(count + 1)
    peak[1:] = ndimage.maximum(thickness, splats, range(1, count + 1))
    seeds = mask & (thickness >= SEED_PEAK * peak[splats]) \
        & (thickness == ndimage.maximum_filter(thickness, SEED_WINDOW))
    distance = dijkstra(graph, directed=False, indices=index[seeds], min_only=True)

    field = np.full(mask.shape, np.inf)
    field[ys, xs] = distance - HEAD_START * peak[splats[ys, xs]]
    return field


def build_stages(diffuse: Image.Image) -> Image.Image:
    """stages.png for a part: its arrival field at the diffuse's size (see the module docstring)."""
    alpha = np.asarray(diffuse.getchannel("A"))
    covered = alpha > COVERED
    small = np.asarray(diffuse.getchannel("A").resize((FIELD_SIZE, FIELD_SIZE), Image.Resampling.BILINEAR)) > COVERED
    field = geodesic_field(ndimage.binary_dilation(small))

    # Stage edges at the STAGE_SHARES quantiles of the art's own pixels, then piecewise-linear onto 0..3*STAGE_STEP.
    values = field[small & np.isfinite(field)]
    t1, t2 = np.quantile(values, STAGE_SHARES)
    scaled = np.interp(field, [values.min(), t1, t2, values.max()], [0, STAGE_STEP, 2 * STAGE_STEP, STAGES * STAGE_STEP])
    scaled[~np.isfinite(field)] = STAGES * STAGE_STEP
    full = Image.fromarray(scaled.astype(np.float32), "F").resize(diffuse.size, Image.Resampling.BILINEAR)

    stages = np.clip(np.rint(np.asarray(full)), 0, STAGES * STAGE_STEP).astype(np.uint8)
    stages[~covered] = NO_ART
    return Image.fromarray(stages, "L")


def stage_shares(stages: Image.Image) -> list[float]:
    """Share of the covered pixels fully shown at each of stages 1..STAGES-1."""
    values = np.asarray(stages)
    covered = values != NO_ART
    return [float((values[covered] <= STAGE_STEP * n).mean()) for n in range(1, STAGES)]


def build_part(folder: str, part_id: str, staged: bool) -> list[str]:
    """Writes the part's assets; returns stage-share errors."""
    src = SOURCE / folder
    out = TARGET / part_id
    out.mkdir(parents=True, exist_ok=True)

    diffuse = Image.open(src / f"{folder}.png").convert("RGBA")
    alpha = diffuse.getchannel("A")
    diffuse.save(out / "diffuse.png", optimize=True)

    normal = Image.open(src / "Bibo_Normal_DirectX.png").convert("RGB")
    if normal.size != diffuse.size:
        normal = normal.resize(diffuse.size, Image.Resampling.BILINEAR)
    normal.putalpha(alpha)
    normal.save(out / "normal.png", optimize=True)

    if not staged:
        (out / "stages.png").unlink(missing_ok=True)
        return []
    stages = build_stages(diffuse)
    stages.save(out / "stages.png", optimize=True)
    shares = stage_shares(stages)
    print(f"  stage shares {', '.join(f'{s:.3f}' for s in shares)}")
    return [f"{folder}: stage {n} shows {s:.3f} of the art, outside {lo}-{hi}"
            for n, (s, (lo, hi)) in enumerate(zip(shares, SHARE_LIMITS), 1) if not lo <= s <= hi]


def main() -> int:
    missing = [f for f in PARTS if not (SOURCE / f).is_dir()]
    if missing:
        print(f"missing part folders in {SOURCE}: {missing}", file=sys.stderr)
        return 1
    errors = []
    for folder, (part_id, staged) in PARTS.items():
        print(f"{folder} -> {part_id}{' (staged)' if staged else ''}")
        errors += build_part(folder, part_id, staged)
    errors += check_sides()
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
