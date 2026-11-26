"""Prepare the approved Mirage and agent images from a 4ill site checkout."""
from argparse import ArgumentParser
from pathlib import Path
from PIL import Image


def main():
    parser = ArgumentParser(description=__doc__)
    parser.add_argument("site_public", type=Path)
    args = parser.parse_args()
    source = args.site_public / "static" / "images"
    output = Path(__file__).parent / "materials" / "retake17"

    for key, relative_path, bounds in (
        ("mirage", "maps/de_mirage.webp", (640, 360)),
        ("ct", "skins/agents/ct_agent_default.webp", (270, 300)),
        ("t", "skins/agents/t_agent_default.png", (270, 300)),
    ):
        with Image.open(source / relative_path) as original:
            image = original.convert("RGBA")
            if key != "mirage":
                image = image.crop(image.getbbox())
            image.thumbnail(bounds)
            image.save(output / f"{key}.png")


if __name__ == "__main__":
    main()
