"""Draws the BT brand mark: lime Inweb parallelogram + green "BT" in Unbounded Bold.

Same geometry as Bison Switcher's tray icon (Windows/Services/TrayIcons.cs, DrawBrandMark),
scaled from its 32 px drawing. Usage: python3 tools/make-icons.py
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
FONT = ROOT / "src/fonts/Unbounded-Bold.ttf"
OUT = ROOT / "src/icons"
LIME = (0xF0, 0xFC, 0x7E, 255)
ORANGE = (0xEF, 0x58, 0x3D, 255)
GREEN = (0x00, 0x3B, 0x29, 255)
SS = 8  # supersampling factor


def mark(size: int, paused: bool = False) -> Image.Image:
    s = size * SS
    k = s / 32  # BS draws on a 32 px canvas
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.polygon([(8 * k, 3 * k), (s - 0.5 * k, 3 * k), (s - 8 * k, s - 3 * k), (0.5 * k, s - 3 * k)], fill=ORANGE if paused else LIME)
    if paused:
        # Pause bars instead of the monogram: readable at 16 px, unmistakable on the toolbar.
        bw, bh, gap = 3.6 * k, 13 * k, 3.4 * k
        x0, y0 = s / 2 - gap / 2 - bw, s / 2 - bh / 2
        d.rounded_rectangle([x0, y0, x0 + bw, y0 + bh], radius=0.8 * k, fill=GREEN)
        x1 = s / 2 + gap / 2
        d.rounded_rectangle([x1, y0, x1 + bw, y0 + bh], radius=0.8 * k, fill=GREEN)
        return img.resize((size, size), Image.LANCZOS)
    font = ImageFont.truetype(str(FONT), int(13.5 * k))
    box = d.textbbox((0, 0), "BT", font=font)
    w, h = box[2] - box[0], box[3] - box[1]
    d.text(((s - w) / 2 - box[0], (s - h) / 2 - box[1]), "BT", font=font, fill=GREEN)
    return img.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    OUT.mkdir(parents=True, exist_ok=True)
    for size in (16, 32, 48, 128):
        mark(size).save(OUT / f"bt-{size}.png")
        mark(size, paused=True).save(OUT / f"bt-paused-{size}.png")
    print("icons written to", OUT)
