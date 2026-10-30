using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rampside.Core.Aircraft.Pmdg;

public enum PmdgFieldKind
{
    Bool,
    Char,
    UInt8,
    Int8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Float32,
    Float64,
    Struct,
}

public sealed record PmdgField(string Name, string TypeName, PmdgFieldKind Kind, int Offset, int ElementSize, IReadOnlyList<int> Dimensions)
{
    public int Count => Dimensions.Aggregate(1, (a, b) => a * b);
    public int Size => ElementSize * Count;
}

public sealed class PmdgStructLayout
{
    private readonly Dictionary<string, PmdgField> _byName;

    public PmdgStructLayout(string name, int size, int alignment, IReadOnlyList<PmdgField> fields)
    {
        Name = name;
        Size = size;
        Alignment = alignment;
        Fields = fields;
        _byName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
    }

    public string Name { get; }
    public int Size { get; }
    public int Alignment { get; }
    public IReadOnlyList<PmdgField> Fields { get; }

    public PmdgField? Find(string fieldName) => _byName.GetValueOrDefault(fieldName);
}

/// <summary>
/// Reads a PMDG SDK header (PMDG_NG3_SDK.h, PMDG_777X_SDK.h, ...) from the user's PMDG installation at runtime:
/// #define values (event ids, client data names) and the struct layouts (MSVC x64 natural alignment).
/// Nothing from PMDG is shipped with Rampside; if PMDG changes the layout in an update, Rampside follows.
/// </summary>
public sealed class PmdgSdkHeader
{
    private static readonly Regex StructRegex = new(@"\bstruct\s+(\w+)\s*\{(.*?)\}\s*;", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DeclarationRegex = new(@"^(?<type>[A-Za-z_][\w\s]*?)\s+(?<name>[A-Za-z_]\w*)(?<dims>(\s*\[[^\]]+\])*)$", RegexOptions.Compiled);
    private static readonly Regex DimensionRegex = new(@"\[([^\]]+)\]", RegexOptions.Compiled);

    private static readonly Dictionary<string, (PmdgFieldKind Kind, int Size)> Primitives = new(StringComparer.Ordinal)
    {
        ["bool"] = (PmdgFieldKind.Bool, 1),
        ["char"] = (PmdgFieldKind.Char, 1),
        ["signed char"] = (PmdgFieldKind.Int8, 1),
        ["unsigned char"] = (PmdgFieldKind.UInt8, 1),
        ["short"] = (PmdgFieldKind.Int16, 2),
        ["signed short"] = (PmdgFieldKind.Int16, 2),
        ["unsigned short"] = (PmdgFieldKind.UInt16, 2),
        ["int"] = (PmdgFieldKind.Int32, 4),
        ["signed int"] = (PmdgFieldKind.Int32, 4),
        ["unsigned int"] = (PmdgFieldKind.UInt32, 4),
        ["unsigned"] = (PmdgFieldKind.UInt32, 4),
        ["long"] = (PmdgFieldKind.Int32, 4),
        ["unsigned long"] = (PmdgFieldKind.UInt32, 4),
        ["float"] = (PmdgFieldKind.Float32, 4),
        ["double"] = (PmdgFieldKind.Float64, 8),
    };

    private readonly Dictionary<string, string> _rawDefines;
    private readonly Dictionary<string, long?> _numberCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PmdgStructLayout> _structs = new(StringComparer.Ordinal);

    private PmdgSdkHeader(string sourcePath, Dictionary<string, string> rawDefines)
    {
        SourcePath = sourcePath;
        _rawDefines = rawDefines;
    }

    public string SourcePath { get; }
    public IReadOnlyDictionary<string, PmdgStructLayout> Structs => _structs;
    public IEnumerable<string> DefineNames => _rawDefines.Keys;

    public static PmdgSdkHeader Load(string path) => Parse(File.ReadAllText(path), path);

    public static PmdgSdkHeader Parse(string text, string sourcePath = "")
    {
        var code = StripComments(text);
        var defines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in code.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("#define", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Substring("#define".Length).Trim().Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && !parts[0].Contains('('))
            {
                defines[parts[0]] = parts[1].Trim();
            }
        }

        var header = new PmdgSdkHeader(sourcePath, defines);
        foreach (Match match in StructRegex.Matches(code))
        {
            header.AddStruct(match.Groups[1].Value, match.Groups[2].Value);
        }

        return header;
    }

    public long? GetNumber(string defineName) => Evaluate(defineName, new HashSet<string>());

    public string? GetString(string defineName)
    {
        if (!_rawDefines.TryGetValue(defineName, out var raw) || raw.Length < 2 || raw[0] != '"')
        {
            return null;
        }

        var end = raw.IndexOf('"', 1);
        return end > 0 ? raw.Substring(1, end - 1) : null;
    }

    public PmdgStructLayout GetStruct(string name) =>
        _structs.TryGetValue(name, out var layout)
            ? layout
            : throw new InvalidDataException($"struct {name} not found in {SourcePath}");

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        var inString = false;
        while (i < text.Length)
        {
            var c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < text.Length)
                {
                    sb.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                inString = c != '"';
                i++;
            }
            else if (c == '"')
            {
                inString = true;
                sb.Append(c);
                i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    private void AddStruct(string name, string body)
    {
        var fields = new List<PmdgField>();
        var offset = 0;
        var maxAlignment = 1;

        foreach (var rawDeclaration in body.Split(';'))
        {
            var declaration = Regex.Replace(rawDeclaration, @"\s+", " ").Trim();
            if (declaration.Length == 0)
            {
                continue;
            }

            var match = DeclarationRegex.Match(declaration);
            if (!match.Success)
            {
                throw new InvalidDataException($"Cannot parse '{declaration}' in struct {name}");
            }

            var typeName = Regex.Replace(match.Groups["type"].Value, @"^struct\s+", string.Empty).Trim();
            var dimensions = DimensionRegex.Matches(match.Groups["dims"].Value)
                .Select(d => (int)(EvaluateExpression(d.Groups[1].Value, new HashSet<string>())
                    ?? throw new InvalidDataException($"Unknown array size '{d.Groups[1].Value}' in struct {name}")))
                .ToList();

            PmdgFieldKind kind;
            int elementSize;
            int alignment;
            if (Primitives.TryGetValue(typeName, out var primitive))
            {
                kind = primitive.Kind;
                elementSize = primitive.Size;
                alignment = primitive.Size;
            }
            else if (_structs.TryGetValue(typeName, out var nested))
            {
                kind = PmdgFieldKind.Struct;
                elementSize = nested.Size;
                alignment = nested.Alignment;
            }
            else
            {
                throw new InvalidDataException($"Unknown type '{typeName}' in struct {name}");
            }

            offset = AlignUp(offset, alignment);
            var field = new PmdgField(match.Groups["name"].Value, typeName, kind, offset, elementSize, dimensions);
            fields.Add(field);
            offset += field.Size;
            maxAlignment = Math.Max(maxAlignment, alignment);
        }

        _structs[name] = new PmdgStructLayout(name, AlignUp(offset, maxAlignment), maxAlignment, fields);
    }

    private long? Evaluate(string defineName, HashSet<string> visiting)
    {
        if (_numberCache.TryGetValue(defineName, out var cached))
        {
            return cached;
        }

        if (!_rawDefines.TryGetValue(defineName, out var raw) || !visiting.Add(defineName))
        {
            return null;
        }

        var value = EvaluateExpression(raw, visiting);
        visiting.Remove(defineName);
        _numberCache[defineName] = value;
        return value;
    }

    /// <summary>Evaluates simple define expressions: numbers, other defines, + - * |, parentheses.</summary>
    private long? EvaluateExpression(string expression, HashSet<string> visiting)
    {
        var tokens = Regex.Matches(expression, @"0[xX][0-9A-Fa-f]+[uUlL]*|\d+[uUlL]*|[A-Za-z_]\w*|[-+*|()]")
            .Select(m => m.Value)
            .ToList();
        if (tokens.Count == 0)
        {
            return null;
        }

        var position = 0;
        try
        {
            var result = ParseOr();
            return position == tokens.Count ? result : null;
        }
        catch (FormatException)
        {
            return null;
        }

        long ParseOr()
        {
            var value = ParseSum();
            while (position < tokens.Count && tokens[position] == "|")
            {
                position++;
                value |= ParseSum();
            }

            return value;
        }

        long ParseSum()
        {
            var value = ParseProduct();
            while (position < tokens.Count && tokens[position] is "+" or "-")
            {
                var op = tokens[position++];
                var right = ParseProduct();
                value = op == "+" ? value + right : value - right;
            }

            return value;
        }

        long ParseProduct()
        {
            var value = ParseUnary();
            while (position < tokens.Count && tokens[position] == "*")
            {
                position++;
                value *= ParseUnary();
            }

            return value;
        }

        long ParseUnary()
        {
            if (position < tokens.Count && tokens[position] == "-")
            {
                position++;
                return -ParseUnary();
            }

            return ParseAtom();
        }

        long ParseAtom()
        {
            if (position >= tokens.Count)
            {
                throw new FormatException();
            }

            var token = tokens[position++];
            if (token == "(")
            {
                var value = ParseOr();
                if (position >= tokens.Count || tokens[position++] != ")")
                {
                    throw new FormatException();
                }

                return value;
            }

            var digits = token.TrimEnd('u', 'U', 'l', 'L');
            if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return long.Parse(digits.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            if (char.IsDigit(digits[0]))
            {
                return long.Parse(digits, CultureInfo.InvariantCulture);
            }

            return Evaluate(token, visiting) ?? throw new FormatException();
        }
    }
}
