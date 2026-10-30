using Rampside.Core.Aircraft;
using Rampside.Core.FlightPlans;
using Rampside.Core.Gsx;
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
    private readonly IGsxStandSource? _gsxStands;
    private TimeSpan? _jetwayDoorDelay;

    // Jetway ↔ stairs swap: doors get this long to close (PMDG L1 takes about 10 s plus the event spacing),
    // the old equipment this long to drive away.
    private static readonly TimeSpan AccessSwapDoorTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan AccessSwapLeaveTimeout = TimeSpan.FromMinutes(4);
    private AccessSwap? _accessSwap;
    private (string Icao, int ParkingIndex)? _gsxStandKey;

    /// <param name="gsxStands">Installed GSX profiles; their pushback targets replace the planned ones where a stand has some.</param>
    public GroundServiceOrchestrator(ISimBridge sim, AircraftProfile profile, IAircraftAdapter? adapter = null, IGsxStandSource? gsxStands = null)
    {
        _gsxStands = gsxStands;
        Adapter = adapter ?? new DefaultAircraftAdapter(sim, profile);
        _context = new ServiceContext(sim, profile, Adapter);
        Jetway = new ConnectableService("jetway", "Jetway", GroundEquipment.Jetway, profile.Jetway, SimEvents.ToggleJetway);
        Stairs = new ConnectableService("stairs", "Stairs", GroundEquipment.Stairs, profile.Stairs, SimEvents.ToggleStairs);
        Gpu = new ConnectableService("gpu", "Ground power", GroundEquipment.GroundPower, profile.Gpu, SimEvents.RequestGroundPower);
        Chocks = new ConnectableService("chocks", "Wheel chocks", GroundEquipment.Chocks, profile.Chocks, string.Empty,
            ConnectableWords.Chocks, startsUnknown: true);
        Adapter.Message += (_, text) => Message?.Invoke(this, text);
        Refueling = new RefuelingService();
        Passengers = new PassengerService();
        Pushback = new PushbackService();

        // Chocks only where a procedure can set them (PMDG CDU); the sim has no chocks of its own.
        Services = new GroundService[] { Jetway, Stairs, Gpu, Chocks, Refueling, Passengers, Pushback }
            .Where(s => s != Chocks || Adapter.Supports(GroundEquipment.Chocks))
            .ToArray();

        foreach (var service in Services)
        {
            service.Changed += (sender, _) =>
            {
                var changed = (GroundService)sender!;
                AutomateDoors(changed);
                ServiceChanged?.Invoke(this, changed);
            };
            service.CrewCalled += (_, call) => CrewCalled?.Invoke(this, call);
        }
    }

    public event EventHandler<GroundService>? ServiceChanged;

    /// <summary>The ground crew of any service talks to the cockpit (for voice playback).</summary>
    public event EventHandler<CrewCall>? CrewCalled;

    /// <summary>Notes for the log from the aircraft adapter (e.g. a CDU request that failed).</summary>
    public event EventHandler<string>? Message;

    public IAircraftAdapter Adapter { get; }
    public AircraftProfile Profile => _context.Profile;
    public AircraftState State => _context.State;
    public IReadOnlyList<GroundService> Services { get; }
    public ConnectableService Jetway { get; }
    public ConnectableService Stairs { get; }
    public ConnectableService Gpu { get; }

    /// <summary>Wheel chocks ("connected" = set). Not in <see cref="Services"/> when the aircraft's procedure is "none".</summary>
    public ConnectableService Chocks { get; }
    public RefuelingService Refueling { get; }
    public PassengerService Passengers { get; }
    public PushbackService Pushback { get; }

    /// <summary>The GSX profile stand for the current stand, or null when there is none (or no GSX profiles are installed).</summary>
    public GsxStandMatch? GsxStand { get; private set; }

    private bool HasCabinAccess => Jetway.IsConnected || Stairs.IsConnected;

    public void Tick(TimeSpan elapsed)
    {
        if (!_context.Sim.IsConnected)
        {
            return;
        }

        Adapter.Tick(elapsed);
        UpdateGsxStand();

        // The sim reports the real jetway state, including jetways moved by GSX, the sim or the user.
        if (State.Stand?.Jetway is { } jetway)
        {
            Jetway.ApplyObserved(jetway.Status switch
            {
                JetwayStatus.Attached when jetway.AttachedToUser => ObservedEquipment.Connected,
                JetwayStatus.Moving => ObservedEquipment.Moving,
                _ => ObservedEquipment.Away,
            });
        }

        // Aircraft that bring their own stairs or GPU (PMDG) report where those are.
        foreach (var service in new[] { Jetway, Stairs, Gpu, Chocks })
        {
            if (Adapter.HandlesEquipment(service.Equipment) && Adapter.GetEquipmentState(service.Equipment, State) is { } observed)
            {
                service.ApplyObserved(observed);
            }
        }

        foreach (var service in Services)
        {
            service.Tick(_context, elapsed);
        }

        TickAccessSwap(elapsed);

        if (_jetwayDoorDelay is { } delay)
        {
            delay -= elapsed;
            _jetwayDoorDelay = delay > TimeSpan.Zero ? delay : null;
            if (_jetwayDoorDelay is null && Jetway.IsConnected)
            {
                Message?.Invoke(this, $"Jetway attached: opening {string.Join(", ", Profile.Doors.Jetway.Select(DoorName))}.");
                RequestDoors(Profile.Doors.Jetway, open: true);
            }
            else if (_jetwayDoorDelay is null)
            {
                Message?.Invoke(this, $"Jetway door not opened: the jetway is {Jetway.Detail.ToLowerInvariant()}.");
            }
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

    public CommandResult ToggleJetway()
    {
        if (State.Stand is { HasJetway: false } stand && _accessSwap?.To != Jetway)
        {
            return CommandResult.Rejected($"{stand.Name} has no jetway. Use the stairs.");
        }

        return ToggleCabinAccess(Jetway, Stairs);
    }

    public CommandResult ToggleStairs() => ToggleCabinAccess(Stairs, Jetway);

    /// <summary>True while one cabin access is being replaced by the other (doors closing, old equipment leaving).</summary>
    public bool IsSwappingCabinAccess => _accessSwap is not null;

    /// <summary>
    /// Jetway and stairs share the doors, so asking for one while the other is connected swaps them:
    /// close the doors, send the connected one away, then call the new one (it opens the doors again when it is there).
    /// Asking again while that runs cancels it.
    /// </summary>
    private CommandResult ToggleCabinAccess(ConnectableService wanted, ConnectableService other)
    {
        if (_accessSwap is { } running)
        {
            CancelAccessSwap($"{running.To.DisplayName} request cancelled.");
            return CommandResult.Ok($"{running.To.DisplayName} request cancelled.");
        }

        if (wanted.IsConnected || wanted.IsBusy || !other.IsConnected)
        {
            return ToggleConnectable(wanted, other);
        }

        if (ParkedCheck() is { } reason)
        {
            return CommandResult.Rejected(reason);
        }

        if (Pushback.IsBusy)
        {
            return CommandResult.Rejected("Not available during pushback.");
        }

        if (Passengers.IsBusy)
        {
            return CommandResult.Rejected($"Wait until boarding has finished before replacing the {other.DisplayName.ToLowerInvariant()}.");
        }

        var doors = Profile.Doors.Automatic ? DoorsFor(other).Where(Adapter.Doors.Contains).ToArray() : Array.Empty<DoorId>();
        RequestDoors(doors, open: false);
        _accessSwap = new AccessSwap(other, wanted, doors);
        wanted.SetWaiting($"Waiting for the {other.DisplayName.ToLowerInvariant()} to leave");

        var steps = doors.Length > 0 ? $"Closing {string.Join(", ", doors.Select(DoorName))}, then the" : "The";
        var message = $"{steps} {other.DisplayName.ToLowerInvariant()} leave and the {wanted.DisplayName.ToLowerInvariant()} comes.";
        Message?.Invoke(this, message);
        return CommandResult.Ok(message);
    }

    private void TickAccessSwap(TimeSpan elapsed)
    {
        if (_accessSwap is not { } swap)
        {
            return;
        }

        swap.Elapsed += elapsed;
        if (!swap.Leaving)
        {
            // The doors must be closed before the equipment moves away (unknown = no door lights, nothing to wait for).
            var stillOpen = swap.Doors.Where(d => Adapter.GetDoorState(d) == DoorState.Open).ToArray();
            if (stillOpen.Length > 0 && swap.Elapsed < AccessSwapDoorTimeout)
            {
                return;
            }

            if (stillOpen.Length > 0)
            {
                Message?.Invoke(this, $"{string.Join(", ", stillOpen.Select(DoorName))} did not close; sending the {swap.From.DisplayName.ToLowerInvariant()} away anyway.");
            }

            if (Adapter.HandlesEquipment(swap.From.Equipment) && Adapter.SetEquipment(swap.From.Equipment, connected: false, State) is { } refused)
            {
                CancelAccessSwap(refused);
                return;
            }

            swap.From.Disconnect(_context);
            swap.Leaving = true;
            swap.Elapsed = TimeSpan.Zero;
            return;
        }

        if (swap.From.Status != ServiceStatus.Idle)
        {
            if (swap.From.Status == ServiceStatus.Active || swap.Elapsed > AccessSwapLeaveTimeout)
            {
                CancelAccessSwap($"The {swap.From.DisplayName.ToLowerInvariant()} did not leave; the {swap.To.DisplayName.ToLowerInvariant()} was not called.");
            }

            return;
        }

        _accessSwap = null;
        swap.To.SetWaiting(null);
        var result = ToggleConnectable(swap.To, swap.From);
        if (!result.Accepted)
        {
            Message?.Invoke(this, result.Message);
        }
    }

    private void CancelAccessSwap(string message)
    {
        if (_accessSwap is { } swap)
        {
            _accessSwap = null;
            swap.To.SetWaiting(null);
            Message?.Invoke(this, message);
        }
    }

    private sealed class AccessSwap
    {
        public AccessSwap(ConnectableService from, ConnectableService to, IReadOnlyList<DoorId> doors)
        {
            From = from;
            To = to;
            Doors = doors;
        }

        public ConnectableService From { get; }
        public ConnectableService To { get; }
        public IReadOnlyList<DoorId> Doors { get; }
        public bool Leaving { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    public CommandResult ToggleGpu() => ToggleConnectable(Gpu, null);

    /// <summary>Sets the wheel chocks, or removes them when they are set.</summary>
    public CommandResult ToggleChocks() => Adapter.Supports(GroundEquipment.Chocks)
        ? ToggleConnectable(Chocks, null)
        : CommandResult.Rejected($"Rampside cannot set the chocks of the {Adapter.Name}. Use the aircraft's own menu.");

    /// <summary>The flight plan boarding and refueling use when no amounts are given, or null.</summary>
    public FlightPlan? FlightPlan { get; private set; }

    /// <summary>Result of the checks when <see cref="FlightPlan"/> was loaded.</summary>
    public FlightPlanReview? FlightPlanReview { get; private set; }

    /// <summary>
    /// Uses a flight plan for boarding and refueling. Refused when the aircraft type or departure airport do not
    /// match (unless <paramref name="force"/>), or while passengers are boarding.
    /// </summary>
    public CommandResult LoadFlightPlan(FlightPlan plan, bool force = false, DateTimeOffset? now = null)
    {
        if (Passengers.IsBusy)
        {
            return CommandResult.Rejected("Wait until boarding or deboarding has finished before changing the flight plan.");
        }

        var isNewPlan = FlightPlan is null || FlightPlan.Id != plan.Id;
        if (isNewPlan && Passengers.PassengersOnBoard >= 0.5 && !force)
        {
            return CommandResult.Rejected(
                $"{Passengers.PassengersOnBoard:F0} passengers of the current load are on board. Deboard first, or use force to switch the plan anyway.");
        }

        var review = FlightPlanCheck.Review(plan, State, Profile, now ?? DateTimeOffset.UtcNow);
        if (review.IsBlocked && !force)
        {
            return CommandResult.Rejected(
                $"{FlightPlanFormat.Title(plan)} not loaded: {string.Join(" ", review.Blockers)} Create a new plan in {plan.Source}, or use force to load it anyway.");
        }

        FlightPlan = plan;
        FlightPlanReview = review;

        var lines = new List<string> { $"{(isNewPlan ? "Loaded" : "Reloaded")} {FlightPlanFormat.Title(plan)}: {FlightPlanFormat.Load(plan)}." };
        lines.AddRange(review.Blockers.Select(b => $"Forced despite: {b}"));
        lines.AddRange(review.Warnings);
        if (review.NotChecked.Count > 0)
        {
            lines.Add($"Not checked: {string.Join(", ", review.NotChecked)}.");
        }

        if (!Adapter.SupportsFuel || !Adapter.SupportsPayload)
        {
            lines.Add($"The {Adapter.Name} adapter cannot set {(Adapter.SupportsFuel ? "payload" : Adapter.SupportsPayload ? "fuel" : "fuel or payload")} yet; enter the plan in the aircraft.");
        }

        return CommandResult.Ok(string.Join(Environment.NewLine, lines));
    }

    public CommandResult ClearFlightPlan()
    {
        if (FlightPlan is null)
        {
            return CommandResult.Rejected("No flight plan loaded.");
        }

        FlightPlan = null;
        FlightPlanReview = null;
        return CommandResult.Ok("Flight plan removed; boarding and refueling use the profile defaults again.");
    }

    /// <summary>Refuels to the given total, or to the flight plan's block fuel when no amount is given.</summary>
    public CommandResult RequestRefuel(double? targetKg = null)
    {
        if (ParkedCheck() is { } reason)
        {
            return CommandResult.Rejected(reason);
        }

        if (targetKg is null && FlightPlan is null)
        {
            return CommandResult.Rejected("Give a fuel amount or load a flight plan first.");
        }

        var target = targetKg ?? FlightPlan!.BlockFuelKg;
        if (target < 0)
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

        Refueling.Start(_context, target);
        var source = targetKg is null ? $" (block fuel of {FlightPlanFormat.Title(FlightPlan!)})" : string.Empty;
        return CommandResult.Ok($"Refueling to {Refueling.TargetKg:F0} kg requested{source}.");
    }

    public CommandResult RequestBoarding(int? passengers = null)
    {
        if (PassengerCheck() is { } reason)
        {
            return CommandResult.Rejected(reason);
        }

        var count = passengers ?? FlightPlan?.Passengers ?? Profile.Passengers.Capacity;
        if (count <= 0)
        {
            return CommandResult.Rejected("Passenger count must be positive.");
        }

        count = Math.Min(count, Profile.Passengers.Capacity);
        var load = FlightPlan is { } plan
            ? LoadPlan.FromFlightPlan(plan, count)
            : LoadPlan.FromProfile(Profile.Passengers, count);
        Passengers.StartBoarding(_context, load);
        var source = FlightPlan is null ? string.Empty : $" and {load.CargoKg:F0} kg in the holds ({FlightPlanFormat.Title(FlightPlan)})";
        return CommandResult.Ok($"Boarding {Passengers.TargetPassengers} passengers{source}.{PayloadNote()}");
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

    /// <summary>
    /// Pushbacks from the current stand: the targets of the stand's GSX profile when it has usable ones,
    /// otherwise planned onto the taxi line behind the aircraft. Empty when neither is known.
    /// </summary>
    public IReadOnlyList<PushbackOption> PushbackOptions()
    {
        if (Pushback.IsBusy || State.Stand is null)
        {
            return Array.Empty<PushbackOption>();
        }

        if (GsxStand is { } gsx && GsxPushbackBuilder.Build(State, gsx) is { Count: > 0 } targets)
        {
            return targets;
        }

        return State.Stand.Taxiways is { } taxiways
            ? PushbackPlanner.Plan(State, taxiways, Profile.Pushback)
            : Array.Empty<PushbackOption>();
    }

    /// <summary>Calls the tug. With <paramref name="facing"/> it follows the first option ending in that direction; without it is steered manually.</summary>
    public CommandResult RequestPushback(CompassDirection? facing = null) =>
        facing is { } direction
            ? StartPushback(options => options.FirstOrDefault(o => o.Facing == direction), $"Facing {Compass.Name(direction)} is not possible here.")
            : StartPushback(null, string.Empty);

    /// <summary>Calls the tug for the option with this <see cref="PushbackOption.Label"/> (e.g. a GSX target name).</summary>
    public CommandResult RequestPlannedPushback(string label) =>
        StartPushback(
            options => options.FirstOrDefault(o => string.Equals(o.Label, label.Trim(), StringComparison.OrdinalIgnoreCase)),
            $"No pushback '{label.Trim()}' here.");

    private CommandResult StartPushback(Func<IReadOnlyList<PushbackOption>, PushbackOption?>? choose, string notFound)
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

        if (Chocks.IsBusy)
        {
            return CommandResult.Rejected("Remove the wheel chocks first.");
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

        PushbackOption? plan = null;
        if (choose is not null)
        {
            var options = PushbackOptions();
            plan = choose(options);
            if (plan is null)
            {
                return CommandResult.Rejected(options.Count == 0
                    ? "No pushback path known for this stand. Use a manual pushback."
                    : $"{notFound} Options: {string.Join(", ", options.Select(o => o.Label))}.");
            }
        }

        Pushback.Start(_context, plan);
        return CommandResult.Ok(plan is null ? "Pushback requested." : $"Pushback requested, {plan.Label}.");
    }

    /// <summary>Moves the aircraft to a parking spot of the current airport (index into <see cref="AircraftState.AirportParkings"/>).</summary>
    public CommandResult RepositionTo(int parkingIndex)
    {
        if (ParkedCheck() is { } reason)
        {
            return CommandResult.Rejected(reason);
        }

        if (Services.FirstOrDefault(s => s.IsBusy) is { } busy)
        {
            return CommandResult.Rejected(busy is ConnectableService { IsConnected: true }
                ? $"Disconnect the {busy.DisplayName.ToLowerInvariant()} first."
                : $"{busy.DisplayName} is still in progress. Wait or abort it first.");
        }

        if (State.Stand?.Jetway is { AttachedToUser: true } || State.Stand?.Jetway is { Status: JetwayStatus.Moving })
        {
            return CommandResult.Rejected("The jetway is attached or moving. Retract it first.");
        }

        if (State.AirportParkings.FirstOrDefault(p => p.Index == parkingIndex) is not { } spot)
        {
            return CommandResult.Rejected("Unknown parking position. The airport's stands are loaded once the aircraft is at rest there.");
        }

        _context.Sim.Reposition(spot.Latitude, spot.Longitude, spot.HeadingDeg);
        var airport = State.AirportIcao is { } icao ? $"{icao} " : string.Empty;
        return CommandResult.Ok($"Moved to {airport}{spot.DisplayName}.{(State.ParkingBrakeSet ? string.Empty : " Set the parking brake.")}");
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

            if (Adapter.HandlesEquipment(service.Equipment) && Adapter.SetEquipment(service.Equipment, connected: false, State) is { } refused)
            {
                return CommandResult.Rejected(refused);
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

        if (Adapter.HandlesEquipment(service.Equipment) && Adapter.SetEquipment(service.Equipment, connected: true, State) is { } refusal)
        {
            return CommandResult.Rejected(refusal);
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

        if (service == Jetway && service.Status == ServiceStatus.Active)
        {
            _jetwayDoorDelay = TimeSpan.FromSeconds(Profile.Doors.JetwayOpenDelaySeconds);
            if (_jetwayDoorDelay <= TimeSpan.Zero)
            {
                _jetwayDoorDelay = null;
                RequestDoors(Profile.Doors.Jetway, open: true);
            }
        }
        else if (service == Stairs && service.Status == ServiceStatus.Active)
        {
            RequestDoors(Profile.Doors.Stairs, open: true);
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

    /// <summary>Looks the stand up in the GSX profiles once per stand and reports what was found.</summary>
    private void UpdateGsxStand()
    {
        if (_gsxStands is null)
        {
            return;
        }

        var stand = State.Stand;
        (string, int)? key = stand is null ? null : (stand.AirportIcao, stand.ParkingIndex);
        if (key == _gsxStandKey)
        {
            return;
        }

        _gsxStandKey = key;
        GsxStand = null;
        if (stand is null)
        {
            return;
        }

        var spot = State.AirportParkings.FirstOrDefault(p => p.Index == stand.ParkingIndex);
        var aircraft = GsxAircraft.From(FlightPlanCheck.AircraftTypes(State, Profile), Profile.Gsx.IdMajor, Profile.Gsx.IdMinor);
        GsxStand = _gsxStands.Find(stand.AirportIcao, spot, stand.Name, aircraft);
        if (GsxStand is { } match)
        {
            var notes = new List<string>();
            var usable = GsxPushbackBuilder.Build(State, match, notes);
            var where = match.DistanceMeters is { } distance ? $"{distance:F1} m from the MSFS spot" : "matched by name";
            var stop = match.StopOffset is { } offset
                ? $", stop position {offset.Meters:F1} m short for {offset.Aircraft} ({offset.Function})"
                : aircraft is null ? ", aircraft unknown to GSX scripts" : string.Empty;
            Message?.Invoke(this, $"GSX profile {match.ProfileName}: {stand.Name} is [{match.Stand.Section}] ({where}){stop}, "
                + $"{usable.Count} of {match.Stand.PushbackTargets.Count} pushback targets usable"
                + (notes.Count > 0 ? $"; {string.Join("; ", notes)}" : string.Empty));
        }
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
