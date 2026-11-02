# SPDX-License-Identifier: AGPL-3.0-or-later
# /// script
# requires-python = ">=3.11"
# dependencies = ["pillow>=11"]
# ///
"""Make fresh image probes for docs/test-playbook-attachments.md.

Every run draws new answers, so nothing a model is asked can be found in this
public repository. Upload <out>/upload; keep <out>/key out of every vault.

    uv run tools/attachment-probes/make_probes.py <out> [--seed N]
"""

import argparse
import math
import random
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

COLOURS = {
    "red": (214, 40, 40),
    "orange": (247, 127, 0),
    "yellow": (240, 200, 8),
    "green": (46, 139, 87),
    "blue": (30, 90, 200),
    "purple": (120, 60, 170),
    "pink": (240, 120, 170),
    "brown": (120, 80, 40),
}
CORNERS = ["top left", "top right", "bottom left", "bottom right"]


def font(size: int) -> ImageFont.ImageFont:
    return ImageFont.load_default(size=size)


def draw_shape(d: ImageDraw.ImageDraw, kind: str, cx: int, cy: int, r: int, fill) -> None:
    if kind == "circle":
        d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=fill)
    elif kind == "square":
        d.rectangle((cx - r, cy - r, cx + r, cy + r), fill=fill)
    elif kind == "triangle":
        d.polygon([(cx, cy - r), (cx - r, cy + r), (cx + r, cy + r)], fill=fill)
    elif kind == "star":
        points = []
        for i in range(10):
            radius = r if i % 2 == 0 else r * 0.42
            angle = -math.pi / 2 + i * math.pi / 5
            points.append((cx + radius * math.cos(angle), cy + radius * math.sin(angle)))
        d.polygon(points, fill=fill)


def shapes(rng: random.Random, out: Path) -> list[str]:
    """No text at all: OCR can't answer this, only seeing can."""
    columns, rows, cell = 4, 3, 300
    image = Image.new("RGB", (columns * cell, rows * cell), (250, 248, 240))
    d = ImageDraw.Draw(image)

    corner_cells = {"top left": 0, "top right": columns - 1, "bottom left": columns * (rows - 1), "bottom right": columns * rows - 1}
    star_corner = rng.choice(CORNERS)
    star_cell = corner_cells[star_corner]
    others = [c for c in range(columns * rows) if c != star_cell]
    rng.shuffle(others)

    circles = rng.randint(2, 5)
    kinds = {star_cell: "star", others[0]: "triangle"}
    kinds |= {c: "circle" for c in others[1 : 1 + circles]}
    kinds |= {c: "square" for c in others[1 + circles :]}

    triangle_colour = rng.choice(list(COLOURS))
    for number, kind in kinds.items():
        name = triangle_colour if kind == "triangle" else rng.choice([c for c in COLOURS if c != triangle_colour])
        cx = number % columns * cell + cell // 2
        cy = number // columns * cell + cell // 2
        draw_shape(d, kind, cx, cy, 95, COLOURS[name])

    image.save(out / "shapes.png")
    return [
        "## shapes.png (seeing, not OCR: the image has no text)",
        f"- Colour of the triangle: **{triangle_colour}**",
        f"- Corner holding the star: **{star_corner}**",
        f"- Number of circles: **{circles}**",
    ]


def small_text(rng: random.Random, out: Path) -> list[str]:
    """A phone-sized screenshot in 14 px type: the whole picture at medium is too coarse to read."""
    image = Image.new("RGB", (1440, 3120), "white")
    d = ImageDraw.Draw(image)
    type_ = font(14)
    d.text((40, 30), "Stock list", fill="black", font=font(28))

    rows = 120
    failed = rng.randint(60, rows - 1)
    values = rng.sample(range(100000, 999999), rows)
    for row in range(rows):
        y = 90 + row * 24
        d.text((40, y), f"item-{row:03d}", fill="black", font=type_)
        d.text((400, y), str(values[row]), fill="black", font=type_)
        d.text((800, y), "FAILED" if row == failed else "OK", fill="black", font=type_)

    image.save(out / "small-text.png")
    return [
        "## small-text.png (details: needs a crop or a large step)",
        f"- The one row with the status FAILED: **item-{failed:03d}**",
        f"- The number in that row: **{values[failed]}**",
    ]


def chart(rng: random.Random, out: Path) -> list[str]:
    """Bars without value labels: the answer lies in the picture, not in its text."""
    width, height, left, bottom, top = 1400, 900, 110, 800, 60
    image = Image.new("RGB", (width, height), "white")
    d = ImageDraw.Draw(image)
    scale = (bottom - top) / 100

    for value in range(0, 101, 10):
        y = bottom - value * scale
        d.line((left, y, width - 40, y), fill=(220, 220, 220), width=2)
        d.text((40, y - 12), str(value), fill="black", font=font(22))

    labels = "ABCDEFGH"
    names = rng.sample(list(COLOURS), len(labels))
    heights = rng.sample(range(15, 96), len(labels))
    step = (width - left - 40) / len(labels)
    for i, label in enumerate(labels):
        x = left + i * step + 20
        d.rectangle((x, bottom - heights[i] * scale, x + step - 40, bottom), fill=COLOURS[names[i]])
        d.text((x + step / 2 - 30, bottom + 15), label, fill="black", font=font(26))

    image.save(out / "chart.png")
    tallest = max(range(len(labels)), key=heights.__getitem__)
    return [
        "## chart.png (reading values off a chart)",
        f"- Tallest bar: **{labels[tallest]}**, about **{heights[tallest]}** (within 5 is a pass)",
        f"- Colour of bar C: **{names[2]}**",
    ]


def rotated(rng: random.Random, out: Path) -> list[str]:
    """Stored on its side with an EXIF orientation, as a phone does: the answers hold only if it is applied."""
    shown = Image.effect_noise((2000, 3000), 28).convert("RGB")
    shown = Image.blend(shown, Image.new("RGB", shown.size, (200, 215, 225)), 0.6)
    d = ImageDraw.Draw(shown)

    direction = rng.choice(["up", "down", "left", "right"])
    angle = {"up": 0, "right": 90, "down": 180, "left": 270}[direction]
    arrow = [(0, -600), (350, -100), (130, -100), (130, 600), (-130, 600), (-130, -100), (-350, -100)]
    turn = math.radians(angle)
    d.polygon(
        [(1000 + x * math.cos(turn) - y * math.sin(turn), 1500 + x * math.sin(turn) + y * math.cos(turn)) for x, y in arrow],
        fill=COLOURS["red"],
    )

    corner = rng.choice(CORNERS)
    x = 60 if "left" in corner else 2000 - 460
    y = 60 if "top" in corner else 3000 - 460
    d.rectangle((x, y, x + 400, y + 400), fill=COLOURS["blue"])

    exif = Image.Exif()
    exif[0x0112] = 6  # "rotate 90° clockwise to display"
    shown.rotate(90, expand=True).save(out / "rotated.jpg", quality=88, exif=exif)
    return [
        "## rotated.jpg (the camera's rotation applied)",
        "- `cat` reports it as **2000×3000** (stored as 3000×2000)",
        f"- The red arrow points **{direction}**",
        f"- Corner holding the blue square: **{corner}**",
    ]


def sideways(rng: random.Random, out: Path) -> list[str]:
    """A sheet stored on its side with no EXIF orientation: only a turn of the picture itself makes it readable."""
    page = Image.new("RGB", (1700, 2200), (250, 248, 242))
    d = ImageDraw.Draw(page)
    d.text((120, 120), "Delivery note", fill="black", font=font(90))
    for i in range(9):
        y = 330 + i * 70
        d.line((120, y, 1580, y), fill=(200, 200, 200), width=3)
        d.line((120, y + 28, 120 + rng.randint(700, 1400), y + 28), fill=(150, 150, 150), width=14)

    code = rng.randint(1000, 9999)
    d.text((120, 1900), f"Ref {code}", fill="black", font=font(28))

    # Stored 2200×1700 on its side, counter-clockwise; no EXIF, so a client sees it as stored.
    page.rotate(90, expand=True).save(out / "sideways.png")
    return [
        "## sideways.png (a sheet on its side, no EXIF: `--rotate 90` reads it)",
        f"- The small print at the bottom of the sheet: **Ref {code}**",
        "- Note how many calls it took to read the code and to crop it, and whether the crop hit",
    ]


def wide(rng: random.Random, out: Path) -> list[str]:
    """A panorama reduced a lot by every step; the marker's code is only legible in a crop."""
    image = Image.new("RGB", (9000, 500), (235, 240, 235))
    d = ImageDraw.Draw(image)
    for x in range(0, 9000, 250):
        d.line((x, 0, x, 500), fill=(205, 212, 205), width=3)

    x = rng.randint(6200, 8600)
    name = rng.choice(list(COLOURS))
    code = rng.randint(1000, 9999)
    draw_shape(d, "circle", x, 200, 70, COLOURS[name])
    d.text((x - 40, 290), str(code), fill="black", font=font(30))

    image.save(out / "wide.png")
    return [
        "## wide.png (very wide: the text should suggest a crop)",
        f"- Colour of the circle: **{name}**, near x = {x}",
        f"- Code below it: **{code}**",
    ]


def animated(rng: random.Random, out: Path) -> list[str]:
    names = rng.sample(list(COLOURS), 3)
    frames = [Image.new("RGB", (600, 400), COLOURS[name]) for name in names]
    frames[0].save(out / "animated.gif", save_all=True, append_images=frames[1:], duration=500, loop=0)
    return [
        "## animated.gif (three frames; `--frame 2` shows the second)",
        f"- Frames in order: **{', '.join(names)}**",
        f"- Colour of frame 2: **{names[1]}**. Without `--frame` only the first, **{names[0]}**, arrives.",
    ]


def transparent(rng: random.Random, out: Path) -> list[str]:
    """A black shape on a transparent ground vanishes if a step flattens the image onto black."""
    image = Image.new("RGBA", (800, 600), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)
    name = rng.choice(list(COLOURS))
    draw_shape(d, "triangle", 250, 300, 150, (0, 0, 0, 255))
    draw_shape(d, "circle", 570, 300, 130, COLOURS[name] + (255,))
    image.save(out / "transparent.webp", lossless=True)
    return [
        "## transparent.webp (a transparent ground)",
        f"- A **black triangle** on the left and a **{name} circle** on the right",
    ]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("out", type=Path)
    parser.add_argument("--seed", type=int)
    args = parser.parse_args()

    upload = args.out / "upload"
    key = args.out / "key"
    upload.mkdir(parents=True, exist_ok=True)
    key.mkdir(parents=True, exist_ok=True)

    rng = random.Random(args.seed)
    lines = ["# Answer key: keep out of every vault", ""]
    for make in (shapes, small_text, chart, rotated, sideways, wide, animated, transparent):
        lines += make(rng, upload) + [""]
    (key / "answers.md").write_text("\n".join(lines), encoding="utf-8")
    print(f"Upload {upload}\nKeep {key / 'answers.md'} to yourself")


if __name__ == "__main__":
    main()
