using Tomlyn;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

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
/// Parses TOML with Tomlyn's strict syntax parser, then adapts the syntax tree
/// to the small <see cref="TomlValue"/> tree the readers consume. The syntax
/// tree keeps the parser AOT-safe and preserves raw datetime tokens.
/// </summary>
public static class TomlParser
{
    private const string SourceName = "<input>";

    /// <summary>
    /// Maximum nesting depth accepted by the adapter. Chosen well above any real
    /// manifest so the limit is a deliberate guard rather than a library default.
    /// </summary>
    internal const int MaxNestingDepth = 1024;

    public static TomlTable Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        DocumentSyntax document;
        try
        {
            // Tomlyn's strict parser implements the TOML 1.1 grammar, including the 1.1-only constructs.
            var options = new TomlSerializerOptions { MaxDepth = MaxNestingDepth };
            document = SyntaxParser.ParseStrict(text, options, SourceName, validate: true);
        }
        catch (Tomlyn.TomlException e)
        {
            throw Translate(e, text);
        }

        return Build(document);
    }

    private static TomlTable Build(DocumentSyntax document)
    {
        var root = new TomlTable();
        foreach (var keyValue in document.KeyValues!)
            Assign(root, KeyPath(keyValue.Key!), ConvertValue(keyValue.Value!));

        foreach (var table in document.Tables!)
        {
            var path = KeyPath(table.Name!);
            if (table is TableArraySyntax)
            {
                var parent = ResolveParent(root, path);
                var name = path[^1];
                if (parent.Get(name) is not TomlArray array)
                {
                    array = new TomlArray([]);
                    parent.Entries[name] = array;
                }

                var entry = new TomlTable();
                array.Items.Add(entry);
                AddItems(entry, table);
            }
            else
            {
                var target = ResolveTable(root, path);
                AddItems(target, table);
            }
        }

        return root;
    }

    private static void AddItems(TomlTable table, TableSyntaxBase syntax)
    {
        foreach (var keyValue in syntax.Items!)
            Assign(table, KeyPath(keyValue.Key!), ConvertValue(keyValue.Value!));
    }

    private static TomlTable ResolveTable(TomlTable root, List<string> path)
    {
        var table = root;
        for (var i = 0; i < path.Count; i++)
        {
            var segment = path[i];
            var existing = table.Get(segment);

            if (i == path.Count - 1)
            {
                if (existing is TomlTable found)
                    return found;
                if (existing is not null)
                    throw new TomlException($"cannot redefine '{string.Join('.', path)}' as a table", 1, 1);

                return CreateTable(table, segment);
            }

            table = existing switch
            {
                TomlTable found => found,
                TomlArray { Items: [.., TomlTable last] } => last,
                null => CreateTable(table, segment),
                _ => throw new TomlException($"'{string.Join('.', path)}' conflicts with an existing value", 1, 1),
            };
        }

        return table;
    }

    private static TomlTable ResolveParent(TomlTable root, List<string> path)
    {
        var table = root;
        for (var i = 0; i < path.Count - 1; i++)
        {
            var segment = path[i];
            table = table.Get(segment) switch
            {
                TomlTable found => found,
                TomlArray { Items: [.., TomlTable last] } => last,
                null => CreateTable(table, segment),
                _ => throw new TomlException($"'{segment}' is not a table", 1, 1),
            };
        }

        return table;
    }

    private static TomlTable CreateTable(TomlTable parent, string key)
    {
        var table = new TomlTable();
        parent.Entries[key] = table;
        return table;
    }

    private static void Assign(TomlTable table, List<string> path, TomlValue value)
    {
        var target = table;
        for (var i = 0; i < path.Count - 1; i++)
        {
            var segment = path[i];
            target = target.Get(segment) switch
            {
                TomlTable found => found,
                null => CreateTable(target, segment),
                _ => throw new TomlException($"'{segment}' is not a table", 1, 1),
            };
        }

        if (!target.Entries.TryAdd(path[^1], value))
            throw new TomlException($"duplicate key '{string.Join('.', path)}'", 1, 1);
    }

    private static List<string> KeyPath(KeySyntax key)
    {
        var path = new List<string> { KeyText(key.Key!) };
        foreach (var dotted in key.DotKeys!)
            path.Add(KeyText(dotted.Key!));
        return path;
    }

    private static string KeyText(BareKeyOrStringValueSyntax key) => key switch
    {
        StringValueSyntax text => text.Value!,
        BareKeySyntax bare => bare.Key!.Text!,
        _ => throw new TomlException($"unsupported key token '{key.Kind}'", 1, 1),
    };

    private static TomlValue ConvertValue(ValueSyntax value) => value switch
    {
        StringValueSyntax text => new TomlString(text.Value!),
        IntegerValueSyntax integer => new TomlInteger(integer.Value),
        FloatValueSyntax number => new TomlFloat(number.Value),
        BooleanValueSyntax flag => new TomlBoolean(flag.Value),
        DateTimeValueSyntax timestamp => new TomlDateTime(timestamp.Token!.Text!),
        ArraySyntax array => new TomlArray(array.Items!.Select(item => ConvertValue(item.Value!)).ToList()),
        InlineTableSyntax table => ConvertInlineTable(table),
        _ => throw new TomlException($"unsupported TOML value '{value.Kind}'", 1, 1),
    };

    private static TomlTable ConvertInlineTable(InlineTableSyntax syntax)
    {
        var table = new TomlTable();
        foreach (var item in syntax.Items!)
            Assign(table, KeyPath(item.KeyValue!.Key!), ConvertValue(item.KeyValue.Value!));
        return table;
    }

    private static TomlException Translate(Tomlyn.TomlException error, string text)
    {
        var diagnostic = error.Diagnostics
            is { Count: > 0 } diagnostics
            ? diagnostics.FirstOrDefault(d => d.Kind == DiagnosticMessageKind.Error) ?? diagnostics[0]
            : null;

        var rawMessage = diagnostic?.Message.Trim() ?? string.Empty;
        if (rawMessage.Length == 0)
            rawMessage = error.Message.Trim();
        var isDepthError = IsDepthError(rawMessage);
        var message = isDepthError
            ? $"the document exceeds the maximum nesting depth of {MaxNestingDepth}"
            : rawMessage;

        if (diagnostic is null)
        {
            if (isDepthError && DepthViolationOffset(text) is int depthOffset)
            {
                var (depthLine, depthColumn) = LineColumn(text, depthOffset);
                return new TomlException(message, depthLine, depthColumn, Snippet(text, depthOffset));
            }

            return new TomlException(message, 1, 1, Snippet(text, 0));
        }

        var offset = Math.Clamp(diagnostic.Span.Start.Offset, 0, text.Length);
        var (line, column) = LineColumn(text, offset);
        return new TomlException(message, line, column, Snippet(text, offset));
    }

    private static bool IsDepthError(string message) =>
        message.Contains("maximum depth", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tomlyn reports a depth violation without a source span, so locate the
    /// container that exceeds the limit by tokenizing the document instead.
    /// </summary>
    private static int? DepthViolationOffset(string text)
    {
        try
        {
            var lexer = TomlLexer.Create(text, SourceName);
            var depth = 0;
            while (lexer.MoveNext())
            {
                switch (lexer.Current.Kind)
                {
                    case TokenKind.OpenBracketDouble:
                    case TokenKind.OpenBracket:
                    case TokenKind.OpenBrace:
                        var opened = lexer.Current.Kind == TokenKind.OpenBracketDouble ? 2 : 1;
                        if (depth + opened >= MaxNestingDepth)
                            return lexer.CurrentSpan.Offset + (MaxNestingDepth - depth) - 1;
                        depth += opened;
                        break;
                    case TokenKind.CloseBracketDouble:
                        depth = Math.Max(0, depth - 2);
                        break;
                    case TokenKind.CloseBracket:
                    case TokenKind.CloseBrace:
                        depth = Math.Max(0, depth - 1);
                        break;
                }
            }
        }
        catch (Tomlyn.TomlException)
        {
        }

        return null;
    }

    private static (int Line, int Column) LineColumn(string text, int offset)
    {
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] != '\n')
                continue;
            line++;
            lineStart = i + 1;
        }

        return (line, offset - lineStart + 1);
    }

    private static string Snippet(string text, int offset)
    {
        var lineStart = offset;
        while (lineStart > 0 && text[lineStart - 1] != '\n')
            lineStart--;
        var lineEnd = offset;
        while (lineEnd < text.Length && text[lineEnd] != '\n')
            lineEnd++;

        const int Context = 60;
        var contextStart = Math.Max(lineStart, offset - Context);
        var contextEnd = Math.Min(lineEnd, offset + Context);
        var prefix = contextStart > lineStart ? "…" : string.Empty;
        var suffix = contextEnd < lineEnd ? "…" : string.Empty;

        var source = text[contextStart..contextEnd].TrimEnd('\r');
        return prefix + source + suffix + "\n"
            + new string(' ', prefix.Length + offset - contextStart) + "^";
    }
}
