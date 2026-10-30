namespace Rampside.Core.Simulation;

/// <summary>Spawns the simulator's own ground vehicles (SimObjects of type ground) next to the aircraft.</summary>
public interface IGroundVehicleSpawner
{
    /// <summary>Raised with the container titles of all spawnable ground vehicles, sorted.</summary>
    event EventHandler<IReadOnlyList<string>>? GroundVehicleCatalogReceived;

    int SpawnedVehicleCount { get; }

    /// <summary>Asks the simulator for its ground vehicle catalog. The answer arrives via <see cref="GroundVehicleCatalogReceived"/>.</summary>
    void RequestGroundVehicleCatalog();

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
