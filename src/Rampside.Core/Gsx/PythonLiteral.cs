using System.Globalization;
using System.Text;

namespace Rampside.Core.Gsx;

/// <summary>
/// Reads the Python literals GSX writes into its profiles (e.g. <c>pushbackaddpos</c>):
/// lists, tuples, dicts, strings (also <c>u'...'</c>), numbers, <c>None</c>, <c>True</c> and <c>False</c>.
/// Lists and tuples become <see cref="List{T}"/> of object, dicts become <see cref="Dictionary{TKey,TValue}"/> with string keys,
/// numbers become double, None becomes null. Nothing is ever executed.
/// </summary>
public static class PythonLiteral
{
    public static object? Parse(string text)
    {
        var reader = new Reader(text);
        var value = reader.Value();
        reader.SkipWhitespace();
        if (!reader.AtEnd)
        {
            throw reader.Error("unexpected text after the value");
        }

        return value;
    }

    private sealed class Reader
    {
        private readonly string _text;
        private int _pos;

        public Reader(string text) => _text = text;

        public bool AtEnd => _pos >= _text.Length;

        public FormatException Error(string message) => new($"Python literal: {message} at position {_pos}.");

        public void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(_text[_pos]))
            {
                _pos++;
            }
        }

        public object? Value()
        {
            SkipWhitespace();
            if (AtEnd)
            {
                throw Error("value expected");
            }

            var c = _text[_pos];
            switch (c)
            {
                case '[':
                    return Sequence('[', ']');
                case '(':
                    return Sequence('(', ')');
                case '{':
                    return Dict();
                case '\'' or '"':
                    return String();
                case 'u' or 'U' or 'r' or 'R' when _pos + 1 < _text.Length && _text[_pos + 1] is '\'' or '"':
                    _pos++;
                    return String();
            }

            if (c is '-' or '+' or '.' || char.IsDigit(c))
            {
                return Number();
            }

            var word = Word();
            return word switch
            {
                "None" => null,
                "True" => true,
                "False" => false,
                _ => throw Error($"unknown name '{word}'"),
            };
        }

        private List<object?> Sequence(char open, char close)
        {
            Expect(open);
            var items = new List<object?>();
            while (true)
            {
                SkipWhitespace();
                if (TryTake(close))
                {
                    return items;
                }

                items.Add(Value());
                SkipWhitespace();
                if (!TryTake(','))
                {
                    Expect(close);
                    return items;
                }
            }
        }

        private Dictionary<string, object?> Dict()
        {
            Expect('{');
            var items = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                SkipWhitespace();
                if (TryTake('}'))
                {
                    return items;
                }

                var key = Value()?.ToString() ?? throw Error("dict key must not be None");
                SkipWhitespace();
                Expect(':');
                items[key] = Value();
                SkipWhitespace();
                if (!TryTake(','))
                {
                    Expect('}');
                    return items;
                }
            }
        }

        private string String()
        {
            var quote = _text[_pos++];
            var result = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw Error("unterminated string");
                }

                var c = _text[_pos++];
                if (c == quote)
                {
                    return result.ToString();
                }

                if (c == '\\' && !AtEnd)
                {
                    var escaped = _text[_pos++];
                    result.Append(escaped switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        _ => escaped,
                    });
                    continue;
                }

                result.Append(c);
            }
        }

        private double Number()
        {
            var start = _pos;
            while (!AtEnd && (char.IsDigit(_text[_pos]) || _text[_pos] is '-' or '+' or '.' or 'e' or 'E'))
            {
                _pos++;
            }

            var token = _text[start.._pos];
            return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : throw Error($"invalid number '{token}'");
        }

        private string Word()
        {
            var start = _pos;
            while (!AtEnd && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
            {
                _pos++;
            }

            return start == _pos ? throw Error($"unexpected '{_text[_pos]}'") : _text[start.._pos];
        }

        private bool TryTake(char c)
        {
            if (!AtEnd && _text[_pos] == c)
            {
                _pos++;
                return true;
            }

            return false;
        }

        private void Expect(char c)
        {
            if (!TryTake(c))
            {
                throw Error($"'{c}' expected");
            }
        }
    }
}
