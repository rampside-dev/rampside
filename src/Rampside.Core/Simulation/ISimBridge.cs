namespace Rampside.Core.Simulation;

/// <summary>
/// Everything Rampside needs from the simulator. The real implementation lives in
/// Rampside.MsfsBridge (SimConnect); <see cref="FakeSimBridge"/> is used for tests and offline development.
/// All values are SI units (kg, m/s, rad/s).
/// </summary>
public interface ISimBridge : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Latest known state of the user aircraft.</summary>
    AircraftState State { get; }

    event EventHandler<bool>? ConnectionChanged;

    /// <summary>Diagnostic messages (sim exceptions, triggered events, reconnects).</summary>
    event EventHandler<string>? Message;

    /// <summary>Starts connecting in the background. Returns immediately; watch <see cref="ConnectionChanged"/>.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    void SetFuel(IReadOnlyDictionary<FuelTankId, double> quantitiesKg);

    void SetPayloadStation(int stationIndex, double weightKg);

    /// <summary>Sends a simulator key event such as TOGGLE_JETWAY.</summary>
    void TriggerEvent(string simEventName, uint data = 0);

    /// <summary>
    /// Drives the aircraft on the ground. Call every tick while active.
    /// Negative <paramref name="longitudinalMps"/> moves backwards; positive <paramref name="yawRateRadPerSec"/> turns the nose right.
    /// </summary>
    void SetGroundMotion(double longitudinalMps, double yawRateRadPerSec);

    /// <summary>Hands control of the aircraft's motion back to the simulator.</summary>
    void ReleaseGroundMotion();
}
