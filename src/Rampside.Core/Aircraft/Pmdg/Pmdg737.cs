using Rampside.Core.Profiles;

namespace Rampside.Core.Aircraft.Pmdg;

/// <summary>
/// What Rampside knows about the PMDG 737 (MSFS 2024). Rampside talks to it only through the PMDG SDK and its CDU,
/// as the PMDG EULA requires, never by writing sim variables.
/// </summary>
public static class Pmdg737
{
    /// <summary>
    /// Door mapping verified in MSFS 2024 with the PMDG 737-800 on 30 Oct 2026 (aircraft powered).
    /// Each light comes on within 0.5 s of the open event and goes off when the door is closed
    /// (L1 about 10 s, R1/L2/R2 about 5 s, cargo doors about 3 s).
    /// Not included: the airstair (its event extended the stairs once without power but did not retract them),
    /// the overwing exits (no reaction on the ground) and the equipment hatch (not a service door).
    /// The sim's own EXIT OPEN variables do not follow the PMDG doors.
    /// </summary>
    public static readonly IReadOnlyList<DoorMapping> DefaultDoorMap = new[]
    {
        new DoorMapping { Door = DoorId.ForwardLeft, Event = "EVT_DOOR_FWD_L", Indicator = "DOOR_annunFWD_ENTRY" },
        new DoorMapping { Door = DoorId.ForwardRight, Event = "EVT_DOOR_FWD_R", Indicator = "DOOR_annunFWD_SERVICE" },
        new DoorMapping { Door = DoorId.AftLeft, Event = "EVT_DOOR_AFT_L", Indicator = "DOOR_annunAFT_ENTRY" },
        new DoorMapping { Door = DoorId.AftRight, Event = "EVT_DOOR_AFT_R", Indicator = "DOOR_annunAFT_SERVICE" },
        new DoorMapping { Door = DoorId.CargoForward, Event = "EVT_DOOR_CARGO_FWD", Indicator = "DOOR_annunFWD_CARGO" },
        new DoorMapping { Door = DoorId.CargoAft, Event = "EVT_DOOR_CARGO_AFT", Indicator = "DOOR_annunAFT_CARGO" },
    };

    /// <summary>Door lights need DC power. Rampside requires any of DC BATT, DC STBY, DC 1, DC 2.</summary>
    public static readonly int[] DoorIndicationBuses = { 2, 3, 4, 5 };

    /// <summary>Rampside's stairs are the PMDG stairs at L1 and L2 (GROUND SERVICES page 3).</summary>
    public static readonly IReadOnlyList<PmdgServiceItem> StairsItems = new[]
    {
        new PmdgServiceItem(3, "STAIRS 1L", "L1"),
        new PmdgServiceItem(3, "STAIRS 2L", "L2"),
    };

    /// <summary>
    /// WHEEL CHOCKS at R6 on every GROUND SERVICES page. The key toggles; the selected option (SET or REMOVED) is
    /// shown in the large green font, the other in the small font. Readable only while the right CDU is.
    /// </summary>
    public static readonly PmdgServiceItem ChocksItem = new(0, "WHEEL CHOCKS", "R6", OnOption: "SET", OffOption: "REMOVED");

    /// <summary>GROUND POWER on GROUND SERVICES page 1 brings the PMDG GPU cart (SimObject PMDG_GPU).</summary>
    public static readonly PmdgServiceItem GroundPowerItem = new(1, "GROUND POWER", "L2");

    /// <summary>
    /// Lit while the GPU is plugged in and delivers power (the pilot still has to switch GRD PWR on).
    /// Reported also cold and dark.
    /// </summary>
    public const string GroundPowerIndicator = "ELEC_annunGRD_POWER_AVAILABLE";

    /// <summary>The PMDG stairs trucks are SimObjects with this title, e.g. PMDG_Stairs_1L.</summary>
    public const string StairsTitlePrefix = "PMDG_Stairs";

    /// <summary>Why the PMDG cannot be used yet, or null when its SDK data arrives.</summary>
    public static string? NotReadyReason(IPmdgLink link) => link.HasData
        ? null
        : link.Problem ?? "No data from the PMDG 737 yet. Add EnableDataBroadcast=1 under [SDK] in 737_Options.ini and reload the aircraft.";
}
