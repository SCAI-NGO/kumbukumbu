# SPDX-License-Identifier: AGPL-3.0-or-later
# /// script
# requires-python = ">=3.11"
# dependencies = ["matplotlib>=3.9", "openpyxl>=3.1"]
# ///
"""Make a corpus of generated figures for the eval of regions with labels (spec section 4.5).

Usage: uv run tools/attachment-probes/make_figure_pdfs.py <out> --seed N

Writes <out>/seed-N/<generator>/<layout>-<i>.pdf for matplotlib, LibreOffice Calc and pdf-lib, each
with a manifest <layout>-<i>.json beside it that says what the generator asked to be drawn. Every
label, tick label, axis title and legend entry carries its figure's token (zq<Letter><digit>); body
text comes from a word list of its own with no tokens and no digits; a caption starts "Figure N".
The pdf-lib figures come from pdf/eval/figures.ts, through `pnpm -C pdf eval:figures`.
"""

from __future__ import annotations

import argparse
import json
import os
import random
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
PAGE_W, PAGE_H = 595, 842

LAYOUTS = [
    "legend-right",
    "legend-below",
    "side-by-side",
    "between-paragraphs",
    "two-columns",
    "long-caption",
    "turned-page",
    "turned-labels",
]

# Body text: no tokens, no digits, and no word shared with the labels.
BODY_WORDS = """
about above across after again against along among around before behind below beside between
beyond common during early every field final first following further general given great group
habit house idea known large later least little local long making matter measure method middle
might moment morning nature never notes number often order other paper part people period place
plain point present public question quite rather reason record report result return right round
second seems several short simple since small social sound space study system table taken their
there these thing those though three through today together toward under until usual value various
very water where whether which while whole within without words world write years young
""".split()

# Label words: the words of axis titles, categories, series and chart titles.
LABEL_WORDS = """
north south east west spring summer autumn winter apple pear plum cherry lemon melon grape peach
maple birch cedar willow aspen alder rowan hazel heron finch raven robin swift wren lark crane
copper silver nickel cobalt zinc iron tin amber coral ivory indigo violet orchard harvest meadow
valley harbour island canyon glacier yield height weight speed depth count share load
""".split()

assert not set(BODY_WORDS) & set(LABEL_WORDS), set(BODY_WORDS) & set(LABEL_WORDS)
assert not any(c.isdigit() for w in BODY_WORDS + LABEL_WORDS for c in w)
assert not any(w.startswith("zq") for w in BODY_WORDS + LABEL_WORDS)


@dataclass
class Figure:
    number: int
    token: str
    categories: list[str]
    series: list[str]
    x_title: str
    y_title: str
    title: str | None
    ticks: list[int]
    values: list[list[int]]
    kind: str
    labels: list[str] = field(default_factory=list)

    def manifest(self) -> dict:
        return {"figure": self.number, "token": self.token, "labels": self.labels, "minTickLabels": len(self.ticks)}


def new_figure(rng: random.Random, number: int, used: set[str], small: bool = False) -> Figure:
    while True:
        token = f"zq{rng.choice('ABCDEFGHIJKLMNOPQRSTUVWXYZ')}{rng.randrange(10)}"
        if token not in used:
            used.add(token)
            break
    n_cats = rng.randint(3, 4) if small else rng.randint(4, 5)
    words = rng.sample(LABEL_WORDS, n_cats + 5)
    label = lambda w: f"{token} {w}"
    step = rng.choice([5, 10, 20, 25])
    n_ticks = rng.randint(4, 5)
    ticks = [step * k for k in range(n_ticks)]
    top = ticks[-1]
    values = [[rng.randint(max(1, top // 6), top) for _ in range(n_cats)] for _ in range(2)]
    f = Figure(
        number=number,
        token=token,
        categories=[label(w) for w in words[:n_cats]],
        series=[label(w) for w in words[n_cats : n_cats + 2]],
        x_title=label(words[n_cats + 2]),
        y_title=label(words[n_cats + 3]),
        title=label(words[n_cats + 4]) if rng.random() < 0.5 else None,
        ticks=ticks,
        values=values,
        kind=rng.choice(["bar", "line"]),
    )
    f.labels = f.categories + f.series + [f.x_title, f.y_title] + ([f.title] if f.title else [])
    return f


def body_line(rng: random.Random, max_chars: int) -> str:
    words: list[str] = []
    while True:
        w = rng.choice(BODY_WORDS)
        if len(" ".join(words + [w])) + 1 > max_chars:
            break
        words.append(w)
    line = " ".join(words)
    return line[0].upper() + line[1:] + "."


def caption_lines(rng: random.Random, number: int, max_chars: int, lines: int) -> list[str]:
    first = f"Figure {number}. " + body_line(rng, max_chars - 10)
    return [first] + [body_line(rng, max_chars) for _ in range(lines - 1)]


def write_manifest(pdf: Path, generator: str, layout: str, figures: list[Figure]) -> None:
    data = {"generator": generator, "layout": layout, "page": 1, "figures": [f.manifest() for f in figures]}
    if layout.startswith("control-"):
        data["control"] = True
    pdf.with_suffix(".json").write_text(json.dumps(data, indent=2) + "\n")


# ---------------------------------------------------------------- matplotlib


def matplotlib_figures(out: Path, rng: random.Random) -> list[Path]:
    import matplotlib

    matplotlib.use("pdf")
    matplotlib.rcParams["pdf.fonttype"] = 42
    matplotlib.rcParams["font.size"] = 9
    import matplotlib.pyplot as plt

    def rect(x: float, y: float, w: float, h: float) -> list[float]:
        # Points from the top left to figure fractions.
        return [x / PAGE_W, 1 - (y + h) / PAGE_H, w / PAGE_W, h / PAGE_H]

    def text(fig, x: float, y: float, line: str) -> None:
        fig.text(x / PAGE_W, 1 - y / PAGE_H, line, fontsize=10, va="top", ha="left")

    def paragraph(fig, x: float, y: float, width: float, lines: int) -> float:
        for _ in range(lines):
            text(fig, x, y, body_line(rng, int(width / 5.8)))
            y += 14
        return y

    def chart(fig, f: Figure, x: float, y: float, w: float, h: float, legend: str, slanted: bool = False) -> None:
        ax = fig.add_axes(rect(x, y, w, h))
        xs = list(range(len(f.categories)))
        if f.kind == "bar":
            ax.bar([i - 0.2 for i in xs], f.values[0], 0.38, label=f.series[0])
            ax.bar([i + 0.2 for i in xs], f.values[1], 0.38, label=f.series[1])
        else:
            for s, vals in zip(f.series, f.values):
                ax.plot(xs, vals, marker="o", label=s)
        ax.set_xticks(xs, f.categories, rotation=40 if slanted else 0, ha="right" if slanted else "center")
        ax.set_yticks(f.ticks, [f"{f.token} {t}" for t in f.ticks])
        ax.set_ylim(0, f.ticks[-1])
        ax.set_xlabel(f.x_title)
        ax.set_ylabel(f.y_title)
        if f.title:
            ax.set_title(f.title)
        if legend == "right":
            ax.legend(loc="center left", bbox_to_anchor=(1.02, 0.5))
        else:
            below = (70 if slanted else 34) / h
            ax.legend(loc="upper center", bbox_to_anchor=(0.5, -below), ncol=2)

    made: list[Path] = []
    for layout in LAYOUTS:
        used: set[str] = set()
        fig = plt.figure(figsize=(PAGE_W / 72, PAGE_H / 72))
        figs: list[Figure] = []
        if layout in ("legend-right", "turned-page", "turned-labels"):
            f = new_figure(rng, 1, used)
            paragraph(fig, 56, 60, 480, 5)
            chart(fig, f, 90, 220, 330, 290, "right", slanted=layout == "turned-labels")
            paragraph(fig, 56, 650, 480, 6)
            figs = [f]
        elif layout == "legend-below":
            f = new_figure(rng, 1, used)
            paragraph(fig, 56, 60, 480, 5)
            chart(fig, f, 90, 210, 420, 280, "below")
            paragraph(fig, 56, 650, 480, 6)
            figs = [f]
        elif layout == "side-by-side":
            figs = [new_figure(rng, 1, used, small=True), new_figure(rng, 2, used, small=True)]
            paragraph(fig, 56, 60, 480, 6)
            # About 11 % of the page each, labels included, as in the playbook.
            chart(fig, figs[0], 85, 270, 180, 170, "below")
            chart(fig, figs[1], 360, 270, 180, 170, "below")
            paragraph(fig, 56, 560, 480, 8)
        elif layout == "between-paragraphs":
            f = new_figure(rng, 1, used)
            y = paragraph(fig, 56, 60, 480, 12)
            top = y + (34 if f.title else 16)
            chart(fig, f, 90, top, 330, 250, "right")
            paragraph(fig, 56, top + 250 + 40, 480, 12)
            figs = [f]
        elif layout == "two-columns":
            f = new_figure(rng, 1, used, small=True)
            paragraph(fig, 56, 60, 220, 50)
            chart(fig, f, 345, 300, 190, 200, "below")
            figs = [f]
        elif layout == "long-caption":
            f = new_figure(rng, 1, used)
            paragraph(fig, 56, 60, 480, 4)
            chart(fig, f, 90, 190, 330, 290, "right")
            y = 190 + 290 + 44
            for line in caption_lines(rng, 1, 82, 4):
                text(fig, 56, y, line)
                y += 14
            paragraph(fig, 56, y + 30, 480, 8)
            figs = [f]
        pdf = out / f"{layout}-1.pdf"
        fig.savefig(pdf)
        plt.close(fig)
        write_manifest(pdf, "matplotlib", layout, figs)
        made.append(pdf)
    return made


# ---------------------------------------------------------------- LibreOffice Calc


def calc_figures(out: Path, rng: random.Random) -> list[Path]:
    from openpyxl import Workbook
    from openpyxl.chart import BarChart, LineChart, Reference
    from openpyxl.chart.data_source import NumFmt
    from openpyxl.chart.shapes import GraphicalProperties
    from openpyxl.chart.text import RichText
    from openpyxl.drawing.line import LineProperties
    from openpyxl.drawing.text import CharacterProperties, Paragraph, ParagraphProperties, RichTextProperties
    from openpyxl.styles import Border, Side

    src = out / "xlsx"
    src.mkdir(parents=True, exist_ok=True)

    def sheet():
        wb = Workbook()
        ws = wb.active
        ws.title = "Page"
        for col in "ABCDEFGH":
            ws.column_dimensions[col].width = 10
        for r in range(1, 61):
            ws.row_dimensions[r].height = 14
        ws.page_setup.paperSize = ws.PAPERSIZE_A4
        ws.page_setup.orientation = "portrait"
        ws.sheet_properties.pageSetUpPr.fitToPage = True
        ws.page_setup.fitToWidth = 1
        ws.page_setup.fitToHeight = 1
        return wb, ws

    def lines(ws, row: int, count: int, chars: int, col: str = "A") -> int:
        for _ in range(count):
            ws[f"{col}{row}"] = body_line(rng, chars)
            row += 1
        return row

    def chart(wb, f: Figure, anchor: str, w_cm: float, h_cm: float, legend: str, slanted: bool = False):
        data = wb.create_sheet(f"Data{f.number}")
        data.sheet_state = "hidden"
        data.append([""] + f.series)
        for i, c in enumerate(f.categories):
            data.append([c, f.values[0][i], f.values[1][i]])
        ch = BarChart() if f.kind == "bar" else LineChart()
        n = len(f.categories)
        ch.add_data(Reference(data, min_col=2, min_row=1, max_col=3, max_row=n + 1), titles_from_data=True)
        ch.set_categories(Reference(data, min_col=1, min_row=2, max_row=n + 1))
        if f.kind == "line":
            # With symbols: lines alone are thin strokes, which give no region at all.
            for s in ch.series:
                s.marker.symbol = "circle"
                s.marker.size = 6
        ch.title = f.title
        ch.x_axis.title = f.x_title
        ch.y_axis.title = f.y_title
        ch.x_axis.delete = False
        ch.y_axis.delete = False
        ch.y_axis.numFmt = NumFmt(formatCode=f'"{f.token} "0', sourceLinked=False)
        ch.y_axis.scaling.min = 0
        ch.y_axis.scaling.max = f.ticks[-1]
        ch.y_axis.majorUnit = f.ticks[1]
        # An explicit angle, also when level: crowded labels Calc slants by itself leave the text
        # layer (they are drawn as outlines), and nothing could be checked.
        ch.x_axis.txPr = RichText(
            bodyPr=RichTextProperties(rot=-2_700_000 if slanted else 0, vert="horz"),
            p=[Paragraph(pPr=ParagraphProperties(defRPr=CharacterProperties()), endParaRPr=CharacterProperties())],
        )
        ch.legend.position = legend
        # No border round the chart area: the charts of the problem had none.
        ch.graphical_properties = GraphicalProperties(ln=LineProperties(noFill=True))
        ch.width = w_cm
        ch.height = h_cm
        wb["Page"].add_chart(ch, anchor)

    made: list[Path] = []
    books: list[tuple[Path, str, list[Figure]]] = []
    for layout in LAYOUTS:
        used: set[str] = set()
        wb, ws = sheet()
        figs: list[Figure]
        if layout in ("legend-right", "turned-page", "turned-labels", "legend-below"):
            f = new_figure(rng, 1, used)
            lines(ws, 1, 4, 80)
            legend = "b" if layout == "legend-below" else "r"
            chart(wb, f, "A7", 15, 10 if legend == "b" else 9, legend, slanted=layout == "turned-labels")
            lines(ws, 32, 5, 80)
            figs = [f]
        elif layout == "side-by-side":
            figs = [new_figure(rng, 1, used, small=True), new_figure(rng, 2, used, small=True)]
            lines(ws, 1, 5, 80)
            # About 11 % of the page each, as in the playbook: narrower columns and margins, so
            # that both fit side by side without the page being scaled down.
            for col in "ABCDEFGH":
                ws.column_dimensions[col].width = 9
            ws.page_margins.left = ws.page_margins.right = 0.3
            chart(wb, figs[0], "A8", 9, 9.2, "b")
            chart(wb, figs[1], "F8", 9, 9.2, "b")
            lines(ws, 29, 6, 80)
        elif layout == "between-paragraphs":
            f = new_figure(rng, 1, used)
            lines(ws, 1, 10, 80)
            chart(wb, f, "A12", 15, 8, "r")
            lines(ws, 29, 10, 80)
            figs = [f]
        elif layout == "two-columns":
            f = new_figure(rng, 1, used, small=True)
            lines(ws, 1, 50, 40)
            chart(wb, f, "E14", 7.6, 7.4, "b")
            figs = [f]
        else:  # long-caption
            f = new_figure(rng, 1, used)
            lines(ws, 1, 3, 80)
            chart(wb, f, "A5", 15, 9, "r")
            row = 27
            for line in caption_lines(rng, 1, 82, 4):
                ws[f"A{row}"] = line
                row += 1
            lines(ws, row + 2, 6, 80)
            figs = [f]
        xlsx = src / f"{layout}-1.xlsx"
        wb.save(xlsx)
        books.append((xlsx, layout, figs))

    # A table with lines, and no chart.
    wb, ws = sheet()
    thin = Side(style="thin")
    for r in range(3, 33):
        for c in range(1, 6):
            cell = ws.cell(row=r, column=c, value=rng.choice(BODY_WORDS))
            cell.border = Border(left=thin, right=thin, top=thin, bottom=thin)
    lines(ws, 1, 1, 60)
    xlsx = src / "control-table-1.xlsx"
    wb.save(xlsx)
    books.append((xlsx, "control-table", []))

    profile = Path(os.environ.get("HOME", "/tmp")) / ".lo-eval"
    subprocess.run(
        ["soffice", "--headless", f"-env:UserInstallation=file://{profile}", "--convert-to", "pdf", "--outdir", str(out)]
        + [str(b[0]) for b in books],
        check=True,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    for xlsx, layout, figs in books:
        pdf = out / f"{xlsx.stem}.pdf"
        if not pdf.exists():
            sys.exit(f"LibreOffice made no {pdf}")
        write_manifest(pdf, "calc", layout, figs)
        made.append(pdf)
    return made


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("out", type=Path)
    parser.add_argument("--seed", type=int, required=True)
    args = parser.parse_args()

    root = args.out.resolve() / f"seed-{args.seed}"
    if root.exists():
        shutil.rmtree(root)
    root.mkdir(parents=True)
    (root / "words.json").write_text(json.dumps({"body": BODY_WORDS, "label": LABEL_WORDS}, indent=2) + "\n")

    for generator in ("matplotlib", "calc"):
        (root / generator).mkdir()
    made = matplotlib_figures(root / "matplotlib", random.Random(args.seed * 1000 + 1))
    made += calc_figures(root / "calc", random.Random(args.seed * 1000 + 2))

    # pdf-lib draws its own figures, and turns the pages of the others' turned-page layout.
    turn = [str(p) for p in made if p.stem.startswith("turned-page-")]
    subprocess.run(
        ["pnpm", "-s", "-C", str(REPO / "pdf"), "eval:figures", str(root / "pdf-lib"), "--seed", str(args.seed), "--turn", *turn],
        check=True,
    )
    print(f"seed {args.seed}: {root}")


if __name__ == "__main__":
    main()
