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

    /// <summary>Places the user aircraft on the ground at a position, at rest (like the sim's own "start at gate").</summary>
    void Reposition(double latitude, double longitude, double headingTrueDeg);

    /// <summary>
    /// Holds the aircraft's pitch by writing it every sim frame, following <paramref name="ramp"/> from its start;
    /// used to lift the nose wheel off the ground like a towbarless tug. Null hands the pitch back to the sim at once.
    /// </summary>
    void HoldPitch(PitchRamp? ramp);
}

/// <summary>
/// A pitch movement along a sin² curve, so it starts and ends gently. Degrees as the sim reports them
/// (PLANE PITCH DEGREES: negative = nose up). With <see cref="ReleaseWhenDone"/> the sim gets the pitch back at the end.
/// </summary>
public sealed record PitchRamp(double FromDeg, double ToDeg, double Seconds, bool ReleaseWhenDone = false)
{
    public double At(double elapsedSeconds)
    {
        var t = Seconds <= 0 ? 1 : Math.Clamp(elapsedSeconds / Seconds, 0, 1);
        var s = Math.Sin(Math.PI / 2 * t);
        return FromDeg + (ToDeg - FromDeg) * s * s;
    }

    public bool IsDone(double elapsedSeconds) => elapsedSeconds >= Seconds;
}
