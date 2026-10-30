namespace Rampside.Core.Gsx;

/// <summary>
/// The aircraft as GSX scripts see it (<c>aircraftData</c>): GSX's type number (737), variant (800),
/// the ICAO type designator and the ICAO aerodrome reference code ("ARC-C").
/// </summary>
public sealed record GsxAircraft(int IdMajor, int IdMinor, string? IcaoTypeDesignator, string? AircraftGroup)
{
    /// <summary>
    /// GSX ids by ICAO type, read off the tables in GSX airport scripts (e.g. <c>table737 = {800: 0, 900: 2}</c>,
    /// <c>20000: 3.2, # concorde</c>). Minor 0 where the scripts only use the type number.
    /// </summary>
    private static readonly Dictionary<string, (int Major, int Minor, double WingspanM)> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A318"] = (318, 0, 34.1),
        ["A319"] = (319, 0, 35.8),
        ["A19N"] = (319, 0, 35.8),
        ["A320"] = (320, 0, 35.8),
        ["A20N"] = (320, 0, 35.8),
        ["A321"] = (321, 0, 35.8),
        ["A21N"] = (321, 0, 35.8),
        ["A306"] = (300, 0, 44.8),
        ["A30B"] = (300, 0, 44.8),
        ["A310"] = (310, 0, 43.9),
        ["A332"] = (330, 200, 60.3),
        ["A333"] = (330, 300, 60.3),
        ["A338"] = (330, 800, 64),
        ["A339"] = (330, 900, 64),
        ["A342"] = (340, 200, 60.3),
        ["A343"] = (340, 300, 60.3),
        ["A345"] = (340, 500, 63.5),
        ["A346"] = (340, 600, 63.5),
        ["A359"] = (350, 900, 64.8),
        ["A35K"] = (350, 1000, 64.8),
        ["A388"] = (380, 0, 79.8),
        ["B731"] = (737, 100, 28.4),
        ["B732"] = (737, 200, 28.4),
        ["B733"] = (737, 300, 28.9),
        ["B734"] = (737, 400, 28.9),
        ["B735"] = (737, 500, 28.9),
        ["B736"] = (737, 600, 34.3),
        ["B737"] = (737, 700, 35.8),
        ["B738"] = (737, 800, 35.8),
        ["B739"] = (737, 900, 35.8),
        ["B37M"] = (737, 700, 35.9),
        ["B38M"] = (737, 800, 35.9),
        ["B39M"] = (737, 900, 35.9),
        ["B744"] = (747, 400, 64.4),
        ["B748"] = (747, 8, 68.4),
        ["B752"] = (757, 200, 38.1),
        ["B753"] = (757, 300, 38.1),
        ["B762"] = (767, 200, 47.6),
        ["B763"] = (767, 300, 47.6),
        ["B764"] = (767, 400, 51.9),
        ["B772"] = (777, 200, 60.9),
        ["B77L"] = (777, 200, 64.8),
        ["B773"] = (777, 300, 60.9),
        ["B77W"] = (777, 300, 64.8),
        ["B779"] = (777, 9, 64.8), // folded wing tips on the ground
        ["B788"] = (787, 8, 60.1),
        ["B789"] = (787, 9, 60.1),
        ["B78X"] = (787, 10, 60.1),
        ["B712"] = (717, 0, 28.4),
        ["B721"] = (727, 0, 32.9),
        ["B722"] = (727, 0, 32.9),
        ["DC10"] = (10, 0, 50.4),
        ["MD11"] = (11, 0, 51.7),
        ["MD81"] = (81, 0, 32.9),
        ["MD82"] = (82, 0, 32.9),
        ["MD83"] = (83, 0, 32.9),
        ["MD87"] = (87, 0, 32.9),
        ["MD88"] = (88, 0, 32.9),
        ["MD90"] = (90, 0, 32.9),
        ["E170"] = (170, 0, 26),
        ["E75L"] = (175, 0, 28.7),
        ["E75S"] = (175, 0, 26),
        ["E175"] = (175, 0, 26),
        ["E190"] = (190, 0, 28.7),
        ["E195"] = (195, 0, 28.7),
        ["E290"] = (290, 0, 33.7),
        ["E295"] = (295, 0, 35.1),
        ["AT43"] = (42, 0, 24.6),
        ["AT45"] = (42, 0, 24.6),
        ["AT46"] = (42, 0, 24.6),
        ["AT72"] = (72, 0, 27.1),
        ["AT75"] = (72, 0, 27.1),
        ["AT76"] = (72, 0, 27.1),
        ["BCS1"] = (221, 0, 35.1),
        ["BCS3"] = (223, 0, 35.1),
        ["B461"] = (146, 0, 26.3),
        ["B462"] = (146, 0, 26.3),
        ["B463"] = (146, 0, 26.3),
        ["CONC"] = (20000, 0, 25.6),
    };

    /// <summary>The aircraft for an ICAO type designator, or null when GSX's id for it is not known here.</summary>
    public static GsxAircraft? FromIcaoType(string? icaoType)
    {
        if (string.IsNullOrWhiteSpace(icaoType) || !Types.TryGetValue(icaoType.Trim(), out var type))
        {
            return null;
        }

        return new GsxAircraft(type.Major, type.Minor, icaoType.Trim().ToUpperInvariant(), ReferenceCode(type.WingspanM));
    }

    /// <summary>
    /// The aircraft for the first of <paramref name="icaoTypes"/> GSX knows; <paramref name="idMajor"/> and
    /// <paramref name="idMinor"/> (from the aircraft profile) replace the ids from the table.
    /// </summary>
    public static GsxAircraft? From(IEnumerable<string> icaoTypes, int? idMajor = null, int? idMinor = null)
    {
        var types = icaoTypes.ToList();
        var known = types.Select(FromIcaoType).FirstOrDefault(a => a is not null);
        if (idMajor is { } major)
        {
            return new GsxAircraft(major, idMinor ?? 0, known?.IcaoTypeDesignator ?? types.FirstOrDefault()?.ToUpperInvariant(), known?.AircraftGroup);
        }

        return known;
    }

    /// <summary>ICAO aerodrome reference code letter by wingspan, written as GSX scripts compare it ("ARC-C").</summary>
    public static string ReferenceCode(double wingspanM) => "ARC-" + wingspanM switch
    {
        < 15 => "A",
        < 24 => "B",
        < 36 => "C",
        < 52 => "D",
        < 65 => "E",
        _ => "F",
    };

    public override string ToString() => $"{IcaoTypeDesignator ?? "aircraft"} (GSX {IdMajor}/{IdMinor})";
}
