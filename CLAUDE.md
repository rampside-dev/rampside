# Rampside

Ground services for MSFS 2024 as an external C# app (.NET 10) talking to the sim over SimConnect.
Architecture: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). User-facing docs: [README.md](README.md).

## Build and test

```bash
dotnet build Rampside.slnx
dotnet test
```

The SimConnect bridge only builds with the MSFS 2024 SDK (`MSFS2024_SDK`, or pass
`-p:MsfsSdkRoot="C:\MSFS 2024 SDK"`); without it everything else builds and the app runs with `--fake`.
`start-rampside.bat` builds and starts the desktop window. A running `Rampside.exe` locks its build output.

## Releases

Releases are built only by GitHub Actions ([.github/workflows/build.yml](.github/workflows/build.yml)),
never uploaded by hand. To release version X.Y.Z, either:

```bash
git tag vX.Y.Z
```

```bash
git push origin vX.Y.Z
```

or start **Build** with `release_version` set (`gh workflow run build.yml -f release_version=X.Y.Z`); that creates
the tag on the commit it built.

- The version comes from the tag. Do not bump `<Version>` in `Directory.Build.props` for a release; it is only
  the version of local builds.
- Plain `X.Y.Z` only: an MSI version has no suffix, so `v1.0.0-beta` fails the build.
- Only tag what is pushed to `main` and green in CI. The release is built from the tagged commit, so nothing
  uncommitted or unpushed gets in.
- The workflow runs the tests, builds `Rampside-X.Y.Z-x64.msi`, publishes the release "Rampside X.Y.Z" (MSI,
  `SHA256SUMS.txt`, generated notes), then starts the **Website** workflow. That deploy points the download
  buttons on the site at the new MSI and writes the site's release data
  (`.github/scripts/stamp-download-links.py`). Check both runs: `gh run list --limit 4`.
- Never move or delete a tag that has a release: links, checksums and installed copies refer to it.
  Publish a new version instead.
- Release notes come from the merged changes; edit them on GitHub afterwards if needed.

## Installer

- `installer/build.ps1` publishes the desktop app self-contained and builds the MSI with WiX 7
  (`installer/Rampside.Installer.wixproj`, `installer/Package.wxs`). `build-installer.bat` and CI both call it,
  so local and released MSIs are built the same way. Output: `installer\bin\x64\Release\`.
- The WiX 7 EULA (Open Source Maintenance Fee, free below US$10,000 annual revenue) was accepted by the
  project owner; it is set in the wixproj.
- Keep `UpgradeCode` in `Package.wxs` unchanged forever; it is how a new MSI replaces the old one.
- Installer artwork (`installer/art/*.bmp`) and the app icon are generated from `assets/rampside.svg` by
  `tools/icon/build_installer_art.py` and `tools/icon/build_icon.py`.

## SimConnect and the MSFS SDK

- Never commit `SimConnect.dll` or `Microsoft.FlightSimulator.SimConnect.dll` (MSFS SDK EULA); they only ship
  inside the MSI.
- CI downloads the SDK installer from Microsoft and keeps only the two DLLs
  (`.github/scripts/get-simconnect.ps1`, cached per version). For a new SDK, change `MSFS_SDK_VERSION` and
  `MSFS_SDK_SHA256` in `build.yml`; a wrong checksum stops the build with the actual value.

## Website

`web/` is static HTML/CSS, deployed to GitHub Pages by `.github/workflows/pages.yml` on every push to `main`
that touches it. Download buttons must keep their `data-download` attribute so the deploy can link them to
the latest MSI.

## Working in this repository

- Several Claude sessions often work here at the same time. Stage only your own files by path
  (`git add <paths>`), never `git add -A` or `git commit -a`, and check `git status` and `git log` before
  committing or pushing.
- Text files use LF. On Windows, Python's text mode writes CRLF: read and write bytes, or pass `newline="\n"`.
