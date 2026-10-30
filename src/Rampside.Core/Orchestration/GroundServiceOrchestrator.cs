using Rampside.Core.Aircraft;
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

    public GroundServiceOrchestrator(ISimBridge sim, AircraftProfile profile, IAircraftAdapter? adapter = null)
    {
        Adapter = adapter ?? new DefaultAircraftAdapter(sim, profile);
        _context = new ServiceContext(sim, profile, Adapter);
        Jetway = new ConnectableService("jetway", "Jetway", profile.Jetway, SimEvents.ToggleJetway);
        Stairs = new ConnectableService("stairs", "Stairs", profile.Stairs, SimEvents.ToggleStairs);
        Gpu = new ConnectableService("gpu", "Ground power", profile.Gpu, SimEvents.RequestGroundPower);
        Refueling = new RefuelingService();
        Passengers = new PassengerService();
        Pushback = new PushbackService();
        Services = new GroundService[] { Jetway, Stairs, Gpu, Refueling, Passengers, Pushback };

        foreach (var service in Services)
        {
            service.Changed += (sender, _) =>
            {
                var changed = (GroundService)sender!;
                AutomateDoors(changed);
                ServiceChanged?.Invoke(this, changed);
            };
        }
    }

    public event EventHandler<GroundService>? ServiceChanged;

    public IAircraftAdapter Adapter { get; }
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

        Adapter.Tick(elapsed);
        foreach (var service in Services)
        {
            service.Tick(_context, elapsed);
        }
    }

    public CommandResult SetDoor(DoorId door, bool open)
    {
        if (!_context.Sim.IsConnected)
        {
            return CommandResult.Rejected("Not connected to the simulator.");
        }

        if (!Adapter.Doors.Contains(door))
        {
            return CommandResult.Rejected($"{Adapter.Name} cannot control the {DoorName(door)} door.");
        }

        if (!Adapter.IsReady)
        {
            return CommandResult.Rejected(Adapter.NotReadyReason ?? "The aircraft is not ready.");
        }

        if (open && !State.IsStationary)
        {
            return CommandResult.Rejected("The aircraft must be stationary on the ground.");
        }

        var state = Adapter.GetDoorState(door);
        if (state == DoorState.Unknown)
        {
            return CommandResult.Rejected($"State of the {DoorName(door)} door is unknown. Is the aircraft powered?");
        }

        if (state == (open ? DoorState.Open : DoorState.Closed))
        {
            return CommandResult.Ok($"{DoorName(door)} door is already {(open ? "open" : "closed")}.");
        }

        Adapter.SetDoor(door, open);
        return CommandResult.Ok($"{DoorName(door)} door {(open ? "opening" : "closing")}.");
    }

    public CommandResult CloseAllDoors()
    {
        var open = OpenDoors();
        if (open.Count == 0)
        {
            return CommandResult.Ok("All doors are closed.");
        }

        foreach (var door in open)
        {
            Adapter.SetDoor(door, false);
        }

        return CommandResult.Ok($"Closing {string.Join(", ", open.Select(DoorName))}.");
    }

    public IReadOnlyList<DoorId> OpenDoors() =>
        Adapter.Doors.Where(d => Adapter.GetDoorState(d) == DoorState.Open).ToList();

    public static string DoorName(DoorId door) => door switch
    {
        DoorId.ForwardLeft => "forward left",
        DoorId.ForwardRight => "forward right",
        DoorId.AftLeft => "aft left",
        DoorId.AftRight => "aft right",
        DoorId.CargoForward => "forward cargo",
        DoorId.CargoAft => "aft cargo",
        DoorId.Airstair => "airstair",
        _ => door.ToString(),
    };

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

        if (!Adapter.SupportsFuel)
        {
            return CommandResult.Rejected($"Refueling is not supported for the {Adapter.Name} yet. Use the aircraft's own fuel menu.");
        }

        if (!Adapter.IsReady)
        {
            return CommandResult.Rejected(Adapter.NotReadyReason ?? "The aircraft is not ready.");
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
        return CommandResult.Ok($"Boarding {Passengers.TargetPassengers} passengers.{PayloadNote()}");
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
        return CommandResult.Ok($"Deboarding started.{PayloadNote()}");
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

        var openDoors = OpenDoors();
        if (openDoors.Count > 0)
        {
            return CommandResult.Rejected($"Close the doors first: {string.Join(", ", openDoors.Select(DoorName))}.");
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

            if (Profile.Doors.Automatic)
            {
                // Doors still used by the other equipment stay open.
                var stillNeeded = alternativeAccess is { IsConnected: true } ? DoorsFor(alternativeAccess) : Array.Empty<DoorId>();
                RequestDoors(DoorsFor(service).Except(stillNeeded), open: false);
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

    private void AutomateDoors(GroundService service)
    {
        if (!Profile.Doors.Automatic || Adapter.Doors.Count == 0)
        {
            return;
        }

        if ((service == Jetway || service == Stairs) && service.Status == ServiceStatus.Active)
        {
            RequestDoors(DoorsFor((ConnectableService)service), open: true);
        }
        else if (service == Passengers)
        {
            if (service.Status == ServiceStatus.Active)
            {
                RequestDoors(Profile.Doors.Cargo, open: true);
            }
            else if (service.Status is ServiceStatus.Completed or ServiceStatus.Aborted)
            {
                RequestDoors(Profile.Doors.Cargo, open: false);
            }
        }
    }

    private IReadOnlyList<DoorId> DoorsFor(ConnectableService service) =>
        service == Jetway ? Profile.Doors.Jetway : service == Stairs ? Profile.Doors.Stairs : Array.Empty<DoorId>();

    private void RequestDoors(IEnumerable<DoorId> doors, bool open)
    {
        foreach (var door in doors.Where(Adapter.Doors.Contains))
        {
            Adapter.SetDoor(door, open);
        }
    }

    private string PayloadNote() => Adapter.SupportsPayload
        ? string.Empty
        : $" Passenger weights are not transferred to the {Adapter.Name} yet; load the payload in the aircraft.";

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

        if (!Adapter.IsReady)
        {
            return Adapter.NotReadyReason ?? "The aircraft is not ready.";
        }

        if (!HasCabinAccess)
        {
            return "Connect the jetway or stairs first.";
        }

        return null;
    }
}
