"""Render deterministic DingLater application and tray icon assets.

The active icon and all selectable alternatives have matching SVG sources under
``Assets/IconCandidates``.  Pillow is used only as the deterministic rasterizer
for the PNG/ICO files consumed by Windows.
"""

from __future__ import annotations

from pathlib import Path
from typing import Callable

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "DingLater.App" / "Assets"
CANDIDATES = ASSETS / "IconCandidates"
DOC_ASSETS = ROOT / "docs" / "assets"
ACTIVE_VARIANT = "full-clock"
ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
WHITE = "#FFFFFF"
BADGE_RED = "#E53935"


def _surface(size: int, canvas: tuple[int, int] | None) -> tuple[Image.Image, ImageDraw.ImageDraw, int, int, int]:
    width, height = canvas or (size, size)
    supersample = 4
    image = Image.new("RGBA", (width * supersample, height * supersample), (0, 0, 0, 0))
    return image, ImageDraw.Draw(image), width, height, supersample


def _finish(image: Image.Image, width: int, height: int) -> Image.Image:
    return image.resize((width, height), Image.Resampling.LANCZOS)


def _rounded_square(
    draw: ImageDraw.ImageDraw,
    cx: float,
    cy: float,
    size: float,
    scale: int,
    fill: str,
    inset_ratio: float = 0.015,
    radius_ratio: float = 0.275,
) -> tuple[float, float, float, float]:
    inset = size * inset_ratio
    left = cx - size / 2 + inset
    top = cy - size / 2 + inset
    right = cx + size / 2 - inset
    bottom = cy + size / 2 - inset
    draw.rounded_rectangle(
        tuple(int(value * scale) for value in (left, top, right, bottom)),
        radius=int(size * radius_ratio * scale),
        fill=fill,
    )
    return left, top, right, bottom


def _clock(
    draw: ImageDraw.ImageDraw,
    cx: float,
    cy: float,
    size: float,
    scale: int,
    *,
    color: str = WHITE,
    radius_ratio: float = 0.255,
    stroke_ratio: float = 0.066,
) -> None:
    radius = size * radius_ratio
    stroke = max(1, round(size * stroke_ratio)) * scale
    draw.ellipse(
        tuple(int(value * scale) for value in (cx - radius, cy - radius, cx + radius, cy + radius)),
        outline=color,
        width=stroke,
    )
    hand_width = max(1, round(size * 0.06)) * scale
    draw.line(
        [
            (int(cx * scale), int((cy - radius * 0.62) * scale)),
            (int(cx * scale), int((cy + radius * 0.04) * scale)),
            (int((cx + radius * 0.52) * scale), int((cy + radius * 0.33) * scale)),
        ],
        fill=color,
        width=hand_width,
        joint="curve",
    )


def full_clock(size: int, *, canvas: tuple[int, int] | None = None, attention: bool = False) -> Image.Image:
    image, draw, width, height, scale = _surface(size, canvas)
    cx, cy = width / 2, height / 2
    _rounded_square(
        draw,
        cx,
        cy,
        size,
        scale,
        "#8998E5" if attention else "#6576C9",
        inset_ratio=0,
        radius_ratio=0.235,
    )
    _clock(draw, cx, cy - size * 0.015, size, scale, radius_ratio=0.275, stroke_ratio=0.068)
    return _finish(image, width, height)


def chat_clock(size: int, *, canvas: tuple[int, int] | None = None, attention: bool = False) -> Image.Image:
    image, draw, width, height, scale = _surface(size, canvas)
    cx, cy = width / 2, height / 2
    color = "#35B8FF" if attention else "#148BE5"
    left, top = cx - size * 0.485, cy - size * 0.475
    right, bottom = cx + size * 0.485, cy + size * 0.345
    draw.rounded_rectangle(
        tuple(int(value * scale) for value in (left, top, right, bottom)),
        radius=int(size * 0.26 * scale),
        fill=color,
    )
    draw.polygon(
        [
            (int((cx + size * 0.10) * scale), int((bottom - size * 0.02) * scale)),
            (int((cx + size * 0.34) * scale), int((cy + size * 0.475) * scale)),
            (int((cx + size * 0.30) * scale), int((bottom - size * 0.11) * scale)),
        ],
        fill=color,
    )
    _clock(draw, cx - size * 0.015, cy - size * 0.06, size, scale, radius_ratio=0.235)
    return _finish(image, width, height)


def bookmark_clock(size: int, *, canvas: tuple[int, int] | None = None, attention: bool = False) -> Image.Image:
    image, draw, width, height, scale = _surface(size, canvas)
    cx, cy = width / 2, height / 2
    color = "#9A7BFF" if attention else "#7758D5"
    left, top = cx - size * 0.43, cy - size * 0.485
    right, bottom = cx + size * 0.43, cy + size * 0.475
    draw.rounded_rectangle(
        tuple(int(value * scale) for value in (left, top, right, bottom)),
        radius=int(size * 0.18 * scale),
        fill=color,
    )
    draw.polygon(
        [
            (int((cx - size * 0.18) * scale), int((bottom - size * 0.01) * scale)),
            (int(cx * scale), int((bottom - size * 0.18) * scale)),
            (int((cx + size * 0.18) * scale), int((bottom - size * 0.01) * scale)),
        ],
        fill=(0, 0, 0, 0),
    )
    _clock(draw, cx, cy - size * 0.075, size, scale, radius_ratio=0.225)
    return _finish(image, width, height)


def hourglass(size: int, *, canvas: tuple[int, int] | None = None, attention: bool = False) -> Image.Image:
    image, draw, width, height, scale = _surface(size, canvas)
    cx, cy = width / 2, height / 2
    _rounded_square(draw, cx, cy, size, scale, "#FF9076" if attention else "#ED684B")
    x0, x1 = cx - size * 0.22, cx + size * 0.22
    y0, y1 = cy - size * 0.28, cy + size * 0.28
    stroke = max(1, round(size * 0.065)) * scale
    draw.line([(int(x0 * scale), int(y0 * scale)), (int(x1 * scale), int(y0 * scale))], fill=WHITE, width=stroke)
    draw.line([(int(x0 * scale), int(y1 * scale)), (int(x1 * scale), int(y1 * scale))], fill=WHITE, width=stroke)
    draw.line(
        [
            (int((x0 + size * 0.035) * scale), int((y0 + size * 0.02) * scale)),
            (int((x1 - size * 0.035) * scale), int((y1 - size * 0.02) * scale)),
        ],
        fill=WHITE,
        width=stroke,
    )
    draw.line(
        [
            (int((x1 - size * 0.035) * scale), int((y0 + size * 0.02) * scale)),
            (int((x0 + size * 0.035) * scale), int((y1 - size * 0.02) * scale)),
        ],
        fill=WHITE,
        width=stroke,
    )
    return _finish(image, width, height)


VARIANTS: dict[str, Callable[..., Image.Image]] = {
    "full-clock": full_clock,
    "chat-clock": chat_clock,
    "bookmark-clock": bookmark_clock,
    "hourglass": hourglass,
}


def save_ico(path: Path, render: Callable[..., Image.Image]) -> None:
    frames = [render(value) for value in ICON_SIZES]
    frames[-1].save(
        path,
        format="ICO",
        append_images=frames[:-1],
        sizes=[(value, value) for value in ICON_SIZES],
    )


def badge_background(*, attention: bool) -> Image.Image:
    size = 128
    image = VARIANTS[ACTIVE_VARIANT](size, attention=attention)
    scale = 4
    large = image.resize((size * scale, size * scale), Image.Resampling.LANCZOS)
    draw = ImageDraw.Draw(large)
    cx, cy, radius = 102, 26, 24
    draw.ellipse(
        tuple(int(value * scale) for value in (cx - radius, cy - radius, cx + radius, cy + radius)),
        fill=WHITE,
    )
    inner = radius - 3
    draw.ellipse(
        tuple(int(value * scale) for value in (cx - inner, cy - inner, cx + inner, cy + inner)),
        fill=BADGE_RED,
    )
    return large.resize((size, size), Image.Resampling.LANCZOS)


def _font(size: int, *, bold: bool = False) -> ImageFont.ImageFont:
    names = ["seguisb.ttf", "segoeuib.ttf"] if bold else ["segoeui.ttf"]
    for name in names:
        path = Path("C:/Windows/Fonts") / name
        if path.exists():
            return ImageFont.truetype(str(path), size=size)
    return ImageFont.load_default()


def render_options_preview() -> Image.Image:
    labels = [
        ("A", "full-clock", "Full Clock"),
        ("B", "chat-clock", "Chat + Clock"),
        ("C", "bookmark-clock", "Bookmark + Clock"),
        ("D", "hourglass", "Hourglass"),
    ]
    preview = Image.new("RGB", (1160, 430), "#EEF1F6")
    draw = ImageDraw.Draw(preview)
    title_font = _font(25, bold=True)
    label_font = _font(18)
    for index, (letter, variant, label) in enumerate(labels):
        x = 24 + index * 284
        draw.rounded_rectangle((x, 22, x + 260, 408), radius=28, fill="#FFFFFF")
        icon = VARIANTS[variant](190)
        preview.paste(icon, (x + 35, 56), icon)
        draw.text((x + 26, 268), f"{letter}  {label}", font=title_font, fill="#202124")
        draw.text((x + 26, 309), "Tray size", font=label_font, fill="#667085")
        draw.rounded_rectangle((x + 24, 340, x + 236, 388), radius=12, fill="#E7EAF0")
        tray_icon = VARIANTS[variant](24)
        preview.paste(tray_icon, (x + 118, 352), tray_icon)
    return preview


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    CANDIDATES.mkdir(parents=True, exist_ok=True)
    DOC_ASSETS.mkdir(parents=True, exist_ok=True)
    active = VARIANTS[ACTIVE_VARIANT]

    active(44).save(ASSETS / "Square44x44Logo.png")
    active(50).save(ASSETS / "StoreLogo.png")
    active(150).save(ASSETS / "Square150x150Logo.png")
    active(144, canvas=(310, 150)).save(ASSETS / "Wide310x150Logo.png")
    save_ico(ASSETS / "DingLater.ico", active)
    save_ico(ASSETS / "DingLaterTray.ico", active)

    badge_background(attention=False).save(ASSETS / "DingLaterTrayBadge.png")
    badge_background(attention=True).save(ASSETS / "DingLaterTrayBadgeAttention.png")

    for name, render in VARIANTS.items():
        render(256).save(CANDIDATES / f"{name}.png")
        save_ico(CANDIDATES / f"{name}.ico", render)

    render_options_preview().save(DOC_ASSETS / "icon-options.png")


if __name__ == "__main__":
    main()
