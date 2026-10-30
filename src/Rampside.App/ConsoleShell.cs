using System.Globalization;
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

    public ConsoleShell(ISimBridge sim, FakeSimBridge? fake, Func<GroundServiceOrchestrator?> orchestrator)
    {
        _sim = sim;
        _fake = fake;
        _orchestrator = orchestrator;
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
            quit
          """);
        if (_fake is not null)
        {
            Console.WriteLine("""
              Fake sim only:
                brake                      toggle parking brake
                engines                    toggle engines running
              """);
        }
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
        Console.WriteLine($"  Aircraft: {state.Title} (profile: {orchestrator?.Profile.Name ?? "-"})");
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
