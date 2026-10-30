using System.Globalization;
using Rampside.Core;
using Rampside.Core.Aircraft;
using Rampside.Core.Aircraft.Pmdg;
using Rampside.Core.FlightPlans;
using Rampside.Core.Orchestration;
using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.App;

/// <summary>Text commands for driving Rampside until the in-sim UI exists.</summary>
internal sealed class ConsoleShell
{
    private readonly ISimBridge _sim;
    private readonly FakeSimBridge? _fake;
    private readonly Func<GroundServiceOrchestrator?> _orchestrator;
    private readonly IPmdgLink? _pmdg;
    private readonly IGroundVehicleSpawner? _spawner;
    // What to print when a catalog of that kind arrives: a filter on the titles (null = all of them).
    private readonly Dictionary<SimObjectKind, Func<string, bool>?> _catalogFilters = new();

    public ConsoleShell(ISimBridge sim, FakeSimBridge? fake, Func<GroundServiceOrchestrator?> orchestrator, IPmdgLink? pmdg)
    {
        _sim = sim;
        _fake = fake;
        _orchestrator = orchestrator;
        _pmdg = pmdg;
        _spawner = sim as IGroundVehicleSpawner;
        if (_spawner is not null)
        {
            _spawner.CatalogReceived += (_, catalog) => PrintCatalog(catalog);
        }
    }

    /// <summary>Runs one command line. Returns false when the user wants to quit.</summary>
    public bool Execute(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return true;
        }

        var command = parts[0].ToLowerInvariant();
        switch (command)
        {
            case "quit" or "exit":
                return false;
            case "help" or "?":
                PrintHelp();
                return true;
            case "status" or "s":
                PrintStatus();
                return true;
            case "pmdg":
                PrintPmdg();
                return true;
            case "pmdgevent" when parts.Length > 2:
                // e.g. "pmdgevent EVT_CDU_L_MENU 0x00200000" (parameter decimal or 0x hex), for trying out PMDG events.
                var parameter = parts[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? uint.Parse(parts[2][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : uint.Parse(parts[2], CultureInfo.InvariantCulture);
                Console.WriteLine(_pmdg?.SendEvent(parts[1], parameter) == true ? $"  Sent {parts[1]} ({parameter:X8})." : $"! Cannot send {parts[1]}.");
                return true;
            case "cdukey" when parts.Length > 1:
                PressCduKey(parts);
                return true;
            case "nearby":
                PrintNearby();
                return true;
            case "cdu":
                PrintCdu(parts.Length > 1 && int.TryParse(parts[1], out var cdu) ? cdu : 0);
                return true;
            case "vehicles" when _spawner is not null:
                RequestCatalog(SimObjectKind.Ground, parts.Length > 1 ? Contains(parts[1]) : null);
                return true;
            case "simobjects" when _spawner is not null:
                if (parts.Length < 2 || ParseKind(parts[1]) is not { } kind)
                {
                    Console.WriteLine("! Usage: simobjects <all|ground|animal|aircraft|helicopter|boat|balloon|avatar> [filter]");
                    return true;
                }

                RequestCatalog(kind, parts.Length > 2 ? Contains(parts[2]) : null);
                return true;
            case "people" when _spawner is not null:
                // SimConnect has no type for people: list all animals, and everything that sounds like a person.
                RequestCatalog(SimObjectKind.Animal, null);
                RequestCatalog(SimObjectKind.All, LooksLikePerson);
                return true;
            case "spawn" when _spawner is not null:
                Spawn(parts);
                return true;
            case "despawn" when _spawner is not null:
                _spawner.RemoveSpawnedVehicles();
                return true;
            case "power" when _pmdg is FakePmdgLink fakePmdg:
                var on = parts.Length < 2 || parts[1] != "off";
                fakePmdg.SetDcPower(on);
                Console.WriteLine($"  (fake) DC power {(on ? "on" : "off")}.");
                return true;
            case "brake" when _fake is not null:
                _fake.Update(s => s with { ParkingBrakeSet = !s.ParkingBrakeSet });
                Console.WriteLine($"  (fake) Parking brake {(_fake.State.ParkingBrakeSet ? "set" : "released")}.");
                return true;
            case "engines" when _fake is not null:
                _fake.Update(s => s with { EnginesRunning = !s.EnginesRunning });
                Console.WriteLine($"  (fake) Engines {(_fake.State.EnginesRunning ? "running" : "off")}.");
                return true;
        }

        var orchestrator = _orchestrator();
        if (orchestrator is null)
        {
            Console.WriteLine("! Waiting for the simulator and the user aircraft.");
            return true;
        }

        PrintResult(Dispatch(orchestrator, command, parts));
        return true;
    }

    /// <summary>
    /// "ofp" or "ofp force" load the latest SimBrief plan; returns whether forced, or null for any other line.
    /// Handled outside the shell because it waits for the network.
    /// </summary>
    public static bool? ParseOfpLoad(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !parts[0].Equals("ofp", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return parts.Length switch
        {
            1 => false,
            2 when parts[1].Equals("force", StringComparison.OrdinalIgnoreCase) => true,
            _ => null,
        };
    }

    public static void PrintResult(CommandResult result)
    {
        var lines = result.Message.Split(Environment.NewLine);
        Console.WriteLine(result.Accepted ? $"> {lines[0]}" : $"! {lines[0]}");
        foreach (var line in lines.Skip(1))
        {
            Console.WriteLine($"  {line}");
        }
    }

    private static CommandResult Dispatch(GroundServiceOrchestrator o, string command, string[] parts)
    {
        var argument = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;
        switch (command)
        {
            case "refuel":
                if (argument is null)
                {
                    return o.RequestRefuel();
                }

                return double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out var kg)
                    ? o.RequestRefuel(kg)
                    : CommandResult.Rejected("Usage: refuel [total kg]");
            case "ofp":
                return argument switch
                {
                    "show" => o.FlightPlan is { } plan
                        ? CommandResult.Ok($"{FlightPlanFormat.Title(plan)}: {FlightPlanFormat.Load(plan)}.")
                        : CommandResult.Rejected("No flight plan loaded. Type 'ofp' to load the latest SimBrief plan."),
                    "clear" => o.ClearFlightPlan(),
                    _ => CommandResult.Rejected("Usage: ofp [force|show|clear]"),
                };
            case "board":
                if (argument is null)
                {
                    return o.RequestBoarding();
                }

                return int.TryParse(argument, out var passengers)
                    ? o.RequestBoarding(passengers)
                    : CommandResult.Rejected("Usage: board [passengers]");
            case "deboard":
                return o.RequestDeboarding();
            case "jetway":
                return o.ToggleJetway();
            case "stairs":
                return o.ToggleStairs();
            case "gpu":
                return o.ToggleGpu();
            case "taxi":
                return CommandResult.Ok(TaxiReport(o));
            case "push":
                return argument switch
                {
                    null => OfferPushback(o),
                    "manual" => o.RequestPushback(),
                    "left" => o.SteerPushback(PushbackSteering.TailLeft),
                    "right" => o.SteerPushback(PushbackSteering.TailRight),
                    "straight" => o.SteerPushback(PushbackSteering.Straight),
                    "stop" => o.StopPushback(),
                    _ when int.TryParse(argument, out var number) => PushOption(o, number),
                    _ when Compass.TryParse(string.Concat(parts.Skip(1)), out var facing) => o.RequestPushback(facing),
                    _ => CommandResult.Rejected("Usage: push [<number>|<direction>|manual|left|right|straight|stop]"),
                };
            case "abort":
                return argument is not null ? o.Abort(argument) : CommandResult.Rejected("Usage: abort <refuel|passengers|pushback>");
            case "doors":
                if (argument == "close")
                {
                    return o.CloseAllDoors();
                }

                return CommandResult.Ok(DoorSummary(o));
            case "door":
                var action = parts.Length > 2 ? parts[2].ToLowerInvariant() : null;
                if (argument is null || ParseDoor(argument) is not { } door || action is not ("open" or "close"))
                {
                    return CommandResult.Rejected("Usage: door <l1|r1|l2|r2|cargo1|cargo2|airstair> <open|close>");
                }

                return o.SetDoor(door, action == "open");
            default:
                return CommandResult.Rejected($"Unknown command '{command}'. Type 'help'.");
        }
    }

    /// <summary>Diagnostics for planned pushbacks: the options and the taxi lines near the aircraft.</summary>
    private static string TaxiReport(GroundServiceOrchestrator o)
    {
        var state = o.State;
        if (state.Stand?.Taxiways is not { } taxiways)
        {
            return "No stand or no taxiway data.";
        }

        var lines = new List<string>
        {
            $"Options: {(o.PushbackOptions() is { Count: > 0 } options ? string.Join(", ", options.Select(x => $"{x.Label} ({x.LengthMeters:F0} m)")) : "none")}",
            o.GsxStand is { } gsx
                ? $"GSX: [{gsx.Stand.Section}] in {gsx.ProfileName}, {gsx.Stand.PushbackTargets.Count} targets, aircraft "
                    + $"{StandLocator.DistanceMeters(state.Latitude, state.Longitude, gsx.StopPosition.Latitude, gsx.StopPosition.Longitude):F1} m from its stop position"
                    + (gsx.StopOffset is { } offset ? $" ({offset.Meters:F1} m short of the profile's for {offset.Aircraft}, {offset.Function})" : string.Empty)
                : "GSX: no profile stand",
            "Paths within 120 m (forward/right of the aircraft in metres, negative forward = behind):",
        };
        foreach (var path in taxiways.Paths)
        {
            if (path.Start < 0 || path.Start >= taxiways.Points.Count || path.End < 0)
            {
                continue;
            }

            var a = taxiways.Points[path.Start];
            var (af, ar) = Geo.Relative(state.Latitude, state.Longitude, state.HeadingTrueDeg, a.Latitude, a.Longitude);
            var endText = "parking " + path.End;
            var near = Math.Sqrt(af * af + ar * ar) < 120;
            if (path.Type != TaxiPathType.Parking && path.End < taxiways.Points.Count)
            {
                var b = taxiways.Points[path.End];
                var (bf, br) = Geo.Relative(state.Latitude, state.Longitude, state.HeadingTrueDeg, b.Latitude, b.Longitude);
                endText = $"({bf:F0}/{br:F0})";
                near |= Math.Sqrt(bf * bf + br * br) < 120;
            }

            if (near)
            {
                lines.Add($"    {path.Type,-8} {(path.Name.Length > 0 ? path.Name : "-"),-6} ({af:F0}/{ar:F0}) -> {endText}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Like GSX: offer the known pushbacks first; without any the tug is steered manually.</summary>
    private static CommandResult OfferPushback(GroundServiceOrchestrator o)
    {
        var options = o.PushbackOptions();
        if (options.Count == 0)
        {
            return o.RequestPushback();
        }

        var choices = options.Select((x, i) => $"  push {i + 1}  {x.Label}, {x.LengthMeters:F0} m"
            + (x.TowMeters > 0 ? $" (push and pull, towed forwards {x.TowMeters:F0} m)" : string.Empty)
            + $", nose ends {x.FinalHeadingDeg:F0}°");
        var source = options[0].Source is { } from ? $" (from {from})" : string.Empty;
        return CommandResult.Ok($"Which way?{source}{Environment.NewLine}{string.Join(Environment.NewLine, choices)}{Environment.NewLine}  push manual");
    }

    /// <summary>"push 2": the second option as listed by <see cref="OfferPushback"/>.</summary>
    private static CommandResult PushOption(GroundServiceOrchestrator o, int number)
    {
        var options = o.PushbackOptions();
        return number >= 1 && number <= options.Count
            ? o.RequestPlannedPushback(options[number - 1].Label)
            : CommandResult.Rejected(options.Count == 0 ? "No pushback options here. Use push manual." : $"Choose 1 to {options.Count}.");
    }

    private void PrintHelp()
    {
        Console.WriteLine("""
          Commands:
            status                     aircraft and service overview
            jetway | stairs | gpu      connect / disconnect equipment
            ofp [force]                load the latest SimBrief plan (force: despite
                                       a different aircraft type or airport)
            ofp show | clear           show / remove the loaded flight plan
            refuel [kg]                refuel (or defuel) to a total fuel load
                                       (default: block fuel of the flight plan)
            board [pax]                board passengers (default: flight plan, else full)
            deboard                    deboard passengers
            push                       call the pushback tug (shows the directions)
            push <number>              pushback as listed by push (GSX profile targets
                                       or planned onto the taxi line)
            push <direction>           first option facing there, e.g. push nw
            push manual                pushback steered by hand
            push left|right|straight   steer: direction the tail moves (ends a planned push)
            push stop                  stop the pushback
            abort <service>            abort refuel, passengers or pushback
            doors                      door states (doors close: close all)
            door <door> open|close     l1 r1 l2 r2 cargo1 cargo2 airstair
            vehicles [filter]          list the sim's ground vehicles
            simobjects <type> [filter] list SimObjects of one type: all ground animal
                                       aircraft helicopter boat balloon avatar
            people                     look for spawnable people (animals, plus all
                                       titles that sound like a person)
            spawn demo                 park a few default vehicles around the aircraft
            spawn <title> <fwd> <right> [hdg]
                                       spawn a vehicle <fwd>/<right> metres from the
                                       aircraft, hdg relative to the aircraft heading
            despawn                    remove all spawned vehicles
            pmdg                       PMDG SDK connection details
            cdu [0|1]                  show a PMDG CDU screen
            cdukey <key>               press a key on the left PMDG CDU (L1..R6, MENU, ...)
            nearby                     objects around the aircraft (stairs, GPU, ...)
            quit
          """);
        if (_fake is not null)
        {
            Console.WriteLine("""
              Fake sim only:
                brake                      toggle parking brake
                engines                    toggle engines running
                power on|off               DC power of the fake PMDG (door lights)
              """);
        }
    }

    /// <summary>Vehicles for 'spawn demo', placed around a narrowbody. Offsets in metres from the aircraft reference point.</summary>
    /// Headings are relative to the aircraft: 90 points to its right, 180 faces the nose.
    private static readonly (string Title, double ForwardM, double RightM, double HeadingDeg)[] DemoVehicles =
    {
        ("ASO_Catering_Truck_01", 14, 7, 90),     // R1 door, box backed up to the fuselage
        ("Aso_Baggage_Loader_01", 9, 6, -70),     // forward cargo door, belt up to the door
        ("ASO_Pushback_White", 22, 0, 180),       // in front of the nose gear, facing the aircraft
    };

    /// <summary>Title parts that hint at a person (ground crew, pedestrians, passengers).</summary>
    private static readonly string[] PersonHints =
    {
        "human", "person", "people", "pedestrian", "passenger", "worker", "crew", "marshal", "wingwalk", "walker",
        "character", "avatar", "npc", "staff", "agent", "mechanic", "pilot", "ramp_", "male", "female",
    };

    private static bool LooksLikePerson(string title) =>
        PersonHints.Any(h => title.Contains(h, StringComparison.OrdinalIgnoreCase));

    private static Func<string, bool> Contains(string text) =>
        title => title.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static SimObjectKind? ParseKind(string text) => text.ToLowerInvariant() switch
    {
        "all" => SimObjectKind.All,
        "ground" => SimObjectKind.Ground,
        "animal" or "animals" => SimObjectKind.Animal,
        "aircraft" or "plane" => SimObjectKind.Aircraft,
        "helicopter" or "heli" => SimObjectKind.Helicopter,
        "boat" or "boats" => SimObjectKind.Boat,
        "balloon" => SimObjectKind.HotAirBalloon,
        "avatar" => SimObjectKind.UserAvatar,
        _ => null,
    };

    private void RequestCatalog(SimObjectKind kind, Func<string, bool>? filter)
    {
        lock (_catalogFilters) _catalogFilters[kind] = filter;
        Console.WriteLine($"  Asking the simulator for its SimObjects of type {kind}...");
        _spawner!.RequestCatalog(kind);
    }

    private void PrintCatalog(SimObjectCatalog catalog)
    {
        Func<string, bool>? filter;
        lock (_catalogFilters) filter = _catalogFilters.GetValueOrDefault(catalog.Kind);
        var shown = catalog.Titles.Where(t => filter is null || filter(t)).ToList();
        Console.WriteLine($"  {catalog.Kind}: {shown.Count} of {catalog.Titles.Count} SimObject(s):");
        foreach (var title in shown)
        {
            Console.WriteLine($"    {title}");
        }
    }

    private void Spawn(string[] parts)
    {
        var state = _sim.State;
        if (!_sim.IsConnected || !state.OnGround)
        {
            Console.WriteLine("! Spawning needs a connected sim and the aircraft on the ground.");
            return;
        }

        if (parts.Length == 2 && parts[1].Equals("demo", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var vehicle in DemoVehicles)
            {
                SpawnRelative(state, vehicle.Title, vehicle.ForwardM, vehicle.RightM, vehicle.HeadingDeg);
            }

            return;
        }

        static bool Number(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        if (parts.Length < 4 || !Number(parts[2], out var forward) || !Number(parts[3], out var right))
        {
            Console.WriteLine("! Usage: spawn demo | spawn <title> <fwd m> <right m> [heading deg]");
            return;
        }

        var heading = 0.0;
        if (parts.Length > 4 && !Number(parts[4], out heading))
        {
            Console.WriteLine("! Heading must be a number.");
            return;
        }

        SpawnRelative(state, parts[1], forward, right, heading);
    }

    private void SpawnRelative(AircraftState state, string title, double forwardM, double rightM, double headingDeg)
    {
        var (lat, lon) = Rampside.Core.Geo.Offset(state.Latitude, state.Longitude, state.HeadingTrueDeg, forwardM, rightM);
        _spawner!.SpawnGroundVehicle(title, lat, lon, Rampside.Core.Geo.NormalizeHeading(state.HeadingTrueDeg + headingDeg));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Spawning {title} at {forwardM:F0} m fwd, {rightM:F0} m right"));
    }

    private static DoorId? ParseDoor(string text) => text switch
    {
        "l1" or "fwdl" => DoorId.ForwardLeft,
        "r1" or "fwdr" => DoorId.ForwardRight,
        "l2" or "aftl" => DoorId.AftLeft,
        "r2" or "aftr" => DoorId.AftRight,
        "cargo1" or "fwdcargo" => DoorId.CargoForward,
        "cargo2" or "aftcargo" => DoorId.CargoAft,
        "airstair" => DoorId.Airstair,
        _ => Enum.TryParse<DoorId>(text, ignoreCase: true, out var door) ? door : null,
    };

    private static string DoorSummary(GroundServiceOrchestrator o)
    {
        if (o.Adapter.Doors.Count == 0)
        {
            return $"The {o.Adapter.Name} adapter does not control doors.";
        }

        return string.Join(", ", o.Adapter.Doors.Select(d => $"{GroundServiceOrchestrator.DoorName(d)}: {o.Adapter.GetDoorState(d)}"));
    }

    private void PrintPmdg()
    {
        if (_pmdg is null)
        {
            Console.WriteLine("  PMDG SDK not set up (header not found or fake mode without --fake-pmdg).");
            return;
        }

        Console.WriteLine($"  {_pmdg.Diagnostics}");
        Console.WriteLine($"  Data: {(_pmdg.HasData ? "receiving" : _pmdg.Problem)}");
        if (!_pmdg.HasData)
        {
            return;
        }

        var buses = string.Join(" ", Enumerable.Range(0, 16).Select(i => _pmdg.GetBool("ELEC_BusPowered", i) == true ? "1" : "0"));
        Console.WriteLine($"  DC/AC buses powered: {buses}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  Fuel (PMDG): L {_pmdg.GetNumber("FUEL_QtyLeft"):F0}  C {_pmdg.GetNumber("FUEL_QtyCenter"):F0}  R {_pmdg.GetNumber("FUEL_QtyRight"):F0} lbs"));
        Console.WriteLine($"  Ground connection possible: {_pmdg.GetBool("GroundConnAvailable")}, weights in kg: {_pmdg.GetBool("WeightInKg")}");
        Console.WriteLine($"  GRD POWER AVAILABLE light: {_pmdg.GetBool(Pmdg737.GroundPowerIndicator)}, GRD PWR switch: {_pmdg.GetBool("ELEC_GrdPwrSw")}");
        var lit = Pmdg737.DefaultDoorMap.Where(d => _pmdg.GetBool(d.Indicator) == true).Select(d => d.Indicator);
        Console.WriteLine($"  Door lights on: {string.Join(", ", lit.DefaultIfEmpty("none"))}");
    }

    /// <summary>
    /// "cdukey R5" clicks R5 on the left CDU; "cdukey MENU right 2" holds MENU on the right CDU for 2 s
    /// (left button down, then left button release).
    /// </summary>
    private void PressCduKey(string[] parts)
    {
        var right = parts.Skip(2).Any(p => p.Equals("right", StringComparison.OrdinalIgnoreCase) || p.Equals("r", StringComparison.OrdinalIgnoreCase));
        var hold = parts.Skip(2).Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : (double?)null)
            .FirstOrDefault(s => s is not null);
        var key = $"EVT_CDU_{(right ? "R" : "L")}_{parts[1].ToUpperInvariant()}";
        if (_pmdg?.SendEvent(key, PmdgCduDriver.KeyParameter) != true)
        {
            Console.WriteLine($"! Cannot send {key}.");
            return;
        }

        if (hold is not { } seconds)
        {
            Console.WriteLine($"  Sent {key}.");
            return;
        }

        Console.WriteLine($"  Holding {key} for {seconds} s...");
        var link = _pmdg;
        _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ =>
        {
            link.SendEvent(key, PmdgCduDriver.KeyReleaseParameter);
            Console.WriteLine($"  Released {key}.");
        }, TaskScheduler.Default);
    }

    private void PrintNearby()
    {
        var state = _sim.State;
        if (state.NearbyObjects.Count == 0)
        {
            Console.WriteLine("  Nothing within 80 m.");
            return;
        }

        foreach (var o in state.NearbyObjects
            .Select(o => (Object: o, Pos: Geo.Relative(state.Latitude, state.Longitude, state.HeadingTrueDeg, o.Latitude, o.Longitude)))
            .OrderBy(x => Math.Abs(x.Pos.ForwardM) + Math.Abs(x.Pos.RightM)))
        {
            var heading = Geo.HeadingDifference(state.HeadingTrueDeg, o.Object.HeadingTrueDeg);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {o.Pos.ForwardM,6:F1} fwd {o.Pos.RightM,6:F1} right  hdg {heading,4:F0}  #{o.Object.ObjectId}  {o.Object.Title}"));
        }
    }

    private void PrintCdu(int index)
    {
        var screen = _pmdg?.GetCdu(index);
        if (screen is null)
        {
            Console.WriteLine($"  No CDU {index} data. Add EnableCDUBroadcast.{index}=1 under [SDK] in 737_Options.ini.");
            return;
        }

        Console.WriteLine($"  CDU {index} ({(screen.Powered ? "powered" : "off")})");
        Console.WriteLine("  +" + new string('-', 24) + "+");
        for (var row = 0; row < screen.Lines.Count; row++)
        {
            // Second column: colour per cell (W C G M A R), lower case = small font.
            Console.WriteLine($"  |{screen.Lines[row]}|  {screen.Style(row)}");
        }

        Console.WriteLine("  +" + new string('-', 24) + "+");
    }

    private void PrintStatus()
    {
        var state = _sim.State;
        if (!_sim.IsConnected)
        {
            Console.WriteLine("  Not connected to the simulator.");
            return;
        }

        var orchestrator = _orchestrator();
        Console.WriteLine($"  Aircraft: {state.Title} (profile: {orchestrator?.Profile.Name ?? "-"}, adapter: {orchestrator?.Adapter.Name ?? "-"})");
        if (orchestrator is { Adapter.IsReady: false })
        {
            Console.WriteLine($"  ! {orchestrator.Adapter.NotReadyReason}");
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  On ground: {YesNo(state.OnGround)}  Brake: {(state.ParkingBrakeSet ? "set" : "released")}  Engines: {(state.EnginesRunning ? "running" : "off")}  GS: {state.GroundSpeedKnots:F1} kt  HDG: {state.HeadingTrueDeg:F0}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  Fuel: {state.TotalFuelKg:F0} / {state.FuelCapacityKg:F0} kg"));
        Console.WriteLine(state.Stand is { } stand
            ? $"  Stand: {stand.AirportIcao} {stand.Name}, jetway: {(stand.Jetway is { } j ? $"{j.Status}{(j.AttachedToUser ? " (to us)" : string.Empty)} [raw {j.RawStatus}]" : "none")}, taxi paths: {stand.Taxiways?.Paths.Count ?? 0}"
            : "  Stand: unknown");

        if (orchestrator is null)
        {
            return;
        }

        Console.WriteLine(orchestrator.FlightPlan is { } plan
            ? $"  Flight plan: {FlightPlanFormat.Title(plan)}: {FlightPlanFormat.Load(plan)}"
            : "  Flight plan: none (type 'ofp')");
        Console.WriteLine($"  Passengers on board: {orchestrator.Passengers.PassengersOnBoard:F0}");
        foreach (var service in orchestrator.Services)
        {
            var progress = service.Progress is { } p ? $" ({p:P0})" : string.Empty;
            Console.WriteLine($"  {service.DisplayName,-14}{service.Status,-11}{service.Detail}{progress}");
        }
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
}
