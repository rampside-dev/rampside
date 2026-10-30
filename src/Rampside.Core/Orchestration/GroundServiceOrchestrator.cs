using Rampside.Core.Profiles;
using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Core.Orchestration;

public sealed record CommandResult(bool Accepted, string Message)
{
    public static CommandResult Ok(string message) => new(true, message);
    public static CommandResult Rejected(string message) => new(false, message);
}

/// <summary>
/// Owns all ground services for one aircraft, ticks them and enforces the rules between them
/// (no pushback with a jetway attached, no boarding without cabin access, ...).
/// Every UI (console, EFB app, web) talks to this class.
/// </summary>
public sealed class GroundServiceOrchestrator
{
    private readonly ServiceContext _context;

    public GroundServiceOrchestrator(ISimBridge sim, AircraftProfile profile)
    {
        _context = new ServiceContext(sim, profile);
        Jetway = new ConnectableService("jetway", "Jetway", profile.Jetway, SimEvents.ToggleJetway);
        Stairs = new ConnectableService("stairs", "Stairs", profile.Stairs, SimEvents.ToggleStairs);
        Gpu = new ConnectableService("gpu", "Ground power", profile.Gpu, SimEvents.RequestGroundPower);
        Refueling = new RefuelingService();
        Passengers = new PassengerService();
        Pushback = new PushbackService();
        Services = new GroundService[] { Jetway, Stairs, Gpu, Refueling, Passengers, Pushback };

        foreach (var service in Services)
        {
            service.Changed += (sender, _) => ServiceChanged?.Invoke(this, (GroundService)sender!);
        }
    }

    public event EventHandler<GroundService>? ServiceChanged;

    public AircraftProfile Profile => _context.Profile;
    public AircraftState State => _context.State;
    public IReadOnlyList<GroundService> Services { get; }
    public ConnectableService Jetway { get; }
    public ConnectableService Stairs { get; }
    public ConnectableService Gpu { get; }
    public RefuelingService Refueling { get; }
    public PassengerService Passengers { get; }
    public PushbackService Pushback { get; }

    private bool HasCabinAccess => Jetway.IsConnected || Stairs.IsConnected;

    public void Tick(TimeSpan elapsed)
    {
        if (!_context.Sim.IsConnected)
        {
            return;
        }

        foreach (var service in Services)
        {
            service.Tick(_context, elapsed);
        }
    }

    public CommandResult ToggleJetway() => ToggleConnectable(Jetway, Stairs);

    public CommandResult ToggleStairs() => ToggleConnectable(Stairs, Jetway);

    public CommandResult ToggleGpu() => ToggleConnectable(Gpu, null);

    public CommandResult RequestRefuel(double targetKg)
    {
        if (ParkedCheck() is { } reason)
        {
            return CommandResult.Rejected(reason);
        }

        if (targetKg < 0)
        {
            return CommandResult.Rejected("Fuel amount must be positive.");
        }

        if (State.FuelCapacityKg <= 0)
        {
            return CommandResult.Rejected("The aircraft reports no fuel tanks.");
        }

        if (State.EnginesRunning && !Profile.Fuel.AllowWithEnginesRunning)
        {
            return CommandResult.Rejected("Shut down the engines before refueling.");
        }

        if (Refueling.IsBusy)
        {
            return CommandResult.Rejected("Refueling is already in progress.");
        }

        if (Pushback.IsBusy)
        {
            return CommandResult.Rejected("Not available during pushback.");
        }

        Refueling.Start(_context, targetKg);
        return CommandResult.Ok($"Refueling to {Refueling.TargetKg:F0} kg requested.");
    }

    public CommandResult RequestBoarding(int? passengers = null)
    {
        if (PassengerCheck() is { } reason)
        {
            return CommandResult.Rejected(reason);
        }

        var count = passengers ?? Profile.Passengers.Capacity;
        if (count <= 0)
        {
            return CommandResult.Rejected("Passenger count must be positive.");
        }

        Passengers.StartBoarding(_context, count);
        return CommandResult.Ok($"Boarding {Passengers.TargetPassengers} passengers.");
    }

    public CommandResult RequestDeboarding()
    {
        if (PassengerCheck() is { } reason)
        {
            return CommandResult.Rejected(reason);
        }

        if (Passengers.PassengersOnBoard <= 0)
        {
            return CommandResult.Rejected("No passengers boarded by Rampside in this session.");
        }

        Passengers.StartDeboarding(_context);
        return CommandResult.Ok("Deboarding started.");
    }

    public CommandResult RequestPushback()
    {
        if (ParkedCheck() is { } reason)
        {
            return CommandResult.Rejected(reason);
        }

        if (Pushback.IsBusy)
        {
            return CommandResult.Rejected("Pushback is already in progress.");
        }

        if (Jetway.IsBusy || Stairs.IsBusy)
        {
            return CommandResult.Rejected("Disconnect the jetway and stairs first.");
        }

        if (Gpu.IsBusy)
        {
            return CommandResult.Rejected("Disconnect ground power first.");
        }

        if (Passengers.IsBusy || Refueling.IsBusy)
        {
            return CommandResult.Rejected("Wait until boarding and refueling have finished.");
        }

        Pushback.Start(_context);
        return CommandResult.Ok("Pushback requested.");
    }

    public CommandResult SteerPushback(PushbackSteering steering)
    {
        if (!Pushback.IsBusy)
        {
            return CommandResult.Rejected("No pushback in progress.");
        }

        Pushback.Steer(steering);
        return CommandResult.Ok($"Steering: {steering}.");
    }

    public CommandResult StopPushback()
    {
        if (!Pushback.IsBusy)
        {
            return CommandResult.Rejected("No pushback in progress.");
        }

        Pushback.RequestStop();
        return CommandResult.Ok("Stop requested.");
    }

    public CommandResult Abort(string serviceId)
    {
        GroundService? service = serviceId.ToLowerInvariant() switch
        {
            "refuel" or "fuel" => Refueling,
            "passengers" or "boarding" or "deboarding" => Passengers,
            "pushback" or "push" => Pushback,
            _ => null,
        };

        if (service is null)
        {
            return CommandResult.Rejected($"Unknown or non-abortable service '{serviceId}'.");
        }

        if (!service.IsBusy)
        {
            return CommandResult.Rejected($"{service.DisplayName} is not running.");
        }

        service.Abort(_context);
        return CommandResult.Ok($"{service.DisplayName} aborted.");
    }

    private CommandResult ToggleConnectable(ConnectableService service, ConnectableService? alternativeAccess)
    {
        if (!_context.Sim.IsConnected)
        {
            return CommandResult.Rejected("Not connected to the simulator.");
        }

        if (service.IsConnected)
        {
            var isLastCabinAccess = alternativeAccess is not null && !alternativeAccess.IsConnected;
            if (Passengers.IsBusy && isLastCabinAccess)
            {
                return CommandResult.Rejected("Wait until boarding has finished.");
            }

            service.Disconnect(_context);
            return CommandResult.Ok($"{service.DisplayName} disconnecting.");
        }

        if (service.IsBusy)
        {
            return CommandResult.Rejected($"{service.DisplayName} is busy ({service.Detail}).");
        }

        if (ParkedCheck() is { } reason)
        {
            return CommandResult.Rejected(reason);
        }

        if (Pushback.IsBusy)
        {
            return CommandResult.Rejected("Not available during pushback.");
        }

        service.Connect(_context);
        return CommandResult.Ok($"{service.DisplayName} requested.");
    }

    private string? ParkedCheck()
    {
        if (!_context.Sim.IsConnected)
        {
            return "Not connected to the simulator.";
        }

        if (!State.IsStationary)
        {
            return "The aircraft must be stationary on the ground.";
        }

        return null;
    }

    private string? PassengerCheck()
    {
        if (ParkedCheck() is { } reason)
        {
            return reason;
        }

        if (Passengers.IsBusy)
        {
            return "Boarding or deboarding is already in progress.";
        }

        if (Pushback.IsBusy)
        {
            return "Not available during pushback.";
        }

        if (!HasCabinAccess)
        {
            return "Connect the jetway or stairs first.";
        }

        return null;
    }
}
