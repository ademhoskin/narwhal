using Narwhal.Config;
using Xunit;

namespace Narwhal.Tests;

/// <summary>Invalid manifests must fail as TomlException with a usable position, never as a raw crash.</summary>
public class TomlInvalidInputTests
{
    [Theory]
    [InlineData("name = \"ok\"\nbroken =\n", 2, 9, "expected a value")]
    [InlineData("name = \"abc\n", 1, 12, "unterminated string")]
    [InlineData("name = 'abc\n", 1, 12, "unterminated string")]
    [InlineData("ports = [\"a\", \"b\"\n", 2, 1, "expected ',' or ']'")]
    [InlineData("a = 1\na = 2\n", 2, 6, "duplicate key 'a'")]
    [InlineData("[build\n", 1, 7, "expected ']'")]
    [InlineData("[[service]\n", 1, 11, "expected ']'")]
    [InlineData("x = \"a\\q\"\n", 1, 9, "invalid escape sequence")]
    [InlineData("x = \"\\u12\"\n", 1, 10, "invalid unicode escape")]
    [InlineData("= 1\n", 1, 1, "expected a key")]
    [InlineData("a = 1 b\n", 1, 7, "expected a newline after the value")]
    [InlineData("x = \"\"\"abc\n", 2, 1, "unterminated multi-line string")]
    [InlineData("x = '''abc\n", 2, 1, "unterminated multi-line string")]
    [InlineData("x = { a = 1\n", 1, 12, "expected '}' but found")]
    [InlineData("[[a]]\nx = 1\n[a]\n", 3, 4, "cannot redefine 'a' as a table")]
    [InlineData("a = 1\na.b = 2\n", 2, 8, "cannot assign a nested key")]
    [InlineData("x = { a = 1, a = 2 }\n", 1, 20, "duplicate key 'a' in inline table")]
    public void RejectsInvalidDocumentsAtTheRightPosition(string document, int line, int column, string message)
    {
        var error = Assert.Throws<TomlException>(() => TomlParser.Parse(document));

        Assert.True(line == error.Line, $"expected line {line}, got {error.Line}: {error.Message}");
        Assert.True(column == error.Column, $"expected column {column}, got {error.Column}: {error.Message}");
        Assert.Contains(message, error.Message);
        Assert.False(string.IsNullOrWhiteSpace(error.Snippet), "the error should carry a source snippet");
    }

    [Theory]
    [InlineData("x = \"\\uD800\"\n")]
    [InlineData("x = \"\\U0000D800\"\n")]
    [InlineData("x = \"\\U00110000\"\n")]
    [InlineData("x = \"\\UFFFFFFFF\"\n")]
    public void RejectsUnicodeEscapesOutsideTheScalarRange(string document)
    {
        // Surrogate and above-U+10FFFF code points are not valid TOML and must
        // not escape as ArgumentOutOfRangeException/OverflowException.
        var error = Assert.Throws<TomlException>(() => TomlParser.Parse(document));
        Assert.Contains("invalid unicode escape", error.Message);
    }

    [Theory]
    [InlineData("count = 0xDEAD_BEEF", 0xDEADBEEFL)]
    [InlineData("count = 0o17", 15L)]
    [InlineData("count = 0b1010", 10L)]
    [InlineData("count = 1_000_000", 1_000_000L)]
    [InlineData("count = -0x10", -16L)]
    [InlineData("count = +42", 42L)]
    public void ParsesEveryIntegerForm(string document, long expected)
    {
        var table = TomlParser.Parse(document);
        Assert.Equal(expected, Assert.IsType<TomlInteger>(table.Get("count")).Value);
    }

    [Theory]
    [InlineData("ratio = 1e3", 1000.0)]
    [InlineData("ratio = -2.5e-2", -0.025)]
    [InlineData("ratio = 1_000.5", 1000.5)]
    [InlineData("ratio = 0.0", 0.0)]
    public void ParsesFloatsWithExponentsAndSeparators(string document, double expected)
    {
        var table = TomlParser.Parse(document);
        Assert.Equal(expected, Assert.IsType<TomlFloat>(table.Get("ratio")).Value);
    }

    [Theory]
    [InlineData("stamp = 1979-05-27T07:32:00Z", "1979-05-27T07:32:00Z")]
    [InlineData("stamp = 1979-05-27", "1979-05-27")]
    public void ParsesDateTimesAsRawTokens(string document, string expected)
    {
        var table = TomlParser.Parse(document);
        Assert.Equal(expected, Assert.IsType<TomlDateTime>(table.Get("stamp")).Raw);
    }

    [Fact]
    public void ParsesCommentOnlyAndEmptyDocuments()
    {
        Assert.Empty(TomlParser.Parse("").Entries);
        Assert.Empty(TomlParser.Parse("# only a comment\n").Entries);
    }

    [Fact]
    public void ParsesEmptyContainersAndQuotedKeys()
    {
        var table = TomlParser.Parse("""
            "" = "empty key"
            list = []
            map = {}
            nested = { a.b = 1 }
            """);

        Assert.Equal("empty key", Assert.IsType<TomlString>(table.Get("")).Value);
        Assert.Empty(Assert.IsType<TomlArray>(table.Get("list")).Items);
        Assert.Empty(Assert.IsType<TomlTable>(table.Get("map")).Entries);
        var nested = Assert.IsType<TomlTable>(Assert.IsType<TomlTable>(table.Get("nested")).Get("a"));
        Assert.Equal(1, Assert.IsType<TomlInteger>(nested.Get("b")).Value);
    }
}
