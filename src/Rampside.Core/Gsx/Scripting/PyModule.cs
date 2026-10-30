using System.Globalization;
using System.Text;

namespace Rampside.Core.Gsx.Scripting;

/// <summary>
/// A GSX airport script (<c>&lt;icao&gt;-&lt;name&gt;.py</c>) run by a small interpreter for the part of Python 2 the
/// scripts use. The script only sees its own names, a few harmless built-ins and what GSX itself provides
/// (Distance, CustomizedName, @AlternativeStopPositions); there are no imports, files, loops or classes,
/// and every run has a step budget and a call depth limit. Numbers are doubles: <c>7 / 2</c> is 3.5, not Python 2's 3.
/// </summary>
public sealed class PyModule
{
    public const int MaxCallDepth = 64;

    /// <summary>Steps (evaluated expressions and statements) the module body may take; real scripts need well under 1 %.</summary>
    public const int ModuleStepBudget = 5_000_000;

    /// <summary>Steps one function call from outside may take.</summary>
    public const int CallStepBudget = 200_000;

    private static readonly IReadOnlyDictionary<string, object> Builtins = CreateBuiltins();

    private readonly Dictionary<string, object> _globals = new(StringComparer.Ordinal);
    private long _steps;
    private int _depth;

    private PyModule()
    {
    }

    /// <summary>
    /// Runs the script's top level. Statements that cannot be parsed or fail are skipped and listed in
    /// <paramref name="problems"/>; the rest of the script still runs, as far as it does not depend on them.
    /// </summary>
    public static PyModule Load(string text, ICollection<string>? problems = null)
    {
        var module = new PyModule();
        List<Stmt> statements;
        try
        {
            statements = PyParser.ParseModule(text, problems);
        }
        catch (PySyntaxError ex)
        {
            problems?.Add(ex.Message);
            return module;
        }

        module._steps = ModuleStepBudget;
        var frame = new Frame(null);
        foreach (var statement in statements)
        {
            try
            {
                module.Exec(statement, frame);
            }
            catch (PyLimitError ex)
            {
                problems?.Add($"line {statement.Line}: {ex.Message}; the rest of the script is skipped");
                break;
            }
            catch (PyError ex)
            {
                problems?.Add($"line {statement.Line}: {ex.Message}");
            }
        }

        return module;
    }

    /// <summary>A top-level name of the script, or null.</summary>
    public object? Get(string name) => _globals.GetValueOrDefault(name);

    public IReadOnlyDictionary<string, object> Globals => _globals;

    /// <summary>Calls a function of the script. Throws <see cref="PyError"/> when the script raises one.</summary>
    public object Call(object function, params object[] args)
    {
        _steps = CallStepBudget;
        _depth = 0;
        return CallValue(function, args, 0);
    }

    private sealed class Frame(Dictionary<string, object>? locals)
    {
        /// <summary>Null at module level, where names live in the globals.</summary>
        public Dictionary<string, object>? Locals { get; } = locals;

        public HashSet<string> DeclaredGlobal { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Running out of steps or call depth; not caught by the script's <c>except</c>.</summary>
    private sealed class PyLimitError(string message) : PyError("RuntimeError", message);

    private readonly record struct Flow(bool Returned, object Value)
    {
        public static readonly Flow Normal = new(false, PyNone.Value);
    }

    private void Step()
    {
        if (--_steps < 0)
        {
            throw new PyLimitError("the script takes too many steps");
        }
    }

    private Flow ExecBlock(IReadOnlyList<Stmt> statements, Frame frame)
    {
        foreach (var statement in statements)
        {
            var flow = Exec(statement, frame);
            if (flow.Returned)
            {
                return flow;
            }
        }

        return Flow.Normal;
    }

    private Flow Exec(Stmt statement, Frame frame)
    {
        Step();
        switch (statement)
        {
            case ExprStmt s:
                Eval(s.Value, frame);
                return Flow.Normal;
            case AssignStmt s:
                var value = Eval(s.Value, frame);
                foreach (var target in s.Targets)
                {
                    Assign(target, value, frame);
                }

                return Flow.Normal;
            case AugAssignStmt s:
                Assign(s.Target, Binary(s.Op, Eval(s.Target, frame), Eval(s.Value, frame)), frame);
                return Flow.Normal;
            case ReturnStmt s:
                return new Flow(true, s.Value is null ? PyNone.Value : Eval(s.Value, frame));
            case IfStmt s:
                return ExecBlock(Py.Truthy(Eval(s.Test, frame)) ? s.Body : s.Else, frame);
            case TryStmt s:
                return ExecTry(s, frame);
            case DefStmt s:
                Store(s.Name, Define(s, frame), frame);
                return Flow.Normal;
            case GlobalStmt s:
                frame.DeclaredGlobal.UnionWith(s.Names);
                return Flow.Normal;
            case PassStmt:
                return Flow.Normal;
            default:
                throw new PyError("SyntaxError", "unsupported statement");
        }
    }

    private Flow ExecTry(TryStmt s, Frame frame)
    {
        Flow flow;
        try
        {
            try
            {
                flow = ExecBlock(s.Body, frame);
            }
            catch (PyError ex) when (ex is not PyLimitError && s.Handlers.FirstOrDefault(h => Catches(h, ex)) is { } handler)
            {
                if (handler.Variable is { } variable)
                {
                    Store(variable, ex.Message, frame);
                }

                return ExecBlock(handler.Body, frame);
            }

            if (!flow.Returned && s.Else.Count > 0)
            {
                flow = ExecBlock(s.Else, frame);
            }
        }
        finally
        {
            if (s.Finally.Count > 0)
            {
                var final = ExecBlock(s.Finally, frame);
                if (final.Returned)
                {
                    flow = final;
                }
            }
        }

        return flow;
    }

    /// <summary>Exception types are matched by name; there are no exception classes to evaluate.</summary>
    private static bool Catches(ExceptClause handler, PyError error)
    {
        if (handler.Types is null)
        {
            return true;
        }

        var names = handler.Types switch
        {
            NameExpr n => new[] { n.Name },
            TupleExpr t => t.Items.OfType<NameExpr>().Select(n => n.Name).ToArray(),
            _ => Array.Empty<string>(),
        };
        return names.Any(name => name switch
        {
            "Exception" or "BaseException" or "StandardError" => true,
            "LookupError" => error.TypeName is "KeyError" or "IndexError",
            "ArithmeticError" => error.TypeName is "ZeroDivisionError" or "OverflowError",
            _ => name == error.TypeName,
        });
    }

    private object Define(DefStmt s, Frame frame)
    {
        var defaults = s.Defaults.Select(d => d is null ? null : Eval(d, frame)).ToArray();
        var decorators = s.Decorators.Select(d => Eval(d, frame)).ToList();
        object function = new PyFunction(s, defaults);
        for (var i = decorators.Count - 1; i >= 0; i--)
        {
            function = CallValue(decorators[i], [function], s.Line);
        }

        return function;
    }

    private void Assign(Expr target, object value, Frame frame)
    {
        switch (target)
        {
            case NameExpr n:
                Store(n.Name, value, frame);
                break;
            case TupleExpr or ListExpr:
                var targets = target is TupleExpr t ? t.Items : ((ListExpr)target).Items;
                var items = value switch
                {
                    PyTuple tuple => tuple.Items,
                    PyList list => list.Items,
                    _ => throw new PyError("TypeError", $"cannot unpack {Py.TypeName(value)}"),
                };
                if (items.Count != targets.Count)
                {
                    throw new PyError("ValueError", $"expected {targets.Count} values to unpack, got {items.Count}");
                }

                for (var i = 0; i < targets.Count; i++)
                {
                    Assign(targets[i], items[i], frame);
                }

                break;
            case SubscriptExpr s:
                var container = Eval(s.Target, frame);
                var index = Eval(s.Index, frame);
                switch (container)
                {
                    case PyDict dict:
                        dict.Items[index] = value;
                        break;
                    case PyList list:
                        list.Items[Index(index, list.Items.Count)] = value;
                        break;
                    default:
                        throw new PyError("TypeError", $"'{Py.TypeName(container)}' object does not support item assignment");
                }

                break;
        }
    }

    private void Store(string name, object value, Frame frame)
    {
        if (frame.Locals is null || frame.DeclaredGlobal.Contains(name))
        {
            _globals[name] = value;
        }
        else
        {
            frame.Locals[name] = value;
        }
    }

    private object Lookup(string name, Frame frame)
    {
        if (frame.Locals is not null && !frame.DeclaredGlobal.Contains(name) && frame.Locals.TryGetValue(name, out var local))
        {
            return local;
        }

        if (_globals.TryGetValue(name, out var global) || Builtins.TryGetValue(name, out global))
        {
            return global;
        }

        if (name.Length > 1 && char.IsUpper(name[0]) && name.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_'))
        {
            return new PySymbol(name); // GSX constants: GATE_A, PARKING, N_PARKING, ...
        }

        throw new PyError("NameError", $"name '{name}' is not defined");
    }

    private object Eval(Expr expr, Frame frame)
    {
        Step();
        switch (expr)
        {
            case ConstExpr c:
                return c.Value;
            case NameExpr n:
                return Lookup(n.Name, frame);
            case TupleExpr t:
                return new PyTuple(t.Items.Select(i => Eval(i, frame)).ToArray());
            case ListExpr l:
                return new PyList(l.Items.Select(i => Eval(i, frame)).ToList());
            case DictExpr d:
                var dict = new PyDict();
                foreach (var (key, value) in d.Items)
                {
                    var k = Eval(key, frame);
                    if (k is PyList or PyDict)
                    {
                        throw new PyError("TypeError", $"unhashable type: '{Py.TypeName(k)}'");
                    }

                    dict.Items[k] = Eval(value, frame);
                }

                return dict;
            case AttrExpr a:
                return Attribute(Eval(a.Target, frame), a.Name);
            case CallExpr c:
                var function = Eval(c.Function, frame);
                var args = c.Args.Select(arg => Eval(arg, frame)).ToArray();
                return CallValue(function, args, c.Line);
            case SubscriptExpr s:
                return Subscript(Eval(s.Target, frame), Eval(s.Index, frame));
            case UnaryExpr u:
                var operand = Eval(u.Operand, frame);
                return u.Op switch
                {
                    "not" => !Py.Truthy(operand),
                    "-" => -(Py.AsNumber(operand) ?? throw new PyError("TypeError", $"bad operand type for unary -: '{Py.TypeName(operand)}'")),
                    _ => Py.AsNumber(operand) ?? throw new PyError("TypeError", $"bad operand type for unary +: '{Py.TypeName(operand)}'"),
                };
            case BinaryExpr b:
                return Binary(b.Op, Eval(b.Left, frame), Eval(b.Right, frame));
            case CompareExpr c:
                var left = Eval(c.First, frame);
                foreach (var (op, rightExpr) in c.Rest)
                {
                    var right = Eval(rightExpr, frame);
                    if (!Compare(op, left, right))
                    {
                        return false;
                    }

                    left = right;
                }

                return true;
            case BoolExpr b:
                var first = Eval(b.Left, frame);
                return Py.Truthy(first) == b.IsAnd ? Eval(b.Right, frame) : first;
            case ConditionalExpr c:
                return Eval(Py.Truthy(Eval(c.Test, frame)) ? c.Then : c.Else, frame);
            default:
                throw new PyError("SyntaxError", "unsupported expression");
        }
    }

    private object CallValue(object function, IReadOnlyList<object> args, int line)
    {
        switch (function)
        {
            case PyFunction f:
                return CallFunction(f, args);
            case PyBuiltin b:
                return b.Call(args);
            case PyObject { Invoke: { } invoke }:
                return invoke(args);
            default:
                throw new PyError("TypeError", $"'{Py.TypeName(function)}' object is not callable");
        }
    }

    private object CallFunction(PyFunction function, IReadOnlyList<object> args)
    {
        var parameters = function.Definition.Parameters;
        if (args.Count > parameters.Count)
        {
            throw new PyError("TypeError", $"{function.Name}() takes {parameters.Count} arguments ({args.Count} given)");
        }

        if (++_depth > MaxCallDepth)
        {
            _depth = 0;
            throw new PyLimitError("maximum call depth exceeded");
        }

        try
        {
            var locals = new Dictionary<string, object>(StringComparer.Ordinal);
            for (var i = 0; i < parameters.Count; i++)
            {
                locals[parameters[i]] = i < args.Count
                    ? args[i]
                    : function.Defaults[i] ?? throw new PyError("TypeError", $"{function.Name}() takes {parameters.Count} arguments ({args.Count} given)");
            }

            return ExecBlock(function.Definition.Body, new Frame(locals)).Value;
        }
        finally
        {
            _depth = Math.Max(0, _depth - 1);
        }
    }

    private static object Attribute(object target, string name)
    {
        switch (target)
        {
            case PyObject o when o.Attributes.TryGetValue(name, out var value):
                return value;
            case PyDict d:
                return name switch
                {
                    "get" => Method("get", a => a.Count is 1 or 2
                        ? d.Items.GetValueOrDefault(a[0]) ?? (a.Count == 2 ? a[1] : PyNone.Value)
                        : throw Arguments("get", "1 or 2", a.Count)),
                    "has_key" => Method("has_key", a => a.Count == 1 ? d.Items.ContainsKey(a[0]) : throw Arguments("has_key", "1", a.Count)),
                    "keys" => Method("keys", _ => new PyList(d.Items.Keys.ToList())),
                    "values" => Method("values", _ => new PyList(d.Items.Values.ToList())),
                    "items" => Method("items", _ => new PyList(d.Items.Select(p => (object)new PyTuple([p.Key, p.Value])).ToList())),
                    _ => throw NoAttribute(target, name),
                };
            case PyList l when name == "append":
                return Method("append", a =>
                {
                    if (l.Items.Count >= 100_000)
                    {
                        throw new PyLimitError("list too long");
                    }

                    l.Items.Add(a.Count == 1 ? a[0] : throw Arguments("append", "1", a.Count));
                    return PyNone.Value;
                });
            case string s:
                return name switch
                {
                    "upper" => Method(name, _ => s.ToUpperInvariant()),
                    "lower" => Method(name, _ => s.ToLowerInvariant()),
                    "strip" => Method(name, _ => s.Trim()),
                    "startswith" => Method(name, a => a.Count == 1 && a[0] is string p ? s.StartsWith(p, StringComparison.Ordinal) : throw Arguments(name, "1", a.Count)),
                    "endswith" => Method(name, a => a.Count == 1 && a[0] is string p ? s.EndsWith(p, StringComparison.Ordinal) : throw Arguments(name, "1", a.Count)),
                    _ => throw NoAttribute(target, name),
                };
            default:
                throw NoAttribute(target, name);
        }
    }

    private static PyBuiltin Method(string name, Func<IReadOnlyList<object>, object> call) => new(name, call);

    private static PyError Arguments(string name, string expected, int given) =>
        new("TypeError", $"{name}() takes {expected} arguments ({given} given)");

    private static PyError NoAttribute(object target, string name) =>
        new("AttributeError", $"'{Py.TypeName(target)}' object has no attribute '{name}'");

    private static object Subscript(object target, object index)
    {
        switch (target)
        {
            case PyDict d:
                return d.Items.TryGetValue(index, out var value) ? value : throw new PyError("KeyError", Py.Repr(index));
            case PyTuple t:
                return t.Items[Index(index, t.Items.Count)];
            case PyList l:
                return l.Items[Index(index, l.Items.Count)];
            case string s:
                return s[Index(index, s.Length)].ToString();
            default:
                throw new PyError("TypeError", $"'{Py.TypeName(target)}' object is not subscriptable");
        }
    }

    private static int Index(object index, int count)
    {
        if (Py.AsNumber(index) is not { } n || n != Math.Floor(n))
        {
            throw new PyError("TypeError", "indices must be integers");
        }

        var i = (int)(n < 0 ? n + count : n);
        return i >= 0 && i < count ? i : throw new PyError("IndexError", "index out of range");
    }

    private static object Binary(string op, object left, object right)
    {
        if (Py.AsNumber(left) is { } a && Py.AsNumber(right) is { } b && left is not string)
        {
            return op switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                "/" => b == 0 ? throw new PyError("ZeroDivisionError", "division by zero") : a / b,
                "//" => b == 0 ? throw new PyError("ZeroDivisionError", "division by zero") : Math.Floor(a / b),
                "%" => b == 0 ? throw new PyError("ZeroDivisionError", "modulo by zero") : a - b * Math.Floor(a / b),
                "**" => Math.Pow(a, b),
                _ => throw new PyError("TypeError", $"unsupported operator {op}"),
            };
        }

        return (op, left, right) switch
        {
            ("+", string x, string y) => x + y,
            ("+", PyTuple x, PyTuple y) => new PyTuple(x.Items.Concat(y.Items).ToArray()),
            ("+", PyList x, PyList y) => new PyList(x.Items.Concat(y.Items).ToList()),
            ("%", string format, _) => Format(format, right is PyTuple t ? t.Items : [right]),
            _ => throw new PyError("TypeError", $"unsupported operand types for {op}: '{Py.TypeName(left)}' and '{Py.TypeName(right)}'"),
        };
    }

    /// <summary>printf-style formatting: %s, %r, %d, %i, %f (with precision), %%.</summary>
    private static string Format(string format, IReadOnlyList<object> args)
    {
        var result = new StringBuilder();
        var next = 0;
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%')
            {
                result.Append(format[i]);
                continue;
            }

            var start = ++i;
            while (i < format.Length && (char.IsDigit(format[i]) || format[i] is '.' or '-' or '+' or ' ' or '0'))
            {
                i++;
            }

            if (i >= format.Length)
            {
                throw new PyError("ValueError", "incomplete format");
            }

            var spec = format[start..i];
            var kind = format[i];
            if (kind == '%')
            {
                result.Append('%');
                continue;
            }

            if (next >= args.Count)
            {
                throw new PyError("TypeError", "not enough arguments for format string");
            }

            var arg = args[next++];
            var precision = spec.Contains('.') && int.TryParse(spec[(spec.IndexOf('.') + 1)..], out var p) ? p : (int?)null;
            result.Append(kind switch
            {
                's' => Py.Str(arg),
                'r' => Py.Repr(arg),
                'd' or 'i' => ((long)Math.Truncate(Py.AsNumber(arg) ?? throw new PyError("TypeError", "%d format: a number is required"))).ToString(CultureInfo.InvariantCulture),
                'f' => (Py.AsNumber(arg) ?? throw new PyError("TypeError", "float argument required")).ToString("F" + (precision ?? 6), CultureInfo.InvariantCulture),
                _ => throw new PyError("ValueError", $"unsupported format character '{kind}'"),
            });
        }

        if (next < args.Count)
        {
            throw new PyError("TypeError", "not all arguments converted during string formatting");
        }

        return result.ToString();
    }

    private static bool Compare(string op, object left, object right)
    {
        switch (op)
        {
            case "==":
                return PyEquality.Instance.Equals(left, right);
            case "!=":
                return !PyEquality.Instance.Equals(left, right);
            case "is":
                return Identical(left, right);
            case "is not":
                return !Identical(left, right);
            case "in":
                return Contains(right, left);
            case "not in":
                return !Contains(right, left);
        }

        int order;
        if (Py.AsNumber(left) is { } a && Py.AsNumber(right) is { } b)
        {
            order = a.CompareTo(b);
        }
        else if (left is string x && right is string y)
        {
            order = string.CompareOrdinal(x, y);
        }
        else
        {
            throw new PyError("TypeError", $"cannot compare '{Py.TypeName(left)}' and '{Py.TypeName(right)}'");
        }

        return op switch
        {
            "<" => order < 0,
            ">" => order > 0,
            "<=" => order <= 0,
            _ => order >= 0,
        };
    }

    private static bool Identical(object left, object right) =>
        ReferenceEquals(left, right) || (left is double or bool && right is double or bool && PyEquality.Instance.Equals(left, right));

    private static bool Contains(object container, object item) => container switch
    {
        PyDict d => d.Items.ContainsKey(item),
        PyTuple t => t.Items.Contains(item, PyEquality.Instance),
        PyList l => l.Items.Contains(item, PyEquality.Instance),
        string s when item is string sub => s.Contains(sub, StringComparison.Ordinal),
        _ => throw new PyError("TypeError", $"argument of type '{Py.TypeName(container)}' is not iterable"),
    };

    private static IReadOnlyDictionary<string, object> CreateBuiltins()
    {
        var distance = new PyObject("Distance")
        {
            Invoke = a => a.Count == 0 ? new PyDistance(0) : new PyDistance(Number(a, 0, "Distance")),
        };
        distance.Attributes["fromMeters"] = Method("fromMeters", a => new PyDistance(Number(a, 1, "fromMeters")));
        distance.Attributes["fromFeet"] = Method("fromFeet", a => new PyDistance(Number(a, 1, "fromFeet") * 0.3048));

        var builtins = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Distance"] = distance,
            ["AlternativeStopPositions"] = Method("AlternativeStopPositions", a =>
            {
                if (a.Count != 1 || a[0] is not PyFunction function)
                {
                    throw new PyError("TypeError", "@AlternativeStopPositions needs a function");
                }

                function.IsStopPositionFunction = true;
                return function;
            }),
            ["CustomizedName"] = Method("CustomizedName", a =>
            {
                var name = new PyObject("CustomizedName");
                name.Attributes["name"] = a.Count > 0 ? a[0] : string.Empty;
                name.Attributes["priority"] = a.Count > 1 ? a[1] : PyNone.Value;
                return name;
            }),
            ["int"] = Method("int", a => Math.Truncate(ToNumber(a, "int"))),
            ["float"] = Method("float", a => ToNumber(a, "float")),
            ["bool"] = Method("bool", a => a.Count == 1 && Py.Truthy(a[0])),
            ["str"] = Method("str", a => a.Count == 1 ? Py.Str(a[0]) : throw Arguments("str", "1", a.Count)),
            ["len"] = Method("len", a => a.Count == 1 ? a[0] switch
            {
                string s => s.Length,
                PyTuple t => t.Items.Count,
                PyList l => l.Items.Count,
                PyDict d => (double)d.Items.Count,
                _ => throw new PyError("TypeError", $"object of type '{Py.TypeName(a[0])}' has no len()"),
            } : throw Arguments("len", "1", a.Count)),
            ["abs"] = Method("abs", a => Math.Abs(Number(a, 1, "abs"))),
            ["round"] = Method("round", a => a.Count == 2
                ? Math.Round(Py.AsNumber(a[0]) ?? 0, (int)(Py.AsNumber(a[1]) ?? 0), MidpointRounding.AwayFromZero)
                : Math.Round(Number(a, 1, "round"), MidpointRounding.AwayFromZero)),
            ["min"] = Method("min", a => MinMax(a, "min", -1)),
            ["max"] = Method("max", a => MinMax(a, "max", 1)),
        };
        return builtins;
    }

    private static double Number(IReadOnlyList<object> args, int count, string name)
    {
        if (args.Count != count)
        {
            throw Arguments(name, count.ToString(CultureInfo.InvariantCulture), args.Count);
        }

        return Py.AsNumber(args[0]) ?? throw new PyError("TypeError", $"{name}(): a number is required, not '{Py.TypeName(args[0])}'");
    }

    private static double ToNumber(IReadOnlyList<object> args, string name) =>
        args.Count == 1 && args[0] is string s
            ? double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new PyError("ValueError", $"invalid literal for {name}(): {Py.Repr(s)}")
            : Number(args, 1, name);

    private static object MinMax(IReadOnlyList<object> args, string name, int sign)
    {
        var items = args.Count == 1 && args[0] is PyTuple or PyList
            ? (args[0] is PyTuple t ? t.Items : ((PyList)args[0]).Items)
            : args;
        if (items.Count == 0)
        {
            throw new PyError("ValueError", $"{name}() arg is an empty sequence");
        }

        var best = items[0];
        foreach (var item in items.Skip(1))
        {
            if (Compare(sign > 0 ? ">" : "<", item, best))
            {
                best = item;
            }
        }

        return best;
    }
}
