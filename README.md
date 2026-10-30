<img src="assets/rampside.svg" alt="Rampside logo" width="96">

# Rampside

Open-source ground services for Microsoft Flight Simulator 2024: pushback, boarding and deboarding,
refueling, jetways, stairs and ground power.

Rampside is an independent community project. It is not affiliated with FSDreamTeam/GSX, Microsoft or Asobo.

> **Status: early prototype (0.1).** The service logic is complete and tested against a fake simulator.
> The SimConnect bridge has not been tested in MSFS yet, and there is no in-sim UI: you control Rampside
> from a console window.

## What works today

| Service | Behaviour |
| --- | --- |
| Jetway / stairs / GPU | Connect and disconnect with timing; triggers the sim's own equipment events |
| Refueling | Truck arrival delay, fuel flows at a set rate, wing tanks first then center; defueling too |
| Boarding / deboarding | Passengers per minute, passenger and bag weight written to payload stations |
| Pushback | Tug connect, waits for brake release, steerable push (tail left/right), stop, brake, disconnect |

**PMDG 737:** doors open and close automatically with jetway, stairs and boarding, through the PMDG SDK.
Fuel and payload stay with the aircraft for now. Needs `EnableDataBroadcast=1` in `737_Options.ini`,
see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#pmdg-737).

Rules between services are enforced, e.g. no pushback while a jetway is attached, no boarding without
jetway or stairs, no refueling with engines running.

## Requirements

- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- MSFS 2024 SDK, for the SimConnect connection only. The installer sets `MSFS2024_SDK`; otherwise pass
  `-p:MsfsSdkRoot="C:\path\to\MSFS 2024 SDK"`. Without the SDK everything else builds and the app runs
  with the fake simulator.

## Build and run

**Desktop window:** double-click `start-rampside.bat` in the repository folder. It builds the current code
(Release) and opens the Rampside window; if Rampside is already running, it offers to close it first.
Arguments are passed on, e.g. `start-rampside.bat --fake-pmdg` to try it without MSFS.

Or by hand:

```bash
dotnet build Rampside.slnx
```

```bash
dotnet test
```

Run without MSFS:

```bash
dotnet run --project src/Rampside.App -- --fake
```

Run against MSFS (start the sim first or afterwards, Rampside reconnects on its own):

```bash
dotnet run --project src/Rampside.App
```

Type `help` in the console for all commands. A typical turnaround:

```
jetway
board
refuel 9000
status
jetway
push
push left
push stop
```

In fake mode, `brake` toggles the parking brake and `engines` toggles the engines. `--fake-pmdg` simulates a
PMDG 737 with doors (`doors`, `door l1 open`, `power off`).

## Installer

Double-click `build-installer.bat` (needs the MSFS 2024 SDK). It publishes the desktop app self-contained and
builds `installer\bin\x64\Release\Rampside-<version>-x64.msi` with the [WiX Toolset](https://wixtoolset.org).
Players need nothing else: the MSI brings the .NET runtime, SimConnect, profiles and crew voices, installs to
`C:\Program Files\Rampside` and adds a Start menu entry. Settings stay in `%AppData%\Rampside`.

For a release, raise `<Version>` in [Directory.Build.props](Directory.Build.props); the new MSI then replaces
the installed version. The MSI is not code-signed yet, so Windows SmartScreen warns on first start.

## Aircraft profiles

Per-aircraft settings live in [`profiles/`](profiles/) as JSON (comments allowed). A profile is picked
when its `match` text appears in the aircraft title; otherwise `generic.json` is used. Start from
[`example-narrowbody.json`](profiles/example-narrowbody.json). Details are in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#aircraft-profiles).

## Project layout

```
src/Rampside.Core         service logic, rules, profiles, fake simulator (no MSFS dependency)
src/Rampside.MsfsBridge   SimConnect connection to MSFS 2024
src/Rampside.App          console host
tests/Rampside.Core.Tests unit tests
installer/                WiX project for the MSI (built by build-installer.bat)
profiles/                 aircraft profiles
docs/                     architecture and roadmap
web/                      website (rampside.dev), published to GitHub Pages by .github/workflows/pages.yml
```

## Contributing

Issues and pull requests are welcome. Most needed right now: testing the SimConnect bridge in MSFS 2024 and
aircraft profiles with verified payload station indexes. Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) first.

## License

[MIT](LICENSE)
