using System.Text;

namespace Narwhal.Config;

/// <summary>Base type for parsed TOML values.</summary>
public abstract class TomlValue
{
    public abstract string Kind { get; }

    public string? AsString() => this is TomlString s ? s.Value
        : this is TomlDateTime d ? d.Raw
        : null;

    public override string ToString() => Kind;
}

public sealed class TomlString(string value) : TomlValue
{
    public string Value { get; } = value;
    public override string Kind => "string";
}

public sealed class TomlInteger(long value) : TomlValue
{
    public long Value { get; } = value;
    public override string Kind => "integer";
}

public sealed class TomlFloat(double value) : TomlValue
{
    public double Value { get; } = value;
    public override string Kind => "float";
}

public sealed class TomlBoolean(bool value) : TomlValue
{
    public bool Value { get; } = value;
    public override string Kind => "boolean";
}

public sealed class TomlDateTime(string raw) : TomlValue
{
    public string Raw { get; } = raw;
    public override string Kind => "datetime";
}

public sealed class TomlArray(List<TomlValue> items) : TomlValue
{
    public List<TomlValue> Items { get; } = items;
    public override string Kind => "array";
}

public sealed class TomlTable : TomlValue
{
    public Dictionary<string, TomlValue> Entries { get; } = new(StringComparer.Ordinal);
    public override string Kind => "table";

    public TomlValue? Get(string key) => Entries.TryGetValue(key, out var value) ? value : null;
}

public sealed class TomlException(string message, int line, int column, string? snippet = null)
    : Exception(message)
{
    public int Line { get; } = line;
    public int Column { get; } = column;
    public string? Snippet { get; } = snippet;
}

/// <summary>
/// A dependency-free TOML parser covering the subset narwhal manifests need:
/// tables, arrays of tables, dotted keys, basic/literal strings, multi-line
/// strings, integers, floats, booleans, arrays, inline tables and comments.
/// Written by hand so the AOT binary needs no reflection-based deserializers.
/// </summary>
public sealed class TomlParser
{
    private readonly string _text;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    private TomlParser(string text) => _text = text;

    public static TomlTable Parse(string text)
    {
        var parser = new TomlParser(text);
        return parser.ParseDocument();
    }

    private TomlTable ParseDocument()
    {
        var root = new TomlTable();
        var current = root;

        while (true)
        {
            SkipWhitespaceAndComments(includeNewlines: true);
            if (AtEnd)
                break;

            if (Peek() == '[')
            {
                if (Peek(1) == '[')
                {
                    current = ParseArrayTableHeader(root);
                }
                else
                {
                    current = ParseTableHeader(root);
                }
                ExpectLineEnd();
            }
            else
            {
                ParseKeyValue(current);
                ExpectLineEnd();
            }
        }

        return root;
    }

    private TomlTable ParseTableHeader(TomlTable root)
    {
        Expect('[');
        var path = ParseKeyPath();
        SkipInlineWhitespace();
        Expect(']');

        var table = root;
        for (var i = 0; i < path.Count; i++)
        {
            var segment = path[i];
            var existing = table.Get(segment);

            if (i == path.Count - 1)
            {
                if (existing is TomlTable t)
                    return t;
                if (existing is not null)
                    throw Error($"cannot redefine '{string.Join('.', path)}' as a table, it is a {existing.Kind}");

                var created = new TomlTable();
                table.Entries[segment] = created;
                return created;
            }

            table = existing switch
            {
                TomlTable t => t,
                TomlArray a when a.Items.Count > 0 && a.Items[^1] is TomlTable last => last, // [[x]] then [x.y]
                null => CreateIntermediate(table, segment),
                _ => throw Error($"'{string.Join('.', path)}' conflicts with an existing {existing.Kind}"),
            };
        }

        return table;
    }

    private TomlTable CreateIntermediate(TomlTable parent, string key)
    {
        var created = new TomlTable();
        parent.Entries[key] = created;
        return created;
    }

    private TomlTable ParseArrayTableHeader(TomlTable root)
    {
        Expect('[');
        Expect('[');
        var path = ParseKeyPath();
        SkipInlineWhitespace();
        Expect(']');
        Expect(']');

        TomlTable table = root;
        for (var i = 0; i < path.Count - 1; i++)
        {
            var segment = path[i];
            table = table.Get(segment) switch
            {
                TomlTable t => t,
                TomlArray a when a.Items.Count > 0 && a.Items[^1] is TomlTable last => last,
                null => CreateIntermediate(table, segment),
                var other => throw Error($"'{segment}' is a {other.Kind}, expected a table"),
            };
        }

        var name = path[^1];
        if (table.Get(name) is not TomlArray array)
        {
            array = new TomlArray([]);
            table.Entries[name] = array;
        }
        else
        {
            foreach (var item in array.Items)
            {
                if (item is not TomlTable)
                    throw Error($"cannot append a table to '{name}', it is an array of {item.Kind}");
            }
        }

        var entry = new TomlTable();
        array.Items.Add(entry);
        return entry;
    }

    private void ParseKeyValue(TomlTable table)
    {
        var path = ParseKeyPath();
        SkipInlineWhitespace();
        Expect('=');
        SkipInlineWhitespace();

        // Value may continue onto further lines only if it is an array, inline
        // table or multi-line string, all of which consume their own newlines.
        var value = ParseValue();

        var target = table;
        for (var i = 0; i < path.Count - 1; i++)
        {
            var segment = path[i];
            target = target.Get(segment) switch
            {
                TomlTable t => t,
                null => CreateIntermediate(target, segment),
                var other => throw Error($"'{segment}' is a {other.Kind}, cannot assign a nested key"),
            };
        }

        var last = path[^1];
        if (!target.Entries.TryAdd(last, value))
            throw Error($"duplicate key '{string.Join('.', path)}'");
    }

    private List<string> ParseKeyPath()
    {
        var parts = new List<string>();
        while (true)
        {
            SkipInlineWhitespace();
            parts.Add(ParseSimpleKey());
            SkipInlineWhitespace();
            if (Peek() != '.')
                return parts;
            Advance(); // '.'
        }
    }

    private string ParseSimpleKey()
    {
        var c = Peek();
        if (c == '"')
            return ParseBasicString();
        if (c == '\'')
            return ParseLiteralString();

        var start = _pos;
        while (!AtEnd)
        {
            c = Peek();
            if (char.IsLetterOrDigit(c) || c is '_' or '-')
                Advance();
            else
                break;
        }

        if (_pos == start)
            throw Error("expected a key");
        return _text[start.._pos];
    }

    private TomlValue ParseValue()
    {
        var c = Peek();
        return c switch
        {
            '"' => _text.AsSpan(_pos).StartsWith("\"\"\"") ? new TomlString(ParseMultilineBasicString()) : new TomlString(ParseBasicString()),
            '\'' => _text.AsSpan(_pos).StartsWith("'''") ? new TomlString(ParseMultilineLiteralString()) : new TomlString(ParseLiteralString()),
            '[' => ParseArray(),
            '{' => ParseInlineTable(),
            't' or 'f' => ParseBoolean(),
            _ => ParseNumberOrDate(),
        };
    }

    private TomlArray ParseArray()
    {
        Expect('[');
        var items = new List<TomlValue>();
        while (true)
        {
            SkipWhitespaceAndComments(includeNewlines: true);
            if (Peek() == ']')
            {
                Advance();
                return new TomlArray(items);
            }

            items.Add(ParseValue());
            SkipWhitespaceAndComments(includeNewlines: true);

            if (Peek() == ',')
            {
                Advance();
                continue;
            }

            if (Peek() == ']')
            {
                Advance();
                return new TomlArray(items);
            }

            throw Error("expected ',' or ']' in array");
        }
    }

    private TomlTable ParseInlineTable()
    {
        Expect('{');
        var table = new TomlTable();
        SkipInlineWhitespace();
        if (Peek() == '}')
        {
            Advance();
            return table;
        }

        while (true)
        {
            SkipInlineWhitespace();
            var path = ParseKeyPath();
            SkipInlineWhitespace();
            Expect('=');
            SkipInlineWhitespace();
            var value = ParseValue();
            SkipInlineWhitespace();

            var target = table;
            for (var i = 0; i < path.Count - 1; i++)
            {
                var segment = path[i];
                target = target.Get(segment) switch
                {
                    TomlTable t => t,
                    null => CreateIntermediate(target, segment),
                    var other => throw Error($"'{segment}' is a {other.Kind}, cannot assign a nested key"),
                };
            }

            if (!target.Entries.TryAdd(path[^1], value))
                throw Error($"duplicate key '{string.Join('.', path)}' in inline table");

            if (Peek() == ',')
            {
                Advance();
                SkipInlineWhitespace();
                continue;
            }

            Expect('}');
            return table;
        }
    }

    private TomlBoolean ParseBoolean()
    {
        if (TryConsume("true"))
            return new TomlBoolean(true);
        if (TryConsume("false"))
            return new TomlBoolean(false);
        throw Error("expected 'true' or 'false'");
    }

    private TomlValue ParseNumberOrDate()
    {
        var start = _pos;
        while (!AtEnd)
        {
            var c = Peek();
            if (char.IsLetterOrDigit(c) || c is '_' or '+' or '-' or '.' or ':' or 'T' or 'Z' or ' ')
            {
                // A space only continues the token in a datetime ("1979-05-27 07:32:00").
                if (c == ' ' && !LooksLikeDateStart(_text.AsSpan(start, _pos - start)))
                    break;
                Advance();
            }
            else
            {
                break;
            }
        }

        var raw = _text[start.._pos].TrimEnd();
        if (raw.Length == 0)
            throw Error("expected a value");

        if (LooksLikeDateStart(raw.AsSpan()))
            return new TomlDateTime(raw);

        if (TryParseInteger(raw, out var integer))
            return new TomlInteger(integer);

        if (double.TryParse(raw.Replace("_", string.Empty), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var real))
            return new TomlFloat(real);

        throw Error($"unrecognised value '{raw}'");
    }

    private static bool LooksLikeDateStart(ReadOnlySpan<char> token) =>
        token.Length >= 10
        && char.IsAsciiDigit(token[0]) && char.IsAsciiDigit(token[1]) && char.IsAsciiDigit(token[2]) && char.IsAsciiDigit(token[3])
        && token[4] == '-';

    private static bool TryParseInteger(string raw, out long value)
    {
        value = 0;
        var text = raw.Replace("_", string.Empty);
        var sign = 1;
        if (text.StartsWith('-'))
        {
            sign = -1;
            text = text[1..];
        }
        else if (text.StartsWith('+'))
        {
            text = text[1..];
        }

        try
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = sign * Convert.ToInt64(text[2..], 16);
            else if (text.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
                value = sign * Convert.ToInt64(text[2..], 8);
            else if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                value = sign * Convert.ToInt64(text[2..], 2);
            else
                return long.TryParse(text, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out value) && (value *= sign) == value;
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            return false;
        }

        return true;
    }

    private string ParseBasicString()
    {
        Expect('"');
        var builder = new StringBuilder();
        while (true)
        {
            if (AtEnd)
                throw Error("unterminated string");
            var c = Peek();
            if (c == '"')
            {
                Advance();
                return builder.ToString();
            }
            if (c == '\n')
                throw Error("unterminated string");

            if (c == '\\')
            {
                Advance();
                builder.Append(ParseEscape());
                continue;
            }

            builder.Append(c);
            Advance();
        }
    }

    private string ParseLiteralString()
    {
        Expect('\'');
        var start = _pos;
        while (!AtEnd && Peek() != '\'')
        {
            if (Peek() == '\n')
                throw Error("unterminated string");
            Advance();
        }
        if (AtEnd)
            throw Error("unterminated string");
        var value = _text[start.._pos];
        Advance(); // closing quote
        return value;
    }

    private string ParseMultilineBasicString()
    {
        Expect('"');
        Expect('"');
        Expect('"');
        ConsumeNewlineIfPresent();

        var builder = new StringBuilder();
        while (true)
        {
            if (AtEnd)
                throw Error("unterminated multi-line string");
            if (_text.AsSpan(_pos).StartsWith("\"\"\""))
            {
                Advance(3);
                // TOML allows up to two additional quotes as content.
                while (Peek() == '"')
                {
                    builder.Append('"');
                    Advance();
                }
                return builder.ToString();
            }

            var c = Peek();
            if (c == '\\')
            {
                Advance();
                if (SkipLineEndingBackslash(builder))
                    continue;
                builder.Append(ParseEscape());
                continue;
            }

            builder.Append(c);
            Advance();
        }
    }

    private bool SkipLineEndingBackslash(StringBuilder builder)
    {
        var save = _pos;
        var saveLine = _line;
        var saveCol = _col;
        while (Peek() is ' ' or '\t')
            Advance();
        if (Peek() is not ('\n' or '\r'))
        {
            _pos = save;
            _line = saveLine;
            _col = saveCol;
            return false;
        }

        // Trim trailing whitespace on the current line, as TOML requires.
        while (builder.Length > 0 && builder[^1] is ' ' or '\t')
            builder.Length--;

        SkipWhitespaceAndComments(includeNewlines: true);
        return true;
    }

    private string ParseMultilineLiteralString()
    {
        Expect('\'');
        Expect('\'');
        Expect('\'');
        ConsumeNewlineIfPresent();

        var start = _pos;
        while (!AtEnd)
        {
            if (_text.AsSpan(_pos).StartsWith("'''"))
            {
                var value = _text[start.._pos];
                Advance(3);
                while (Peek() == '\'')
                {
                    value += "'";
                    Advance();
                }
                return value;
            }
            Advance();
        }

        throw Error("unterminated multi-line string");
    }

    private void ConsumeNewlineIfPresent()
    {
        if (Peek() == '\r' && Peek(1) == '\n')
            Advance(2);
        else if (Peek() == '\n')
            Advance();
    }

    private string ParseEscape()
    {
        if (AtEnd)
            throw Error("unterminated escape sequence");
        var c = Peek();
        Advance();
        return c switch
        {
            'b' => "\b",
            't' => "\t",
            'n' => "\n",
            'f' => "\f",
            'r' => "\r",
            'e' => "\u001b",
            '"' => "\"",
            '\\' => "\\",
            'u' => ParseUnicodeEscape(4),
            'U' => ParseUnicodeEscape(8),
            _ => throw Error($"invalid escape sequence '\\{c}'"),
        };
    }

    private string ParseUnicodeEscape(int digits)
    {
        var start = _pos;
        for (var i = 0; i < digits; i++)
        {
            if (AtEnd || !Uri.IsHexDigit(Peek()))
                throw Error("invalid unicode escape");
            Advance();
        }
        var code = Convert.ToInt32(_text[start.._pos], 16);
        return char.ConvertFromUtf32(code);
    }

    private void ExpectLineEnd()
    {
        SkipInlineWhitespace();
        if (Peek() == '#')
        {
            while (!AtEnd && Peek() != '\n')
                Advance();
        }

        if (AtEnd)
            return;
        if (Peek() == '\r' && Peek(1) == '\n')
        {
            Advance(2);
            return;
        }
        if (Peek() == '\n')
        {
            Advance();
            return;
        }
        throw Error("expected a newline after the value");
    }

    private void SkipWhitespaceAndComments(bool includeNewlines)
    {
        while (!AtEnd)
        {
            var c = Peek();
            if (c is ' ' or '\t' || (includeNewlines && c is '\r' or '\n'))
            {
                Advance();
                continue;
            }
            if (c == '#')
            {
                while (!AtEnd && Peek() != '\n')
                    Advance();
                continue;
            }
            break;
        }
    }

    private void SkipInlineWhitespace()
    {
        while (!AtEnd && Peek() is ' ' or '\t')
            Advance();
    }

    private void Expect(char expected)
    {
        if (AtEnd)
            throw Error($"expected '{expected}' but reached end of input");
        if (Peek() != expected)
            throw Error($"expected '{expected}' but found '{Peek()}'");
        Advance();
    }

    private bool TryConsume(string token)
    {
        if (!_text.AsSpan(_pos).StartsWith(token))
            return false;
        Advance(token.Length);
        return true;
    }

    private bool AtEnd => _pos >= _text.Length;

    private char Peek(int offset = 0)
    {
        var index = _pos + offset;
        return index < _text.Length ? _text[index] : '\0';
    }

    private void Advance(int count = 1)
    {
        for (var i = 0; i < count && _pos < _text.Length; i++)
        {
            if (_text[_pos] == '\n')
            {
                _line++;
                _col = 1;
            }
            else
            {
                _col++;
            }
            _pos++;
        }
    }

    private TomlException Error(string message)
    {
        var lineStart = _pos;
        while (lineStart > 0 && _text[lineStart - 1] != '\n')
            lineStart--;
        var lineEnd = _pos;
        while (lineEnd < _text.Length && _text[lineEnd] != '\n')
            lineEnd++;

        var snippet = _text[lineStart..lineEnd];
        var caret = new string(' ', Math.Max(0, _col - 1)) + "^";
        return new TomlException(message, _line, _col, snippet + "\n" + caret);
    }
}
