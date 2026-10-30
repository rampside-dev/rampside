namespace Rampside.Core.Simulation;

/// <summary>
/// Default MSFS key events used to show the simulator's built-in ground equipment.
/// Profiles can override each one. Verify against the MSFS 2024 SDK event list when testing in the sim.
/// </summary>
public static class SimEvents
{
    public const string ToggleJetway = "TOGGLE_JETWAY";
    public const string ToggleStairs = "TOGGLE_RAMPTRUCK";
    public const string RequestGroundPower = "REQUEST_POWER_SUPPLY";
}
