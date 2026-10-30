namespace Rampside.Core.Simulation;

/// <summary>
/// A SimObject next to the user aircraft (ground vehicle, AI aircraft, ...), e.g. stairs or a GPU that the
/// aircraft, GSX or the sim placed there. Rampside uses these to see equipment it did not bring itself.
/// </summary>
public sealed record NearbyObject(uint ObjectId, string Title, double Latitude, double Longitude, double HeadingTrueDeg);
