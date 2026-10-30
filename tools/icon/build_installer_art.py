"""Builds the installer bitmaps from the Rampside logo (assets/rampside.svg):

    installer/art/dialog.bmp   welcome and finish pages: the left 164 px are the picture, the rest stays white for the text
    installer/art/banner.bmp   top banner of the other pages: white for the title on the left, the logo on the right

WiX's sizes are 493 x 312 and 493 x 58 at 100 % display scale. Windows Installer stretches the bitmap to the
dialog, so it is drawn at SCALE times that size to stay sharp on scaled displays (150 % is common).
Renders with headless Edge or Chrome like build_icon.py.

    python tools/icon/build_installer_art.py
"""

import subprocess
import sys
import tempfile
import time
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_icon import ASSETS, ROOT, find_browser  # noqa: E402

OUT = ROOT / "installer" / "art"
SCALE = 2

# Colors from the website (web/style.css).
APRON, JOINT, TAXI, HULL, CONCRETE = "#17191C", "#101214", "#F2B84B", "#EEF3F5", "#9BA3AA"
FONT = "'Segoe UI Variable Display', 'Segoe UI', sans-serif"


def dialog_html(logo: str) -> str:
    return f"""<!doctype html><html><body style="margin:0;width:493px;height:312px;background:#fff;overflow:hidden">
<div style="position:absolute;left:0;top:0;width:164px;height:312px;background-color:{APRON};
  background-image:linear-gradient({JOINT} 1px, transparent 1px),linear-gradient(90deg,{JOINT} 1px, transparent 1px);
  background-size:41px 41px;background-position:-1px -1px">
  <img src="{logo}" width="96" height="96" style="position:absolute;left:34px;top:38px">
  <div style="position:absolute;left:0;right:0;top:146px;text-align:center;font:700 22px {FONT};color:{HULL};
    letter-spacing:0.2px">Rampside</div>
  <div style="position:absolute;left:12px;right:12px;top:176px;text-align:center;font:600 10.5px/1.35 {FONT};
    color:{CONCRETE}">Ground services for<br>MSFS 2024</div>
  <svg width="164" height="312" viewBox="0 0 164 312" style="position:absolute;left:0;top:0">
    <rect x="58" y="226" width="48" height="7" rx="3.5" fill="{TAXI}"/>
    <path d="M82 240 L82 258 C82 284 120 284 120 320" fill="none" stroke="{TAXI}" stroke-width="5"
      stroke-linecap="round"/>
  </svg>
</div></body></html>"""


def banner_html(logo: str) -> str:
    return f"""<!doctype html><html><body style="margin:0;width:493px;height:58px;background:#fff;overflow:hidden">
<img src="{logo}" width="44" height="44" style="position:absolute;right:9px;top:7px">
</body></html>"""


def render(browser: str, html: str, name: str, width: int, height: int, work: Path) -> Image.Image:
    page = work / f"{name}.html"
    page.write_text(html, encoding="utf-8")
    out = work / f"{name}.png"
    subprocess.run(
        [
            browser,
            "--headless=new",
            "--disable-gpu",
            "--hide-scrollbars",
            f"--force-device-scale-factor={SCALE}",
            f"--user-data-dir={work / 'profile'}",
            f"--window-size={width},{height}",
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
    return Image.open(out).convert("RGB").crop((0, 0, width * SCALE, height * SCALE))


def main() -> None:
    browser = find_browser()
    logo = (ASSETS / "rampside.svg").as_uri()
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        work = Path(tmp)
        for name, html, width, height in (
            ("dialog", dialog_html(logo), 493, 312),
            ("banner", banner_html(logo), 493, 58),
        ):
            image = render(browser, html, name, width, height, work)
            path = OUT / f"{name}.bmp"
            image.save(path, format="BMP")
            print(f"Wrote {path.relative_to(ROOT)} ({image.width} x {image.height})")


if __name__ == "__main__":
    main()
