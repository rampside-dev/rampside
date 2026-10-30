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

Rules between services are enforced, e.g. no pushback while a jetway is attached, no boarding without
jetway or stairs, no refueling with engines running.

## Requirements

- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- MSFS 2024 SDK, for the SimConnect connection only. The installer sets `MSFS2024_SDK`; otherwise pass
  `-p:MsfsSdkRoot="C:\path\to\MSFS 2024 SDK"`. Without the SDK everything else builds and the app runs
  with the fake simulator.

## Build and run

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

In fake mode, `brake` toggles the parking brake and `engines` toggles the engines.

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
profiles/                 aircraft profiles
docs/                     architecture and roadmap
```

## Contributing

Issues and pull requests are welcome. Most needed right now: testing the SimConnect bridge in MSFS 2024 and
aircraft profiles with verified payload station indexes. Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) first.

## License

[MIT](LICENSE)
