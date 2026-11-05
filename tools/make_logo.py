#!/usr/bin/env python3
"""AirTile logo generator.

Monochrome line-art on white: full-bleed WHITE background, near-black
(#111111) glyph only, no text, no gradients, no colour fill.

Glyph: a folder drawn as ONE clean outline path - left edge up, raised
tab across the top-left, short slanted shoulder down to the top-right
edge, right edge down, bottom back to the start - plus ONE horizontal
header-strip line across the folder just below its top edge. Inside
the folder body, centred: a Wi-Fi symbol (hollow ring dot with two
arcs above it).

Reference geometry (proportions of the glyph box side S, drawn
supersampled at ~1240 px on the long side, downscaled with LANCZOS):
  folder path: (0.15,0.76) -> (0.15,0.25) -> (0.37,0.25) ->
               (0.45,0.31) -> (0.85,0.31) -> (0.85,0.76) -> close,
               jointed line, stroke ~0.042*S
  header line: (0.15,0.395) -> (0.85,0.395)
  Wi-Fi dot:   centre (0.50,0.625), ring radius 0.034*S, stroke 0.026*S
  Wi-Fi arcs:  radii 0.072*S and 0.118*S around the dot centre,
               stroke 0.028*S, PIL arc from 205 to 335 degrees

Usage: python3 tools/make_logo.py
Writes every PNG in FtpServer/Assets (at its exact existing size) and
a 620x620 preview to ~/workspace/your_files/AirTile-logo.png.
"""

import math
import os

from PIL import Image, ImageDraw

WHITE = (255, 255, 255)
INK = (0x11, 0x11, 0x11)  # #111111

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

FOLDER_POINTS = [(0.15, 0.76), (0.15, 0.25), (0.37, 0.25),
                 (0.45, 0.31), (0.85, 0.31), (0.85, 0.76)]


def draw_glyph(d, box_x, box_y, s):
    """Draw the ink glyph inside the square box (box_x, box_y, side s)."""

    def pt(px, py):
        return (box_x + px * s, box_y + py * s)

    def w(ref):
        return max(1, int(round(ref * s)))

    # --- Folder outline: one jointed path, closed back to the start -----
    pts = [pt(px, py) for px, py in FOLDER_POINTS]
    d.line(pts + [pts[0]], fill=INK, width=w(0.042), joint="curve")

    # --- Header strip line -----------------------------------------------
    d.line([pt(0.15, 0.395), pt(0.85, 0.395)], fill=INK, width=w(0.042))

    # --- Wi-Fi: hollow ring dot + two arcs --------------------------------
    cx, cy = pt(0.50, 0.625)
    r = 0.034 * s
    d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=INK, width=w(0.026))
    for rad in (0.072, 0.118):
        rr = rad * s
        d.arc([cx - rr, cy - rr, cx + rr, cy + rr],
              start=205, end=335, fill=INK, width=w(0.028))


def render(width, height):
    """Render the full-bleed logo at (width, height), supersampled."""
    k = max(1, int(math.ceil(1240 / max(width, height))))
    w, h = width * k, height * k
    img = Image.new("RGB", (w, h), WHITE)
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
