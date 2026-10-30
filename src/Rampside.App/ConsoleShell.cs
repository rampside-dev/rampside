using System.Globalization;
using Rampside.Core.Aircraft;
using Rampside.Core.Aircraft.Pmdg;
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

    public ConsoleShell(ISimBridge sim, FakeSimBridge? fake, Func<GroundServiceOrchestrator?> orchestrator, IPmdgLink? pmdg)
    {
        _sim = sim;
        _fake = fake;
        _orchestrator = orchestrator;
        _pmdg = pmdg;
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
            case "cdu":
                PrintCdu(parts.Length > 1 && int.TryParse(parts[1], out var cdu) ? cdu : 0);
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

        var result = Dispatch(orchestrator, command, parts);
        Console.WriteLine(result.Accepted ? $"> {result.Message}" : $"! {result.Message}");
        return true;
    }

    private static CommandResult Dispatch(GroundServiceOrchestrator o, string command, string[] parts)
    {
        var argument = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;
        switch (command)
        {
            case "refuel":
                return argument is not null && double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out var kg)
                    ? o.RequestRefuel(kg)
                    : CommandResult.Rejected("Usage: refuel <total kg>");
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
            case "push":
                return argument switch
                {
                    null => o.RequestPushback(),
                    "left" => o.SteerPushback(PushbackSteering.TailLeft),
                    "right" => o.SteerPushback(PushbackSteering.TailRight),
                    "straight" => o.SteerPushback(PushbackSteering.Straight),
                    "stop" => o.StopPushback(),
                    _ => CommandResult.Rejected("Usage: push [left|right|straight|stop]"),
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

    private void PrintHelp()
    {
        Console.WriteLine("""
          Commands:
            status                     aircraft and service overview
            jetway | stairs | gpu      connect / disconnect equipment
            refuel <kg>                refuel (or defuel) to a total fuel load
            board [pax]                board passengers (default: full)
            deboard                    deboard passengers
            push                       call the pushback tug
            push left|right|straight   steer: direction the tail moves
            push stop                  stop the pushback
            abort <service>            abort refuel, passengers or pushback
            doors                      door states (doors close: close all)
            door <door> open|close     l1 r1 l2 r2 cargo1 cargo2 airstair
            pmdg                       PMDG SDK connection details
            cdu [0|1]                  show a PMDG CDU screen
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
        var lit = Pmdg737Adapter.DoorMap.Where(d => _pmdg.GetBool(d.Annunciator) == true).Select(d => d.Annunciator);
        Console.WriteLine($"  Door lights on: {string.Join(", ", lit.DefaultIfEmpty("none"))}");
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
        foreach (var line in screen.Lines)
        {
            Console.WriteLine($"  |{line}|");
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

        if (orchestrator is null)
        {
            return;
        }

        Console.WriteLine($"  Passengers on board: {orchestrator.Passengers.PassengersOnBoard:F0}");
        foreach (var service in orchestrator.Services)
        {
            var progress = service.Progress is { } p ? $" ({p:P0})" : string.Empty;
            Console.WriteLine($"  {service.DisplayName,-14}{service.Status,-11}{service.Detail}{progress}");
        }
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
}
