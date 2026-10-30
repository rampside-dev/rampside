using Rampside.Core.Aircraft.Procedures;
using Rampside.Core.Profiles;
using Rampside.Core.Simulation;

namespace Rampside.Core.Aircraft;

/// <summary>
/// The adapter the orchestrator builds when it is given none: the profile's procedures without any aircraft SDK link
/// (for the "default" family: the sim's ground services, fuel and payload variables, no door control).
/// </summary>
public sealed class DefaultAircraftAdapter : ComposedAircraftAdapter
{
    public DefaultAircraftAdapter(ISimBridge sim, AircraftProfile profile)
        : base(AircraftProcedureCatalog.Compose(profile, new ProcedureContext(sim, profile)))
    {
    }
}
