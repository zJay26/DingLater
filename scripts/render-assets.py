"""Render deterministic DingLater application assets with Pillow.

The matching vector source is kept in Assets/DingLaterMark.svg for editing.
"""

from __future__ import annotations

from pathlib import Path
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "DingLater.App" / "Assets"
ACCENT = "#6576C9"
WHITE = "#FFFFFF"
SOFT = "#DDE3FF"


def mark(size: int, *, canvas: tuple[int, int] | None = None) -> Image.Image:
    width, height = canvas or (size, size)
    scale = 4
    image = Image.new("RGBA", (width * scale, height * scale), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    box_size = int(size * 0.82)
    left = (width - box_size) // 2
    top = (height - box_size) // 2
    radius = int(box_size * 0.28)
    box = tuple(value * scale for value in (left, top, left + box_size, top + box_size))
    draw.rounded_rectangle(box, radius=radius * scale, fill=ACCENT)

    cx = width / 2
    cy = height / 2 - size * 0.02
    clock_radius = size * 0.205
    stroke = max(2, int(size * 0.054))
    clock_box = tuple(int(value * scale) for value in (
        cx - clock_radius,
        cy - clock_radius,
        cx + clock_radius,
        cy + clock_radius,
    ))
    draw.ellipse(clock_box, outline=WHITE, width=stroke * scale)
    line_width = max(2, int(size * 0.05)) * scale
    draw.line(
        [(int(cx * scale), int((cy - clock_radius * 0.63) * scale)),
         (int(cx * scale), int((cy + clock_radius * 0.05) * scale)),
         (int((cx + clock_radius * 0.5) * scale), int((cy + clock_radius * 0.34) * scale))],
        fill=WHITE,
        width=line_width,
        joint="curve",
    )
    baseline_y = top + box_size * 0.77
    draw.line(
        [(int((cx - box_size * 0.16) * scale), int(baseline_y * scale)),
         (int((cx + box_size * 0.16) * scale), int(baseline_y * scale))],
        fill=SOFT,
        width=max(2, int(size * 0.04)) * scale,
    )
    return image.resize((width, height), Image.Resampling.LANCZOS)


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    mark(44).save(ASSETS / "Square44x44Logo.png")
    mark(50).save(ASSETS / "StoreLogo.png")
    mark(150).save(ASSETS / "Square150x150Logo.png")
    mark(144, canvas=(310, 150)).save(ASSETS / "Wide310x150Logo.png")
    icon_sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [mark(value) for value in icon_sizes]
    frames[-1].save(
        ASSETS / "DingLater.ico",
        format="ICO",
        append_images=frames[:-1],
        sizes=[(value, value) for value in icon_sizes],
    )


if __name__ == "__main__":
    main()
