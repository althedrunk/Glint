import argparse
from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "Glint" / "Assets"
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]


def render(source, size):
    margin = max(1, round(size * 0.04))
    mark = source.copy()
    mark.thumbnail((size - margin * 2, size - margin * 2), Image.Resampling.LANCZOS)
    icon = Image.new("RGBA", (size, size))
    icon.alpha_composite(mark, ((size - mark.width) // 2, (size - mark.height) // 2))
    return icon


def main():
    parser = argparse.ArgumentParser(description="Export Windows icons from the approved Glint artwork.")
    parser.add_argument("--source", type=Path, default=ROOT / "docs" / "branding" / "glint-icon.png")
    parser.add_argument("--preview", action="store_true")
    args = parser.parse_args()

    with Image.open(args.source) as image:
        source = image.convert("RGBA")
    bounds = source.getchannel("A").point(lambda a: 255 if a >= 128 else 0).getbbox()
    if not bounds:
        raise ValueError("The source artwork is empty")
    source = source.crop(bounds)
    OUT.mkdir(parents=True, exist_ok=True)
    images = [render(source, size) for size in SIZES]
    images[-1].save(OUT / "Glint.ico", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    render(source, 512).save(OUT / "Logo512.png")
    render(source, 64).save(OUT / "Logo64.png")

    if args.preview:
        sheet = Image.new("RGBA", (900, 360), (17, 16, 21, 255))
        draw = ImageDraw.Draw(sheet)
        for row, background in enumerate(((17, 16, 21, 255), (245, 245, 245, 255))):
            y = row * 180
            draw.rectangle((0, y, 900, y + 179), fill=background)
            sheet.alpha_composite(render(source, 160), (12, y + 10))
            x = 210
            for size in (64, 48, 32, 24, 20, 16):
                sheet.alpha_composite(render(source, size), (x, y + (180 - size) // 2))
                x += size + 35
        sheet.convert("RGB").save(ROOT / "logo-preview.png")


if __name__ == "__main__":
    main()
