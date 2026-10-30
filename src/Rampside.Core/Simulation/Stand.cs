namespace Rampside.Core.Simulation;

public enum JetwayStatus
{
    /// <summary>Parked away from any aircraft (raw status 0).</summary>
    Rest,

    /// <summary>Driving, turning or adjusting the hood (raw status 1..6).</summary>
    Moving,

    /// <summary>Fully attached to an aircraft (raw status 7).</summary>
    Attached,
}

/// <summary>
/// One jetway as reported by SimConnect_RequestJetwayData. Observed in MSFS 2024 when attaching:
/// 0 → 1 → 2 → 3 → 7 within about 33 seconds.
/// </summary>
public sealed record JetwayInfo(int RawStatus, bool AttachedToUser, int Door)
{
    public JetwayStatus Status => RawStatus switch
    {
        0 => JetwayStatus.Rest,
        7 => JetwayStatus.Attached,
        _ => JetwayStatus.Moving,
    };
}

/// <summary>The parking spot the user aircraft stands on, from the airport's facility data.</summary>
/// <param name="ParkingIndex">Index in the airport's TAXI_PARKING list (not the stand number shown to pilots).</param>
/// <param name="Name">Display name such as "Gate 121".</param>
/// <param name="Jetway">Jetway serving this stand, or null if it has none.</param>
public sealed record StandInfo(
    string AirportIcao,
    int ParkingIndex,
    string Name,
    double DistanceMeters,
    double HeadingDeg,
    JetwayInfo? Jetway)
{
    public bool HasJetway => Jetway is not null;
}
