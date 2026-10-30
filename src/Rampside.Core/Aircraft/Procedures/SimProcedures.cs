using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Core.Aircraft.Procedures;

/// <summary>"sim": the sim's own ground service event (TOGGLE_JETWAY, TOGGLE_RAMPTRUCK, REQUEST_POWER_SUPPLY).</summary>
/// <remarks>The event toggles, so it is sent for connect and disconnect alike. An empty event name sends nothing.</remarks>
public sealed class SimEventEquipment : IEquipmentProcedure
{
    private readonly ISimBridge _sim;
    private readonly string _event;

    public SimEventEquipment(ISimBridge sim, GroundEquipment equipment, string simEvent)
    {
        _sim = sim;
        Equipment = equipment;
        _event = simEvent;
    }

    public string Name => "sim";
    public GroundEquipment Equipment { get; }
    public string? NotReadyReason => _sim.IsConnected ? null : "Not connected to the simulator.";

    public string? Request(bool connected, AircraftState state)
    {
        if (!string.IsNullOrWhiteSpace(_event))
        {
            _sim.TriggerEvent(_event);
        }

        return null;
    }

    // The jetway is watched by the orchestrator (stand monitor); stairs and GPU are not visible yet.
    public ObservedEquipment? Observe(AircraftState state) => null;

    public void Tick(TimeSpan elapsed)
    {
    }
}

/// <summary>"none": Rampside cannot do this for the aircraft; the pilot uses the aircraft's own menus.</summary>
public sealed class NoEquipment : IEquipmentProcedure
{
    public NoEquipment(GroundEquipment equipment) => Equipment = equipment;

    public string Name => "none";
    public GroundEquipment Equipment { get; }
    public string? NotReadyReason => null;

    public string? Request(bool connected, AircraftState state) =>
        $"Rampside cannot handle the {Describe(Equipment)} of this aircraft. Use the aircraft's own menu.";

    public ObservedEquipment? Observe(AircraftState state) => null;

    public void Tick(TimeSpan elapsed)
    {
    }

    public static string Describe(GroundEquipment equipment) => equipment switch
    {
        GroundEquipment.GroundPower => "ground power",
        GroundEquipment.Chocks => "chocks",
        _ => equipment.ToString().ToLowerInvariant(),
    };
}

/// <summary>"none": no door control.</summary>
public sealed class NoDoors : IDoorProcedure
{
    public string Name => "none";
    public string? NotReadyReason => null;
    public IReadOnlyList<DoorId> Doors => Array.Empty<DoorId>();
    public DoorState GetDoorState(DoorId door) => DoorState.Unknown;

    public void SetDoor(DoorId door, bool open)
    {
    }

    public void Tick(TimeSpan elapsed)
    {
    }
}

/// <summary>"sim": writes the sim's fuel tank variables.</summary>
public sealed class SimFuel : IFuelProcedure
{
    private readonly ISimBridge _sim;

    public SimFuel(ISimBridge sim) => _sim = sim;

    public string Name => "sim";
    public string? NotReadyReason => _sim.IsConnected ? null : "Not connected to the simulator.";
    public bool Supported => true;
    public void SetFuel(IReadOnlyDictionary<FuelTankId, double> quantitiesKg) => _sim.SetFuel(quantitiesKg);

    public void Tick(TimeSpan elapsed)
    {
    }
}

/// <summary>"none": the aircraft is fuelled through its own menus.</summary>
public sealed class NoFuel : IFuelProcedure
{
    public string Name => "none";
    public string? NotReadyReason => null;
    public bool Supported => false;

    public void SetFuel(IReadOnlyDictionary<FuelTankId, double> quantitiesKg)
    {
    }

    public void Tick(TimeSpan elapsed)
    {
    }
}

/// <summary>"sim": writes the sim's payload stations, split as the profile says.</summary>
public sealed class SimPayload : IPayloadProcedure
{
    private readonly ISimBridge _sim;
    private readonly Profiles.AircraftProfile _profile;

    public SimPayload(ISimBridge sim, Profiles.AircraftProfile profile)
    {
        _sim = sim;
        _profile = profile;
    }

    public string Name => "sim";
    public string? NotReadyReason => _sim.IsConnected ? null : "Not connected to the simulator.";
    public bool Supported => true;

    public void SetPayload(PayloadLoad load)
    {
        foreach (var station in PayloadCalculator.Compute(_profile, load))
        {
            _sim.SetPayloadStation(station.Key, station.Value);
        }
    }

    public void Tick(TimeSpan elapsed)
    {
    }
}

/// <summary>"none": the aircraft is loaded through its own menus.</summary>
public sealed class NoPayload : IPayloadProcedure
{
    public string Name => "none";
    public string? NotReadyReason => null;
    public bool Supported => false;

    public void SetPayload(PayloadLoad load)
    {
    }

    public void Tick(TimeSpan elapsed)
    {
    }
}
