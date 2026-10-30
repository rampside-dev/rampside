#!/usr/bin/env python3
"""Points the website's download buttons straight at the MSI of the latest release.

Run by the Website workflow on the copy of web/ that gets published:
    stamp-download-links.py <site dir> <release.json>
release.json is GitHub's "latest release" (GET /repos/{owner}/{repo}/releases/latest), or {} when there is none.

In web/ the buttons (<a ... data-download>) link to releases/latest, the release page. That stays the fallback
for local previews and for a repository without a release, so nothing breaks when this script changes nothing.
<span data-release>...</span> gets the version and size, e.g. "Version 0.1.0, 58 MB".
"""
import json
import re
import sys
from pathlib import Path

DOWNLOAD_LINK = re.compile(r'<a\b[^>]*\bdata-download\b[^>]*>')
RELEASE_INFO = re.compile(r'(<span\b[^>]*\bdata-release\b[^>]*>)(.*?)(</span>)', re.S)


def main(site: Path, release_file: Path) -> int:
    release = json.loads(release_file.read_text(encoding="utf-8") or "{}")
    msi = next((a for a in release.get("assets", []) if a.get("name", "").endswith(".msi")), None)
    if msi is None:
        print("No release with an MSI yet; the buttons keep linking to the release page.")
        return 0

    version = release["tag_name"].removeprefix("v")
    url = msi["browser_download_url"]
    size_mb = round(msi["size"] / (1024 * 1024))
    title = f"Rampside {version} for Windows (MSI, {size_mb} MB)"

    def stamp(tag: re.Match) -> str:
        a = re.sub(r'\bhref="[^"]*"', f'href="{url}"', tag.group(0))
        a = re.sub(r'\s*\btitle="[^"]*"', "", a)
        return a[:-1] + f' title="{title}">'

    links = 0
    for page in site.rglob("*.html"):
        html = page.read_text(encoding="utf-8")
        html, n = DOWNLOAD_LINK.subn(stamp, html)
        links += n
        html = RELEASE_INFO.sub(lambda m: f"{m.group(1)}Version {version}, {size_mb} MB{m.group(3)}", html)
        page.write_text(html, encoding="utf-8")

    if links == 0:
        print("No <a data-download> links found in the site; did the markup change?", file=sys.stderr)
        return 1

    print(f"{links} download link(s) now point at {url}")
    return 0


if __name__ == "__main__":
    sys.exit(main(Path(sys.argv[1]), Path(sys.argv[2])))
