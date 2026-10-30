#!/usr/bin/env python3
"""Points the website's download buttons straight at the MSI of the latest release and writes latest.json.

Run by the Website workflow on the copy of web/ that gets published:
    stamp-download-links.py <site dir> <release.json>
release.json is GitHub's "latest release" (GET /repos/{owner}/{repo}/releases/latest), or {} when there is none.

In web/ the buttons (<a ... data-download>) link to releases/latest, the release page. That stays the fallback
for local previews and for a repository without a release, so nothing breaks when this script changes nothing.
<span data-release>...</span> gets the version and size, e.g. "Version 0.1.0, 58 MB".

latest.json is what Rampside's update check reads (src/Rampside.Hosting/Updates/ReleaseInfo.cs):
    {"version": "0.2.0", "publishedAt": "...", "notesUrl": "<release page>",
     "msi": {"url": "<MSI download>", "size": 60683752, "sha256": "<hex>"}}
The checksum is GitHub's digest of the uploaded MSI, or the line in the release's SHA256SUMS.txt.
"""
import json
import re
import sys
import urllib.request
from pathlib import Path

DOWNLOAD_LINK = re.compile(r'<a\b[^>]*\bdata-download\b[^>]*>')
RELEASE_INFO = re.compile(r'(<span\b[^>]*\bdata-release\b[^>]*>)(.*?)(</span>)', re.S)
SHA256 = re.compile(r'^[0-9a-f]{64}$')


def msi_sha256(release: dict, msi: dict) -> str | None:
    digest = (msi.get("digest") or "").lower()
    if digest.startswith("sha256:") and SHA256.match(digest[7:]):
        return digest[7:]
    sums = next((a for a in release.get("assets", []) if a.get("name") == "SHA256SUMS.txt"), None)
    if sums is None:
        return None
    with urllib.request.urlopen(sums["browser_download_url"], timeout=30) as response:
        for line in response.read().decode("utf-8").splitlines():
            parts = line.split()
            if len(parts) == 2 and parts[1].lstrip("*") == msi["name"] and SHA256.match(parts[0].lower()):
                return parts[0].lower()
    return None


def write_latest_json(site: Path, release: dict, msi: dict, version: str) -> bool:
    sha256 = msi_sha256(release, msi)
    if sha256 is None:
        print(f"No SHA-256 for {msi['name']}; latest.json not written, so Rampside offers no update.", file=sys.stderr)
        return False
    latest = {
        "version": version,
        "publishedAt": release.get("published_at"),
        "notesUrl": release["html_url"],
        "msi": {"url": msi["browser_download_url"], "size": msi["size"], "sha256": sha256},
    }
    (site / "latest.json").write_text(json.dumps(latest, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"latest.json: Rampside {version}")
    return True


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

    if not write_latest_json(site, release, msi, version):
        return 1

    print(f"{links} download link(s) now point at {url}")
    return 0


if __name__ == "__main__":
    sys.exit(main(Path(sys.argv[1]), Path(sys.argv[2])))
