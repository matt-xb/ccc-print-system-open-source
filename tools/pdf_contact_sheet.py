from __future__ import annotations

import math
import re
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageOps


def page_number(path: Path) -> int:
    match = re.search(r"(\d+)$", path.stem)
    return int(match.group(1)) if match else 0


def build_contact_sheet(
    images: list[Path],
    output_path: Path,
    first_page: int,
) -> None:
    columns = 3
    thumbnail_width = 360
    label_height = 34
    margin = 18
    gap = 18

    thumbnails: list[Image.Image] = []
    for path in images:
        with Image.open(path) as image:
            copy = image.convert("RGB")
            copy.thumbnail((thumbnail_width, 520), Image.Resampling.LANCZOS)
            thumbnails.append(copy)

    cell_width = thumbnail_width + margin * 2
    cell_height = max(image.height for image in thumbnails) + label_height + margin * 2
    rows = math.ceil(len(thumbnails) / columns)
    sheet = Image.new(
        "RGB",
        (
            columns * cell_width + (columns - 1) * gap,
            rows * cell_height + (rows - 1) * gap,
        ),
        "white",
    )
    draw = ImageDraw.Draw(sheet)

    for index, thumbnail in enumerate(thumbnails):
        row, column = divmod(index, columns)
        cell_x = column * (cell_width + gap)
        cell_y = row * (cell_height + gap)
        x = cell_x + (cell_width - thumbnail.width) // 2
        y = cell_y + margin + label_height
        sheet.paste(ImageOps.expand(thumbnail, border=1, fill="#C6CBD4"), (x, y))
        draw.text(
            (cell_x + margin, cell_y + margin),
            f"Page {first_page + index}",
            fill="#1C2434",
        )

    output_path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(output_path, quality=92)


def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: pdf_contact_sheet.py <render-root> <output-dir>")
        return 2

    render_root = Path(sys.argv[1])
    output_dir = Path(sys.argv[2])

    for pdf_folder in sorted(path for path in render_root.iterdir() if path.is_dir()):
        pages = sorted(pdf_folder.glob("page-*.png"), key=page_number)
        for chunk_index, start in enumerate(range(0, len(pages), 12), start=1):
            chunk = pages[start : start + 12]
            output_path = output_dir / f"{pdf_folder.name}-sheet-{chunk_index:02}.jpg"
            build_contact_sheet(chunk, output_path, start + 1)
            print(output_path)

    return 0


raise SystemExit(main())

