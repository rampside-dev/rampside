using System.Globalization;
using System.Text;

namespace Rampside.Core.Gsx.Scripting;

internal enum TokenKind
{
    Name,
    Number,
    String,
    Op,
    Newline,
    Indent,
    Dedent,
    End,
}

/// <param name="StartsLine">First token of a logical line at <paramref name="LineIndent"/> (used to skip a broken statement).</param>
internal readonly record struct Token(TokenKind Kind, string Text, int Line, object? Value = null, bool StartsLine = false, int LineIndent = 0)
{
    public bool Is(string op) => Kind is TokenKind.Op or TokenKind.Name && Text == op;

    public override string ToString() => Kind switch
    {
        TokenKind.Newline => "end of line",
        TokenKind.Indent => "indent",
        TokenKind.Dedent => "dedent",
        TokenKind.End => "end of file",
        _ => $"'{Text}'",
    };
}

/// <summary>A syntax error in a GSX profile script, with its line.</summary>
public sealed class PySyntaxError(int line, string message) : FormatException($"line {line}: {message}")
{
    public int Line { get; } = line;
}

/// <summary>Splits Python 2 source into tokens, with INDENT/DEDENT for blocks like Python's own tokenizer.</summary>
internal static class PyLexer
{
    private static readonly string[] Operators =
    [
        "**", "//", "==", "!=", "<>", "<=", ">=", "+=", "-=", "*=", "/=",
        "+", "-", "*", "/", "%", "<", ">", "=", "(", ")", "[", "]", "{", "}", ",", ":", ".", "@", ";",
    ];

    public static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var indents = new Stack<int>();
        indents.Push(0);
        var pos = 0;
        var line = 1;
        var depth = 0; // open brackets: newlines and indentation inside them do not count
        var atLineStart = true;
        var lineIndent = 0;

        while (pos < text.Length)
        {
            if (atLineStart && depth == 0)
            {
                var column = 0;
                while (pos < text.Length && text[pos] is ' ' or '\t' or '\f')
                {
                    column = text[pos] == '\t' ? (column / 8 + 1) * 8 : column + 1;
                    pos++;
                }

                if (pos >= text.Length)
                {
                    break;
                }

                if (text[pos] is '\r' or '\n' or '#')
                {
                    SkipToLineEnd(text, ref pos);
                    if (pos < text.Length)
                    {
                        pos++;
                        line++;
                    }

                    continue; // blank or comment line
                }

                if (column > indents.Peek())
                {
                    indents.Push(column);
                    tokens.Add(new Token(TokenKind.Indent, string.Empty, line));
                }
                else
                {
                    while (column < indents.Peek())
                    {
                        indents.Pop();
                        tokens.Add(new Token(TokenKind.Dedent, string.Empty, line));
                    }

                    if (column != indents.Peek())
                    {
                        throw new PySyntaxError(line, "indentation does not match any outer level");
                    }
                }

                lineIndent = column;
                atLineStart = false;
                tokens.Add(Next(text, ref pos, ref line, ref depth, startsLine: true, lineIndent));
                continue;
            }

            var c = text[pos];
            if (c is ' ' or '\t' or '\f' or '\r')
            {
                pos++;
            }
            else if (c == '#')
            {
                SkipToLineEnd(text, ref pos);
            }
            else if (c == '\\' && pos + 1 < text.Length && text[pos + 1] is '\n' or '\r')
            {
                pos++;
                if (text[pos] == '\r' && pos + 1 < text.Length && text[pos + 1] == '\n')
                {
                    pos++;
                }

                pos++;
                line++;
            }
            else if (c == '\n')
            {
                if (depth == 0)
                {
                    tokens.Add(new Token(TokenKind.Newline, string.Empty, line));
                    atLineStart = true;
                }

                pos++;
                line++;
            }
            else
            {
                tokens.Add(Next(text, ref pos, ref line, ref depth, startsLine: false, lineIndent));
            }
        }

        if (tokens.Count > 0 && tokens[^1].Kind is not (TokenKind.Newline or TokenKind.Dedent))
        {
            tokens.Add(new Token(TokenKind.Newline, string.Empty, line));
        }

        while (indents.Count > 1)
        {
            indents.Pop();
            tokens.Add(new Token(TokenKind.Dedent, string.Empty, line));
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, line, StartsLine: true));
        return tokens;
    }

    private static void SkipToLineEnd(string text, ref int pos)
    {
        while (pos < text.Length && text[pos] != '\n')
        {
            pos++;
        }
    }

    private static Token Next(string text, ref int pos, ref int line, ref int depth, bool startsLine, int lineIndent)
    {
        var c = text[pos];
        var start = pos;
        var startLine = line;

        if (IsStringStart(text, pos, out var prefixLength))
        {
            var raw = text.AsSpan(pos, prefixLength).ToString().Contains('r', StringComparison.OrdinalIgnoreCase);
            pos += prefixLength;
            var value = ReadString(text, ref pos, ref line, raw);
            return new Token(TokenKind.String, text[start..pos], startLine, value, startsLine, lineIndent);
        }

        if (char.IsDigit(c) || (c == '.' && pos + 1 < text.Length && char.IsDigit(text[pos + 1])))
        {
            return new Token(TokenKind.Number, text[start..ReadNumber(text, ref pos, line, out var number)], startLine, number, startsLine, lineIndent);
        }

        if (char.IsLetter(c) || c == '_')
        {
            while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_'))
            {
                pos++;
            }

            return new Token(TokenKind.Name, text[start..pos], startLine, null, startsLine, lineIndent);
        }

        foreach (var op in Operators)
        {
            if (string.CompareOrdinal(text, pos, op, 0, op.Length) == 0)
            {
                pos += op.Length;
                depth += op is "(" or "[" or "{" ? 1 : op is ")" or "]" or "}" ? -1 : 0;
                depth = Math.Max(depth, 0);
                return new Token(TokenKind.Op, op, startLine, null, startsLine, lineIndent);
            }
        }

        throw new PySyntaxError(line, $"unexpected character '{c}'");
    }

    /// <summary>A quote, optionally after u, r, b, ur or br in any case.</summary>
    private static bool IsStringStart(string text, int pos, out int prefixLength)
    {
        prefixLength = 0;
        while (prefixLength < 2 && pos + prefixLength < text.Length && text[pos + prefixLength] is 'u' or 'U' or 'r' or 'R' or 'b' or 'B')
        {
            prefixLength++;
        }

        if (pos + prefixLength < text.Length && text[pos + prefixLength] is '\'' or '"')
        {
            return prefixLength == 0 || !(pos > 0 && (char.IsLetterOrDigit(text[pos - 1]) || text[pos - 1] == '_'));
        }

        prefixLength = 0;
        return false;
    }

    private static string ReadString(string text, ref int pos, ref int line, bool raw)
    {
        var quote = text[pos];
        var triple = pos + 2 < text.Length && text[pos + 1] == quote && text[pos + 2] == quote;
        pos += triple ? 3 : 1;
        var value = new StringBuilder();
        while (true)
        {
            if (pos >= text.Length)
            {
                throw new PySyntaxError(line, "string is not closed");
            }

            var c = text[pos];
            if (c == quote && (!triple || (pos + 2 < text.Length && text[pos + 1] == quote && text[pos + 2] == quote)))
            {
                pos += triple ? 3 : 1;
                return value.ToString();
            }

            if (c == '\n')
            {
                if (!triple)
                {
                    throw new PySyntaxError(line, "string is not closed");
                }

                line++;
            }

            if (c == '\\' && pos + 1 < text.Length)
            {
                var e = text[pos + 1];
                pos += 2;
                if (raw)
                {
                    value.Append('\\').Append(e);
                    continue;
                }

                switch (e)
                {
                    case 'n': value.Append('\n'); break;
                    case 't': value.Append('\t'); break;
                    case 'r': value.Append('\r'); break;
                    case '0': value.Append('\0'); break;
                    case '\\' or '\'' or '"': value.Append(e); break;
                    case '\n': line++; break;
                    case 'x' when pos + 2 <= text.Length && int.TryParse(text.AsSpan(pos, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var x):
                        value.Append((char)x);
                        pos += 2;
                        break;
                    case 'u' when pos + 4 <= text.Length && int.TryParse(text.AsSpan(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u):
                        value.Append((char)u);
                        pos += 4;
                        break;
                    default: value.Append('\\').Append(e); break;
                }

                continue;
            }

            value.Append(c);
            pos++;
        }
    }

    /// <summary>Decimal, float with exponent, 0x hex; a trailing L (Python 2 long) is dropped.</summary>
    private static int ReadNumber(string text, ref int pos, int line, out double number)
    {
        var start = pos;
        if (text[pos] == '0' && pos + 1 < text.Length && text[pos + 1] is 'x' or 'X')
        {
            pos += 2;
            while (pos < text.Length && Uri.IsHexDigit(text[pos]))
            {
                pos++;
            }

            number = long.Parse(text.AsSpan(start + 2, pos - start - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else
        {
            while (pos < text.Length && (char.IsDigit(text[pos]) || text[pos] == '.'))
            {
                pos++;
            }

            if (pos < text.Length && text[pos] is 'e' or 'E')
            {
                var mark = pos++;
                if (pos < text.Length && text[pos] is '+' or '-')
                {
                    pos++;
                }

                if (pos < text.Length && char.IsDigit(text[pos]))
                {
                    while (pos < text.Length && char.IsDigit(text[pos]))
                    {
                        pos++;
                    }
                }
                else
                {
                    pos = mark;
                }
            }

            if (!double.TryParse(text.AsSpan(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                throw new PySyntaxError(line, $"invalid number '{text[start..pos]}'");
            }
        }

        if (pos < text.Length && text[pos] is 'L' or 'l')
        {
            pos++;
        }

        if (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_'))
        {
            throw new PySyntaxError(line, $"invalid number '{text[start..(pos + 1)]}'");
        }

        return pos;
    }
}
