using Rampside.Core.Gsx.Scripting;

namespace Rampside.Core.Gsx;

/// <summary>How far a GSX script moves a stand's stop position for one aircraft.</summary>
/// <param name="Meters">As the script returns it. Positive = the aircraft stops that far short of the profile's stop position.</param>
/// <param name="Function">The script function that gave it, e.g. "customOffset14".</param>
public sealed record GsxStopOffset(double Meters, string Function, GsxAircraft Aircraft);

/// <summary>
/// The per-aircraft stop positions of a GSX airport script (<c>&lt;icao&gt;-&lt;name&gt;.py</c>, next to the profile's .ini).
/// The script's <c>parkings</c> table names, per stand, a function marked <c>@AlternativeStopPositions</c> that GSX
/// calls with the aircraft and that returns a <c>Distance</c>:
/// <code>
/// parkings = {
///     GATE_A : {
///         None : ( ),                                  # default for the other GATE_A stands
///         113 : (Apron1GateB, customOffset14, ),       # [gate a 113] in the .ini
///     },
/// }
/// </code>
/// </summary>
public sealed class GsxStopOffsets
{
    private readonly PyModule _module;
    private readonly Dictionary<string, PyFunction?> _stands;
    private readonly Dictionary<string, PyFunction?> _groupDefaults;

    private GsxStopOffsets(string name, PyModule module, Dictionary<string, PyFunction?> stands, Dictionary<string, PyFunction?> groupDefaults)
    {
        Name = name;
        _module = module;
        _stands = stands;
        _groupDefaults = groupDefaults;
    }

    public string Name { get; }

    /// <summary>Stands (ini section names) listed in the script's parkings table.</summary>
    public IReadOnlyCollection<string> Stands => _stands.Keys;

    /// <summary>Stands with a stop position function, counting group defaults once.</summary>
    public int StandsWithFunction => _stands.Values.Count(f => f is not null) + _groupDefaults.Values.Count(f => f is not null);

    /// <summary>Reads a script. Broken or failing parts are skipped and listed in <paramref name="problems"/>.</summary>
    public static GsxStopOffsets Parse(string name, string text, ICollection<string>? problems = null)
    {
        var module = PyModule.Load(text, problems);
        var stands = new Dictionary<string, PyFunction?>(StringComparer.OrdinalIgnoreCase);
        var defaults = new Dictionary<string, PyFunction?>(StringComparer.OrdinalIgnoreCase);
        switch (module.Get("parkings"))
        {
            case null:
                break; // a script with names only, or none at all
            case PyDict parkings:
                foreach (var (groupKey, groupValue) in parkings.Items)
                {
                    if (groupValue is not PyDict group)
                    {
                        problems?.Add($"parkings[{Py.Str(groupKey)}] is not a dict");
                        continue;
                    }

                    var prefix = GroupPrefix(groupKey);
                    foreach (var (standKey, entry) in group.Items)
                    {
                        var function = StopFunction(entry);
                        if (standKey is PyNone)
                        {
                            defaults[prefix] = function;
                        }
                        else
                        {
                            stands[$"{prefix} {Py.Str(standKey).ToLowerInvariant()}"] = function;
                        }
                    }
                }

                break;
            default:
                problems?.Add("parkings is not a dict");
                break;
        }

        return new GsxStopOffsets(name, module, stands, defaults);
    }

    /// <summary>GATE_A → "gate a", N_PARKING → "n parking", as GSX writes them in the .ini section names.</summary>
    private static string GroupPrefix(object key) => Py.Str(key).Replace('_', ' ').Trim().ToLowerInvariant();

    /// <summary>The first function in a stand's tuple, preferring one marked @AlternativeStopPositions.</summary>
    private static PyFunction? StopFunction(object entry)
    {
        var items = entry switch
        {
            PyTuple t => t.Items,
            PyFunction f => [f],
            _ => Array.Empty<object>(),
        };
        var functions = items.OfType<PyFunction>().ToList();
        return functions.FirstOrDefault(f => f.IsStopPositionFunction) ?? functions.FirstOrDefault();
    }

    /// <summary>The stop position function for an .ini section ("gate a 113"), or null when the stand has none.</summary>
    public PyFunction? FunctionFor(string section)
    {
        var key = string.Join(' ', section.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (_stands.TryGetValue(key, out var function))
        {
            return function;
        }

        var lastSpace = key.LastIndexOf(' ');
        return lastSpace > 0 ? _groupDefaults.GetValueOrDefault(key[..lastSpace]) : null;
    }

    /// <summary>
    /// The script's stop offset for <paramref name="aircraft"/> at <paramref name="section"/>; null when the stand has no
    /// stop position function or the function fails (GSX then uses the profile's stop position too, e.g. when a table
    /// has no entry for the aircraft and <c>Distance.fromMeters(None)</c> fails). Failures go to <paramref name="problems"/>.
    /// </summary>
    public GsxStopOffset? For(string section, GsxAircraft aircraft, ICollection<string>? problems = null)
    {
        if (FunctionFor(section) is not { } function)
        {
            return null;
        }

        var data = new PyObject("AircraftData");
        data.Attributes["idMajor"] = (double)aircraft.IdMajor;
        data.Attributes["idMinor"] = (double)aircraft.IdMinor;
        data.Attributes["icaoTypeDesignator"] = (object?)aircraft.IcaoTypeDesignator ?? PyNone.Value;
        data.Attributes["aircraftGroup"] = (object?)aircraft.AircraftGroup ?? PyNone.Value;
        try
        {
            switch (_module.Call(function, data))
            {
                case PyDistance distance:
                    return new GsxStopOffset(distance.Meters, function.Name, aircraft);
                case var other:
                    problems?.Add($"{function.Name} returned {Py.Repr(other)} for {aircraft}, not a Distance");
                    return null;
            }
        }
        catch (PyError ex)
        {
            problems?.Add($"{function.Name} failed for {aircraft}: {ex.Message}");
            return null;
        }
    }
}
