#!/usr/bin/env python3
"""AirTile logo generator.

Flat Windows Phone tile style: full-bleed solid #0078D7 (Windows blue),
one white glyph, no text, no gradients. Glyph, top to bottom: a Wi-Fi
symbol (centre dot + two arcs) over a pair of transfer arrows (DOWN on
the left, UP on the right, rounded shafts, triangular heads).

Reference geometry (proportions of the glyph box side S):
  dot:   centre (S/2, 0.30*S), radius 0.030*S
  arcs:  radii 0.085*S and 0.145*S around the dot centre,
         stroke 0.030*S, PIL arc from 205 to 335 degrees
  arrows zone: top 0.46*S, bottom 0.78*S; shaft width 0.075*S
         (rounded ends), head width 0.155*S, head height 0.130*S;
         centre gap 0.085*S between the two shafts

The whole glyph group is scaled up by SCALE (1.12) about its own
centre (0.46*S) and optically centred in the box. Everything is drawn
supersampled (~1240 px on the long side) and downscaled with LANCZOS.

Usage: python3 tools/make_logo.py
Writes every PNG in FtpServer/Assets (at its exact existing size) and
a 620x620 preview to ~/workspace/your_files/AirTile-logo.png.
"""

import math
import os

from PIL import Image, ImageDraw

BLUE = (0x00, 0x78, 0xD7)  # #0078D7
WHITE = (255, 255, 255)

SCALE = 1.12       # glyph group scale-up vs the reference proportions
GROUP_CY = 0.46    # reference vertical centre of the glyph group

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS_DIR = os.path.join(REPO_ROOT, "FtpServer", "Assets")
PREVIEW_PATH = os.path.expanduser("~/workspace/your_files/AirTile-logo.png")

ASSETS = {
    "AppLogo.png": (96, 96),
    "BadgeLogo.png": (58, 58),
    "SplashScreen.png": (620, 300),
    "Square150x150Logo.png": (150, 150),
    "Square310x310Logo.png": (310, 310),
    "Square44x44Logo.png": (44, 44),
    "Square71x71Logo.png": (71, 71),
    "StoreLogo.png": (50, 50),
    "Wide310x150Logo.png": (310, 150),
}


def draw_glyph(d, box_x, box_y, s):
    """Draw the white glyph inside the square box (box_x, box_y, side s)."""

    def x(ref):
        return box_x + (0.5 + (ref - 0.5) * SCALE) * s

    def y(ref):
        return box_y + (0.5 + (ref - GROUP_CY) * SCALE) * s

    def ln(ref):
        return ref * SCALE * s

    # --- Wi-Fi: dot + two arcs -------------------------------------------
    cx = x(0.5)
    dot_cy = y(0.30)
    r = ln(0.030)
    d.ellipse([cx - r, dot_cy - r, cx + r, dot_cy + r], fill=WHITE)
    for rad in (0.085, 0.145):
        rr = ln(rad)
        w = max(1, int(round(ln(0.030))))
        d.arc([cx - rr, dot_cy - rr, cx + rr, dot_cy + rr],
              start=205, end=335, fill=WHITE, width=w)

    # --- Transfer arrows --------------------------------------------------
    top, bot = y(0.46), y(0.78)
    sw = ln(0.075)          # shaft width
    hw = ln(0.155)          # head width
    hh = ln(0.130)          # head height
    gap = ln(0.085)         # centre gap between the two shafts
    left_cx = cx - gap / 2 - sw / 2
    right_cx = cx + gap / 2 + sw / 2
    overlap = ln(0.01)      # shaft tucks slightly into its head

    def shaft(cx_, y0, y1):
        rr = sw / 2
        d.rectangle([cx_ - rr, y0, cx_ + rr, y1], fill=WHITE)
        d.ellipse([cx_ - rr, y0 - rr, cx_ + rr, y0 + rr], fill=WHITE)
        d.ellipse([cx_ - rr, y1 - rr, cx_ + rr, y1 + rr], fill=WHITE)

    # DOWN arrow (left): shaft from the top, head at the bottom.
    shaft(left_cx, top, bot - hh + overlap)
    d.polygon([(left_cx - hw / 2, bot - hh),
               (left_cx + hw / 2, bot - hh),
               (left_cx, bot)], fill=WHITE)

    # UP arrow (right): head at the top, shaft to the bottom.
    d.polygon([(right_cx - hw / 2, top + hh),
               (right_cx + hw / 2, top + hh),
               (right_cx, top)], fill=WHITE)
    shaft(right_cx, top + hh - overlap, bot)


def render(width, height):
    """Render the full-bleed logo at (width, height), supersampled."""
    k = max(1, int(math.ceil(1240 / max(width, height))))
    w, h = width * k, height * k
    img = Image.new("RGB", (w, h), BLUE)
    d = ImageDraw.Draw(img)
    box = min(w, h)  # glyph box: full canvas for squares, height for wide
    draw_glyph(d, (w - box) / 2, (h - box) / 2, box)
    if k > 1:
        img = img.resize((width, height), Image.LANCZOS)
    return img


def main():
    for name, (w, h) in sorted(ASSETS.items()):
        path = os.path.join(ASSETS_DIR, name)
        render(w, h).save(path)
        print("wrote", path, (w, h))
    os.makedirs(os.path.dirname(PREVIEW_PATH), exist_ok=True)
    render(620, 620).save(PREVIEW_PATH)
    print("wrote", PREVIEW_PATH, (620, 620))


if __name__ == "__main__":
    main()
