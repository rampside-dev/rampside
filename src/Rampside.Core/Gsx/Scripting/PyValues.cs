using System.Globalization;

namespace Rampside.Core.Gsx.Scripting;

/// <summary>Python's None. A real object (not null) so it can be a dict key, as in GSX's <c>None : ( )</c>.</summary>
public sealed class PyNone
{
    public static readonly PyNone Value = new();

    private PyNone()
    {
    }

    public override string ToString() => "None";
}

/// <summary>A name the script uses but does not define, written in capitals: GSX constants like GATE_A or PARKING.</summary>
public sealed record PySymbol(string Name)
{
    public override string ToString() => Name;
}

/// <summary>GSX's Distance object, as returned by a stop position function.</summary>
public sealed record PyDistance(double Meters);

/// <summary>A Python tuple, compared by value.</summary>
public sealed class PyTuple(IReadOnlyList<object> items) : IEquatable<PyTuple>
{
    public static readonly PyTuple Empty = new(Array.Empty<object>());

    public IReadOnlyList<object> Items { get; } = items;

    public bool Equals(PyTuple? other) =>
        other is not null && other.Items.Count == Items.Count && Items.Zip(other.Items).All(p => PyEquality.Instance.Equals(p.First, p.Second));

    public override bool Equals(object? obj) => obj is PyTuple t && Equals(t);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items)
        {
            hash.Add(PyEquality.Instance.GetHashCode(item));
        }

        return hash.ToHashCode();
    }
}

public sealed class PyList(List<object> items)
{
    public List<object> Items { get; } = items;
}

public sealed class PyDict
{
    public Dictionary<object, object> Items { get; } = new(PyEquality.Instance);
}

/// <summary>An object with attributes (aircraftData, the Distance class) and, optionally, a call.</summary>
public sealed class PyObject(string typeName)
{
    public string TypeName { get; } = typeName;

    public Dictionary<string, object> Attributes { get; } = new(StringComparer.Ordinal);

    public Func<IReadOnlyList<object>, object>? Invoke { get; init; }

    public override string ToString() => $"<{TypeName}>";
}

/// <summary>A function defined in the script.</summary>
public sealed class PyFunction
{
    internal PyFunction(DefStmt definition, IReadOnlyList<object?> defaults)
    {
        Definition = definition;
        Defaults = defaults;
    }

    internal DefStmt Definition { get; }

    public string Name => Definition.Name;

    /// <summary>Default values of the parameters, null where there is none.</summary>
    internal IReadOnlyList<object?> Defaults { get; }

    /// <summary>Marked with GSX's <c>@AlternativeStopPositions</c>.</summary>
    public bool IsStopPositionFunction { get; set; }

    public override string ToString() => $"<function {Name}>";
}

/// <summary>A built-in function such as len or CustomizedName.</summary>
public sealed record PyBuiltin(string Name, Func<IReadOnlyList<object>, object> Call);

/// <summary>A Python exception raised while a script runs; <see cref="TypeName"/> is matched by <c>except</c>.</summary>
public class PyError(string typeName, string message) : Exception($"{typeName}: {message}")
{
    public string TypeName { get; } = typeName;
}

/// <summary>Python's ==: numbers by value (True == 1), strings by ordinal, tuples by their items, everything else by identity.</summary>
internal sealed class PyEquality : IEqualityComparer<object>
{
    public static readonly PyEquality Instance = new();

    public new bool Equals(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (Py.AsNumber(x) is { } a && Py.AsNumber(y) is { } b)
        {
            return a == b;
        }

        return x switch
        {
            string s => y is string t && string.Equals(s, t, StringComparison.Ordinal),
            PyTuple t => t.Equals(y),
            PySymbol s => s.Equals(y),
            PyDistance d => d.Equals(y),
            _ => false,
        };
    }

    public int GetHashCode(object obj) => Py.AsNumber(obj) is { } n ? n.GetHashCode() : obj switch
    {
        string s => StringComparer.Ordinal.GetHashCode(s),
        _ => obj.GetHashCode(),
    };
}

internal static class Py
{
    public static double? AsNumber(object? value) => value switch
    {
        double d => d,
        bool b => b ? 1 : 0,
        _ => null,
    };

    public static bool Truthy(object value) => value switch
    {
        PyNone => false,
        bool b => b,
        double d => d != 0,
        string s => s.Length > 0,
        PyTuple t => t.Items.Count > 0,
        PyList l => l.Items.Count > 0,
        PyDict d => d.Items.Count > 0,
        _ => true,
    };

    public static string TypeName(object value) => value switch
    {
        PyNone => "NoneType",
        bool => "bool",
        double d => d == Math.Floor(d) && Math.Abs(d) < 1e15 ? "int" : "float",
        string => "str",
        PyTuple => "tuple",
        PyList => "list",
        PyDict => "dict",
        PyFunction or PyBuiltin => "function",
        PyObject o => o.TypeName,
        PyDistance => "Distance",
        PySymbol => "symbol",
        _ => value.GetType().Name,
    };

    /// <summary>str(): integers without a decimal point, like Python ints.</summary>
    public static string Str(object value) => value switch
    {
        double d when d == Math.Floor(d) && Math.Abs(d) < 1e15 => ((long)d).ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        string s => s,
        PyTuple t => "(" + string.Join(", ", t.Items.Select(Repr)) + (t.Items.Count == 1 ? ",)" : ")"),
        PyList l => "[" + string.Join(", ", l.Items.Select(Repr)) + "]",
        PyDict d => "{" + string.Join(", ", d.Items.Select(p => $"{Repr(p.Key)}: {Repr(p.Value)}")) + "}",
        _ => value.ToString() ?? string.Empty,
    };

    public static string Repr(object value) => value is string s ? $"'{s.Replace("'", "\\'")}'" : Str(value);
}
