#!/usr/bin/env python3
"""
workspace-bwfc: render the WireCopy launcher wordmark to a 1400x1400 PNG
suitable for use as podcast cover art (Apple Podcasts spec).

The wordmark glyphs come straight from LauncherRenderer.cs so the source
of truth stays in C# code — this script copies the same six rows and
the same two-tone pink coloring (rows 1,2,5,6 in #ff87d7, rows 3,4 in
#ff5fd7) over a phosphor-black background.

Output: assets/podcast-cover.png (committed; embed at runtime via
AudioMetadata.CoverArtPath and PodcastConfiguration.ImageUrl).
"""
import os
import sys

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError:
    sys.stderr.write("Install Pillow: pip install Pillow\n")
    sys.exit(1)

# These six rows match LauncherRenderer.Wordmark verbatim. Keep them in
# sync if the C# wordmark ever changes (the visual identity is owned by
# the launcher; this asset just mirrors it onto a square canvas).
WORDMARK_ROWS = [
    "      ██╗    ██╗ ██╗ ██████╗  ███████╗      ██████╗  ██████╗  ██████╗  ██╗   ██╗       ",
    "      ██║    ██║ ██║ ██╔══██╗ ██╔════╝     ██╔════╝ ██╔═══██╗ ██╔══██╗ ╚██╗ ██╔╝       ",
    "      ██║ █╗ ██║ ██║ ██████╔╝ █████╗       ██║      ██║   ██║ ██████╔╝  ╚████╔╝        ",
    "      ██║███╗██║ ██║ ██╔══██╗ ██╔══╝       ██║      ██║   ██║ ██╔═══╝    ╚██╔╝         ",
    "      ╚███╔███╔╝ ██║ ██║  ██║ ███████╗     ╚██████╗ ╚██████╔╝ ██║         ██║          ",
    "       ╚══╝╚══╝  ╚═╝ ╚═╝  ╚═╝ ╚══════╝      ╚═════╝  ╚═════╝  ╚═╝         ╚═╝          ",
]

# LauncherRenderer.WordmarkUsesDark — rows 3 and 4 (0-indexed 2 and 3)
# use the darker pink for the centre stripe.
WORDMARK_USES_DARK = [False, False, True, True, False, False]

OUTER_PINK = (0xFF, 0x87, 0xD7)  # ANSI 212, HeaderTitleFg
INNER_PINK = (0xFF, 0x5F, 0xD7)  # ANSI 206, CelebrationFg
BACKGROUND = (0, 0, 0)           # phosphor black

CANVAS_SIZE = 1400
FONT_PATH = "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf"
TAGLINE = "All copy, no nonsense."
TAGLINE_COLOR = (0x33, 0xCC, 0x33)  # phosphor green
SUBTAG = "wirecopy"
SUBTAG_COLOR = (0x66, 0x66, 0x66)


def measure_text(text: str, font: ImageFont.FreeTypeFont) -> tuple[int, int]:
    """Return (width, height) of the rendered text."""
    bbox = font.getbbox(text)
    return bbox[2] - bbox[0], bbox[3] - bbox[1]


def fit_font_to_width(text: str, target_width: int, font_path: str) -> ImageFont.FreeTypeFont:
    """Binary-search the largest font size that keeps `text` within target_width."""
    lo, hi = 4, 200
    best = ImageFont.truetype(font_path, lo)
    while lo <= hi:
        mid = (lo + hi) // 2
        font = ImageFont.truetype(font_path, mid)
        w, _ = measure_text(text, font)
        if w <= target_width:
            best = font
            lo = mid + 1
        else:
            hi = mid - 1
    return best


def render(out_path: str):
    img = Image.new("RGB", (CANVAS_SIZE, CANVAS_SIZE), BACKGROUND)
    draw = ImageDraw.Draw(img)

    # Fill ~88% of the canvas width so the wordmark reads big on Apple
    # Podcasts' grid (where covers display at ~120×120 minimum). Strip
    # the leading/trailing padding spaces so the visible glyphs do the
    # filling rather than padded whitespace.
    trimmed = [r.strip() for r in WORDMARK_ROWS]
    widest = max(trimmed, key=len)
    target_width = int(CANVAS_SIZE * 0.88)
    font = fit_font_to_width(widest, target_width, FONT_PATH)

    sample_w, sample_h = measure_text(widest, font)
    # The Unicode block-drawing rows expect zero inter-row gap so vertical
    # bars merge into one continuous letter stroke. DejaVu Sans Mono adds
    # internal leading; shrink the per-row advance to ~0.78x of the bbox
    # height so adjacent rows tile cleanly.
    line_height = int(sample_h * 0.78)
    total_h = line_height * len(trimmed) + (sample_h - line_height)

    # Tagline + small wordmark beneath the giant title.
    tagline_font = ImageFont.truetype(FONT_PATH, max(36, line_height // 3))
    tagline_w, tagline_h = measure_text(TAGLINE, tagline_font)
    subtag_font = ImageFont.truetype(FONT_PATH, max(24, line_height // 5))
    subtag_w, subtag_h = measure_text(SUBTAG, subtag_font)

    gap = max(40, line_height // 2)
    block_h = total_h + gap + tagline_h + (gap // 2) + subtag_h
    block_top = (CANVAS_SIZE - block_h) // 2

    for i, row in enumerate(trimmed):
        row_w, _ = measure_text(row, font)
        x = (CANVAS_SIZE - row_w) // 2
        y = block_top + i * line_height
        color = INNER_PINK if WORDMARK_USES_DARK[i] else OUTER_PINK
        draw.text((x, y), row, font=font, fill=color)

    tag_x = (CANVAS_SIZE - tagline_w) // 2
    tag_y = block_top + total_h + gap
    draw.text((tag_x, tag_y), TAGLINE, font=tagline_font, fill=TAGLINE_COLOR)

    sub_x = (CANVAS_SIZE - subtag_w) // 2
    sub_y = tag_y + tagline_h + (gap // 2)
    draw.text((sub_x, sub_y), SUBTAG, font=subtag_font, fill=SUBTAG_COLOR)

    img.save(out_path, "PNG", optimize=True)


def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(script_dir)
    out_dir = os.path.join(repo_root, "assets")
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, "podcast-cover.png")
    render(out_path)
    size = os.path.getsize(out_path)
    print(f"Wrote {out_path} ({size:,} bytes, {CANVAS_SIZE}x{CANVAS_SIZE})")


if __name__ == "__main__":
    main()
