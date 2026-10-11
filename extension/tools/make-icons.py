#!/usr/bin/env python3
"""Regenerates extension/icons/icon{16,32,48,128}.png.

Not referenced by the manifest and not shipped logic — run it only when the mark
changes:  python3 extension/tools/make-icons.py   (needs Pillow)

The mark is a bold black "W" on a phosphor-green tile (the app's #00d700), drawn from
strokes instead of a font so it stays crisp and identical at 16 px.
"""
import os
from PIL import Image, ImageDraw

GREEN = (0, 215, 0, 255)      # --tr-green
INK = (10, 10, 10, 255)       # --tr-bg
SIZES = (16, 32, 48, 128)
SUPER = 16                    # supersampling factor for smooth edges


def draw(size):
    s = size * SUPER
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Tile: nearly full-bleed rounded square (full-bleed reads best at 16 px).
    margin = max(1, round(s * 0.03))
    d.rounded_rectangle((margin, margin, s - margin, s - margin),
                        radius=round(s * 0.2), fill=GREEN)

    # "W" as one thick polyline on a 100-unit grid.
    def p(x, y):
        return (x / 100 * s, y / 100 * s)

    pts = [p(19, 27), p(34, 73), p(50, 40), p(66, 73), p(81, 27)]
    width = round(s * 0.14)
    d.line(pts, fill=INK, width=width, joint="curve")
    r = width / 2
    for x, y in (pts[0], pts[-1]):  # round the stroke ends
        d.ellipse((x - r, y - r, x + r, y + r), fill=INK)

    return img.resize((size, size), Image.LANCZOS)


def main():
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "icons")
    os.makedirs(out, exist_ok=True)
    for size in SIZES:
        path = os.path.join(out, f"icon{size}.png")
        draw(size).save(path, optimize=True)
        print("wrote", os.path.normpath(path))


if __name__ == "__main__":
    main()
