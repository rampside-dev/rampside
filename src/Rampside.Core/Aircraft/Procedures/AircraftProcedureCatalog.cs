using Rampside.Core.Aircraft.Pmdg;
using Rampside.Core.Profiles;
using Rampside.Core.Simulation;

namespace Rampside.Core.Aircraft.Procedures;

/// <summary>
/// Procedure name per job. In a profile (<c>"procedures"</c>) only the jobs that differ from the aircraft family are
/// listed, e.g. <c>{ "stairs": "sim" }</c>; null means "as the family does it".
/// </summary>
public sealed record ProcedureSet
{
    public string? Doors { get; init; }
    public string? Jetway { get; init; }
    public string? Stairs { get; init; }
    public string? GroundPower { get; init; }
    public string? Chocks { get; init; }
    public string? Fuel { get; init; }
    public string? Payload { get; init; }

    /// <summary>This set with every job that <paramref name="overrides"/> names replaced.</summary>
    public ProcedureSet With(ProcedureSet? overrides) => overrides is null ? this : new()
    {
        Doors = Pick(overrides.Doors, Doors),
        Jetway = Pick(overrides.Jetway, Jetway),
        Stairs = Pick(overrides.Stairs, Stairs),
        GroundPower = Pick(overrides.GroundPower, GroundPower),
        Chocks = Pick(overrides.Chocks, Chocks),
        Fuel = Pick(overrides.Fuel, Fuel),
        Payload = Pick(overrides.Payload, Payload),
    };

    private static string? Pick(string? value, string? fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
}

/// <summary>
/// A kind of aircraft with its usual procedures (profile field <c>"adapter"</c>).
/// <paramref name="SdkOnly"/>: the aircraft's EULA forbids writing sim variables (PMDG), so "sim" fuel and payload are refused.
/// </summary>
public sealed record AircraftFamily(string Id, string Name, ProcedureSet Procedures, bool SdkOnly);

/// <summary>The procedures Rampside knows, by job, and the aircraft families that combine them.</summary>
public static class AircraftProcedureCatalog
{
    /// <summary>Aircraft that use the sim's own systems: default ground services, fuel and payload variables.</summary>
    public static readonly AircraftFamily DefaultFamily = new(AircraftProfile.DefaultAdapter, "Default", new ProcedureSet
    {
        Doors = "none",
        Jetway = "sim",
        Stairs = "sim",
        GroundPower = "sim",
        Chocks = "none",
        Fuel = "sim",
        Payload = "sim",
    }, SdkOnly: false);

    /// <summary>PMDG 737: doors through the SDK, its own stairs and GPU through the CDU, fuel and payload in the aircraft.</summary>
    public static readonly AircraftFamily Pmdg737Family = new(AircraftProfile.Pmdg737Adapter, "PMDG 737", new ProcedureSet
    {
        Doors = "pmdg-sdk",
        Jetway = "sim",
        Stairs = "pmdg-cdu",
        GroundPower = "pmdg-cdu",
        Chocks = "pmdg-cdu",
        Fuel = "none",
        Payload = "none",
    }, SdkOnly: true);

    public static IReadOnlyList<AircraftFamily> Families { get; } = new[] { DefaultFamily, Pmdg737Family };

    /// <summary>Procedure names per job, for profile checks and documentation.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Names { get; } = new Dictionary<string, IReadOnlyList<string>>
    {
        ["doors"] = new[] { "none", "pmdg-sdk" },
        ["jetway"] = new[] { "none", "sim" },
        ["stairs"] = new[] { "none", "sim", "pmdg-cdu" },
        ["groundPower"] = new[] { "none", "sim", "pmdg-cdu" },
        ["chocks"] = new[] { "none", "pmdg-cdu" },
        ["fuel"] = new[] { "none", "sim" },
        ["payload"] = new[] { "none", "sim" },
    };

    public static AircraftFamily? FindFamily(string? id) =>
        Families.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The adapter for an aircraft: its family's procedures with the profile's overrides.</summary>
    /// <param name="family">Family to use instead of the profile's "adapter" (tests).</param>
    public static ComposedAircraftAdapter Create(AircraftProfile profile, ProcedureContext context, AircraftFamily? family = null) =>
        new(Compose(profile, context, family));

    /// <summary>
    /// Builds the procedures for a profile. Unknown or forbidden names fall back to "none" and are reported
    /// in <see cref="Composition.Warnings"/>, so a typo never makes Rampside guess.
    /// </summary>
    public static Composition Compose(AircraftProfile profile, ProcedureContext context, AircraftFamily? family = null)
    {
        var warnings = new List<string>();
        family ??= FindFamily(profile.Adapter);
        if (family is null)
        {
            warnings.Add($"Profile '{profile.Name}': unknown adapter '{profile.Adapter}', using '{DefaultFamily.Id}'.");
            family = DefaultFamily;
        }

        var set = family.Procedures.With(profile.Procedures);

        string Checked(string job, string? name)
        {
            name ??= "none";
            if (!Names[job].Contains(name))
            {
                warnings.Add($"Profile '{profile.Name}': unknown {job} procedure '{name}', using 'none'. Known: {string.Join(", ", Names[job])}.");
                return "none";
            }

            if (family.SdkOnly && name == "sim" && job is "fuel" or "payload")
            {
                warnings.Add($"Profile '{profile.Name}': {job} 'sim' would write sim variables, which the {family.Name} does not allow. Using 'none'.");
                return "none";
            }

            return name;
        }

        IEquipmentProcedure Equipment(GroundEquipment equipment, string job, string? name, ConnectableConfigEvent simEvent) =>
            Checked(job, name) switch
            {
                "sim" => new SimEventEquipment(context.Sim, equipment, simEvent.Resolve()),
                "pmdg-cdu" => new PmdgCduEquipment(context, equipment),
                _ => new NoEquipment(equipment),
            };

        var parts = new AircraftProcedures(
            Checked("doors", set.Doors) switch
            {
                "pmdg-sdk" => new PmdgSdkDoors(context.Pmdg, profile.DoorMap, context.Log),
                _ => new NoDoors(),
            },
            new Dictionary<GroundEquipment, IEquipmentProcedure>
            {
                [GroundEquipment.Jetway] = Equipment(GroundEquipment.Jetway, "jetway", set.Jetway, new(profile.Jetway.Event, SimEvents.ToggleJetway)),
                [GroundEquipment.Stairs] = Equipment(GroundEquipment.Stairs, "stairs", set.Stairs, new(profile.Stairs.Event, SimEvents.ToggleStairs)),
                [GroundEquipment.GroundPower] = Equipment(GroundEquipment.GroundPower, "groundPower", set.GroundPower, new(profile.Gpu.Event, SimEvents.RequestGroundPower)),
                [GroundEquipment.Chocks] = Equipment(GroundEquipment.Chocks, "chocks", set.Chocks, new(profile.Chocks.Event, string.Empty)),
            },
            Checked("fuel", set.Fuel) == "sim" ? new SimFuel(context.Sim) : new NoFuel(),
            Checked("payload", set.Payload) == "sim" ? new SimPayload(context.Sim, profile) : new NoPayload());

        return new Composition(family, parts, context, warnings);
    }

    /// <summary>Profile event name (null = Rampside default, empty = none).</summary>
    private readonly record struct ConnectableConfigEvent(string? Configured, string Default)
    {
        public string Resolve() => Configured ?? Default;
    }
}

/// <summary>Everything a <see cref="ComposedAircraftAdapter"/> is made of.</summary>
public sealed record Composition(AircraftFamily Family, AircraftProcedures Procedures, ProcedureContext Context, IReadOnlyList<string> Warnings);
