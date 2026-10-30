namespace Rampside.Core.Simulation;

/// <summary>Spawns the simulator's own ground vehicles (SimObjects of type ground) next to the aircraft.</summary>
public interface IGroundVehicleSpawner
{
    /// <summary>Raised with the container titles of all spawnable SimObjects of one kind, sorted.</summary>
    event EventHandler<SimObjectCatalog>? CatalogReceived;

    int SpawnedVehicleCount { get; }

    /// <summary>
    /// Asks the simulator for its SimObject catalog of one kind. The answer arrives via <see cref="CatalogReceived"/>.
    /// Every title can be spawned with <see cref="SpawnGroundVehicle"/>, not only ground vehicles.
    /// </summary>
    void RequestCatalog(SimObjectKind kind);

    /// <summary>
    /// Places a vehicle on the ground at the given position, facing <paramref name="headingTrueDeg"/>.
    /// Returns a handle for <see cref="MoveGroundVehicle"/> and <see cref="RemoveGroundVehicle"/>, or null when not connected.
    /// </summary>
    int? SpawnGroundVehicle(string title, double latitude, double longitude, double headingTrueDeg);

    /// <summary>Moves a spawned vehicle. Ignored while the sim has not created it yet.</summary>
    void MoveGroundVehicle(int vehicle, double latitude, double longitude, double headingTrueDeg);

    /// <summary>
    /// Keeps a spawned vehicle at a fixed place relative to the user aircraft, updated every sim frame,
    /// so it moves with the aircraft without lagging. Null detaches it; it then stays where it is.
    /// </summary>
    void AttachGroundVehicle(int vehicle, VehicleAttachment? attachment);

    /// <summary>
    /// Drives a spawned vehicle along <paramref name="drive"/>, moved every sim frame (detaches it from the aircraft).
    /// With <paramref name="removeAtEnd"/> the vehicle is removed when it arrives; otherwise it stays there.
    /// </summary>
    void DriveGroundVehicle(int vehicle, VehicleDrive drive, bool removeAtEnd);

    /// <summary>Removes one spawned vehicle, also when the sim has not created it yet.</summary>
    void RemoveGroundVehicle(int vehicle);

    /// <summary>Removes every vehicle this client has spawned.</summary>
    void RemoveSpawnedVehicles();
}

/// <summary>
/// SimObject types SimConnect can enumerate (SIMCONNECT_SIMOBJECT_TYPE). There is no type for people:
/// ground crew and pedestrians, if spawnable, show up under one of these (presumably <see cref="Animal"/> or only under <see cref="All"/>).
/// </summary>
public enum SimObjectKind
{
    All,
    Aircraft,
    Helicopter,
    Boat,
    Ground,
    HotAirBalloon,
    Animal,
    UserAvatar,
}

/// <summary>Answer to <see cref="IGroundVehicleSpawner.RequestCatalog"/>: the sorted container titles of one kind.</summary>
public sealed record SimObjectCatalog(SimObjectKind Kind, IReadOnlyList<string> Titles);

/// <summary>
/// Place of a vehicle relative to the aircraft reference point: metres forward and to the right,
/// and its heading relative to the aircraft heading (180 = facing the aircraft from ahead).
/// </summary>
public readonly record struct VehicleAttachment(double ForwardM, double RightM, double HeadingOffsetDeg)
{
    public (double Latitude, double Longitude, double HeadingTrueDeg) Place(double latitude, double longitude, double headingTrueDeg)
    {
        var (lat, lon) = Geo.Offset(latitude, longitude, headingTrueDeg, ForwardM, RightM);
        return (lat, lon, Geo.NormalizeHeading(headingTrueDeg + HeadingOffsetDeg));
    }
}
