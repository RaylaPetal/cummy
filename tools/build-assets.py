"""Convert the Substance Painter exports in Textures/Finished into the overlays Cummy ships.

    python tools/build-assets.py

For each body part folder it writes Cummy/Assets/<partId>/:
  diffuse.png  RGBA straight from Bibo_Base_color.png (its alpha is the coverage)
  normal.png   RGB from Bibo_Normal_DirectX.png, alpha copied from the diffuse alpha

Roughness/Specular_level are constant in the export, so gloss is not shipped as art: the compositor writes fixed
gloss values under the diffuse coverage. Needs Pillow.
"""
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "Textures" / "Finished"
TARGET = ROOT / "Cummy" / "Assets"

# Folder name in Textures/Finished -> wire id (must match Cummy's BodyPart ids).
PARTS = {
    "Arm Left": "al",
    "Arm Right": "ar",
    "Back": "bk",
    "Butt": "bt",
    "Chest": "ch",
    "Groin": "gr",
    "Leg Left": "ll",
    "Leg Right": "lr",
    "Stomach": "st",
    "Full Body": "fb",
}


def build_part(folder: str, part_id: str) -> None:
    src = SOURCE / folder
    out = TARGET / part_id
    out.mkdir(parents=True, exist_ok=True)

    diffuse = Image.open(src / "Bibo_Base_color.png").convert("RGBA")
    alpha = diffuse.getchannel("A")
    diffuse.save(out / "diffuse.png", optimize=True)

    normal = Image.open(src / "Bibo_Normal_DirectX.png").convert("RGB")
    if normal.size != diffuse.size:
        normal = normal.resize(diffuse.size, Image.Resampling.BILINEAR)
    normal.putalpha(alpha)
    normal.save(out / "normal.png", optimize=True)


def main() -> int:
    missing = [f for f in PARTS if not (SOURCE / f).is_dir()]
    if missing:
        print(f"missing part folders in {SOURCE}: {missing}", file=sys.stderr)
        return 1
    for folder, part_id in PARTS.items():
        build_part(folder, part_id)
        print(f"{folder} -> {part_id}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
