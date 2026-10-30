using Rampside.Core.Profiles;
using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Core.Aircraft;

/// <summary>For aircraft that use the simulator's default fuel and payload variables. No door control.</summary>
public sealed class DefaultAircraftAdapter : IAircraftAdapter
{
    private readonly ISimBridge _sim;
    private readonly AircraftProfile _profile;

    public DefaultAircraftAdapter(ISimBridge sim, AircraftProfile profile)
    {
        _sim = sim;
        _profile = profile;
    }

    public string Name => "Default";
    public bool IsReady => _sim.IsConnected;
    public string? NotReadyReason => IsReady ? null : "Not connected to the simulator.";
    public IReadOnlyList<DoorId> Doors => Array.Empty<DoorId>();
    public bool SupportsFuel => true;
    public bool SupportsPayload => true;

    public DoorState GetDoorState(DoorId door) => DoorState.Unknown;

    public void SetDoor(DoorId door, bool open)
    {
    }

    public void SetFuel(IReadOnlyDictionary<FuelTankId, double> quantitiesKg) => _sim.SetFuel(quantitiesKg);

    public void SetPassengerLoad(int passengers)
    {
        foreach (var station in PayloadCalculator.Compute(_profile, passengers))
        {
            _sim.SetPayloadStation(station.Key, station.Value);
        }
    }

    public void Tick(TimeSpan elapsed)
    {
    }
}
