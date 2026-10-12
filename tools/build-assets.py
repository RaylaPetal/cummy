"""Convert the Substance Painter exports in Textures/Finished into the overlays Cummy ships.

    python tools/build-assets.py

For each body part folder it writes Cummy/Assets/<partId>/:
  diffuse.png  RGBA straight from <Part>/<Part>.png (its alpha is the coverage)
  normal.png   RGB from <Part>/Bibo_Normal_DirectX.png, alpha copied from the diffuse alpha
  stages.png   staged parts only: 8-bit greyscale, pixel = the first stage (1-3) that covers it, 0 = no art. Built
               from Staged Parts/<Part>/<Part> - Stage N.png; Cummy shows stage N as the art where stages.png is 1..N.

The staged exports nest (stage N is contained in stage N+1, with identical pixels), so one full-resolution image plus
the stage map reproduces every stage. Roughness/Specular_level are constant in the export, so gloss is not shipped as
art: the compositor writes fixed gloss values under the diffuse coverage. Needs Pillow.
"""
import sys
from pathlib import Path

from PIL import Image, ImageChops

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "Textures" / "Finished"
STAGED = SOURCE / "Staged Parts"
TARGET = ROOT / "Cummy" / "Assets"

STAGES = 3
# A stage pixel counts as covered above this alpha (the exports have faint noise at 1-2).
COVERED = 2
# Stage n's coverage may stick out of stage n+1's by at most this many pixels (stray anti-aliasing specks).
NEST_TOLERANCE = 64

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


def covered(alpha: Image.Image) -> Image.Image:
    return alpha.point(lambda v: 255 if v > COVERED else 0)


def build_stages(folder: str, diffuse: Image.Image) -> Image.Image:
    """stages.png for a part: the first stage covering each pixel, at the diffuse's size."""
    size = diffuse.size
    masks = []
    for n in range(1, STAGES + 1):
        alpha = Image.open(STAGED / folder / f"{folder} - Stage {n}.png").convert("RGBA").getchannel("A")
        masks.append(covered(alpha))
    for n in range(STAGES - 1):
        stray = ImageChops.subtract(masks[n], masks[n + 1]).histogram()[255]
        if stray > NEST_TOLERANCE:
            raise ValueError(f"{folder}: stage {n + 1} has {stray} px outside stage {n + 2}; stages must nest")

    # Nearest: the stage exports already hold their strokes' soft edges, so any widening would let a later stage's
    # art show early. Art covered by no stage export (the full-resolution image's own edges) is the last stage.
    stages = covered(diffuse.getchannel("A")).point(lambda v: STAGES if v else 0)
    for n in range(STAGES - 1, 0, -1):
        stages.paste(n, mask=masks[n - 1].resize(size, Image.Resampling.NEAREST))
    return stages


def build_part(folder: str, part_id: str, staged: bool) -> None:
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

    if staged:
        build_stages(folder, diffuse).save(out / "stages.png", optimize=True)
    else:
        (out / "stages.png").unlink(missing_ok=True)


def main() -> int:
    missing = [f for f in PARTS if not (SOURCE / f).is_dir()]
    missing += [f"Staged Parts/{f}" for f, (_, staged) in PARTS.items() if staged and not (STAGED / f).is_dir()]
    if missing:
        print(f"missing part folders in {SOURCE}: {missing}", file=sys.stderr)
        return 1
    for folder, (part_id, staged) in PARTS.items():
        build_part(folder, part_id, staged)
        print(f"{folder} -> {part_id}{' (staged)' if staged else ''}")
    if errors := check_sides():
        print("\n".join(errors), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
