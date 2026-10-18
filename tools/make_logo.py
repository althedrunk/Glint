"""Draws the Glint icon: an angular chat bubble with a play button cut into it.

    python tools/make_logo.py            # writes src/Glint/Assets
    python tools/make_logo.py --preview  # also writes logo-preview.png with the other variants
"""
import os
import sys
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "Glint", "Assets")

PURPLE = (145, 70, 255)
WHITE = (255, 255, 255)
INK = (24, 24, 27)


def rounded_plate(draw, s, color, radius=0.22):
    draw.rounded_rectangle((0, 0, s - 1, s - 1), radius=int(s * radius), fill=color)


def bubble(s, x0, y0, x1, y1, chamfer, tail_x, tail_w, tail_h):
    """Square-ish speech bubble: chamfered top-left and bottom-right corners, stepped tail bottom-left."""
    c = chamfer * s
    X0, Y0, X1, Y1 = x0 * s, y0 * s, x1 * s, y1 * s
    tx, tw, th = tail_x * s, tail_w * s, tail_h * s
    return [
        (X0 + c, Y0), (X1, Y0), (X1, Y1 - c), (X1 - c, Y1),
        (tx + tw, Y1), (tx, Y1 + th), (tx, Y1),
        (X0, Y1), (X0, Y0 + c),
    ]


def play(s, cx, cy, size):
    h = size * s
    return [(cx * s - h * 0.42, cy * s - h / 2), (cx * s - h * 0.42, cy * s + h / 2), (cx * s + h * 0.5, cy * s)]


def render(size, variant="a"):
    ss = 8 if size <= 64 else 4
    S = size * ss
    im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    small = size <= 24

    if variant == "a":  # purple plate, white bubble, purple play
        rounded_plate(d, S, PURPLE)
        d.polygon(bubble(S, 0.19, 0.20, 0.81, 0.66, 0.10, 0.27, 0.14, 0.13), fill=WHITE)
        d.polygon(play(S, 0.515, 0.43, 0.24 if small else 0.21), fill=PURPLE)
    elif variant == "b":  # dark plate, purple bubble, white play
        rounded_plate(d, S, INK)
        d.polygon(bubble(S, 0.17, 0.19, 0.83, 0.67, 0.10, 0.26, 0.15, 0.14), fill=PURPLE)
        d.polygon(play(S, 0.515, 0.43, 0.22), fill=WHITE)
    elif variant == "c":  # bubble silhouette, no plate
        d.polygon(bubble(S, 0.04, 0.06, 0.96, 0.76, 0.15, 0.16, 0.22, 0.19), fill=PURPLE)
        d.polygon(play(S, 0.52, 0.43, 0.30), fill=WHITE)

    return im.resize((size, size), Image.LANCZOS)


def main():
    os.makedirs(OUT, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    images = [render(s) for s in sizes]
    images[-1].save(os.path.join(OUT, "Glint.ico"), sizes=[(s, s) for s in sizes], append_images=images[:-1])
    render(512).save(os.path.join(OUT, "Logo512.png"))
    render(64).save(os.path.join(OUT, "Logo64.png"))

    if "--preview" in sys.argv:
        sheet = Image.new("RGBA", (1000, 470), (14, 14, 16, 255))
        light = (243, 243, 243, 255)
        for row, v in enumerate("abc"):
            y = 20 + row * 150
            big = render(128, v)
            sheet.paste(big, (20, y), big)
            x = 180
            for s in (64, 48, 32, 24, 16):
                ic = render(s, v)
                sheet.paste(ic, (x, y + 64 - s // 2), ic)
                x += s + 24
            ImageDraw.Draw(sheet).rectangle((560, y, 980, y + 128), fill=light)
            x = 580
            for s in (64, 48, 32, 24, 16):
                ic = render(s, v)
                sheet.paste(ic, (x, y + 64 - s // 2), ic)
                x += s + 24
            ImageDraw.Draw(sheet).text((4, y + 2), v.upper(), fill=(160, 160, 170, 255))
        sheet.save(os.path.join(ROOT, "logo-preview.png"))


if __name__ == "__main__":
    main()
