"""Builds the Rampside icon (.ico and PNGs at 256, 512 and 1024 px) from the SVG sources in assets/.

Renders each SVG with headless Edge or Chrome (no extra Python packages beyond Pillow),
then scales the render down to each icon size. Sizes up to 24 px use the simplified
assets/rampside-small.svg when it exists, because fine detail turns to mush there.

    python tools/icon/build_icon.py
"""

import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "assets"
ICON_SIZES = [16, 24, 32, 48, 64, 256]
PNG_SIZES = [256, 512, 1024]
SMALL_MAX = 24
RENDER = 1024

BROWSERS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]


def find_browser() -> str:
    for path in BROWSERS:
        if os.path.exists(path):
            return path
    for name in ("msedge", "chrome", "chromium"):
        found = shutil.which(name)
        if found:
            return found
    sys.exit("No Edge or Chrome found to render the SVG.")


def render(browser: str, svg: Path, size: int, work: Path) -> Image.Image:
    page = work / f"{svg.stem}.html"
    page.write_text(
        "<!doctype html><html><body style='margin:0;background:transparent'>"
        f"<img src='{svg.as_uri()}' width='{size}' height='{size}' style='display:block'>"
        "</body></html>",
        encoding="utf-8",
    )
    out = work / f"{svg.stem}.png"
    subprocess.run(
        [
            browser,
            "--headless=new",
            "--disable-gpu",
            "--hide-scrollbars",
            "--force-device-scale-factor=1",
            "--default-background-color=00000000",
            f"--user-data-dir={work / 'profile'}",
            f"--window-size={size},{size}",
            f"--screenshot={out}",
            page.as_uri(),
        ],
        check=True,
        capture_output=True,
    )
    # Edge's launcher can return before the screenshot is written.
    deadline = time.monotonic() + 30
    while not out.exists() and time.monotonic() < deadline:
        time.sleep(0.2)
    time.sleep(0.2)
    return Image.open(out).convert("RGBA").crop((0, 0, size, size))


def main() -> None:
    browser = find_browser()
    big_svg = ASSETS / "rampside.svg"
    small_svg = ASSETS / "rampside-small.svg"

    with tempfile.TemporaryDirectory() as tmp:
        work = Path(tmp)
        big = render(browser, big_svg, RENDER, work)
        small = render(browser, small_svg, RENDER, work) if small_svg.exists() else big

    frames = []
    for size in ICON_SIZES:
        source = small if size <= SMALL_MAX else big
        frames.append(source.resize((size, size), Image.Resampling.LANCZOS))

    ico = ASSETS / "rampside.ico"
    frames[-1].save(ico, format="ICO", sizes=[(s, s) for s in ICON_SIZES], append_images=frames[:-1])
    for size in PNG_SIZES:
        png = big if size == RENDER else big.resize((size, size), Image.Resampling.LANCZOS)
        png.save(ASSETS / f"rampside-{size}.png", optimize=True)
    pngs = ", ".join(f"assets/rampside-{s}.png" for s in PNG_SIZES)
    print(f"Wrote {ico.relative_to(ROOT)} ({', '.join(map(str, ICON_SIZES))} px), {pngs}")


if __name__ == "__main__":
    main()
