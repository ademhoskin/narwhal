using Narwhal.Config;
using Xunit;

namespace Narwhal.Tests;

public class TomlTests
{
    private static TomlTable Parse(string text) => TomlParser.Parse(text);

    private static string Str(TomlTable table, string key) =>
        Assert.IsType<TomlString>(table.Get(key)).Value;

    [Fact]
    public void ParsesScalars()
    {
        var table = Parse("""
            name = "narwhal"
            count = 42
            ratio = 1.5
            enabled = true
            disabled = false
            """);

        Assert.Equal("narwhal", Str(table, "name"));
        Assert.Equal(42, Assert.IsType<TomlInteger>(table.Get("count")).Value);
        Assert.Equal(1.5, Assert.IsType<TomlFloat>(table.Get("ratio")).Value);
        Assert.True(Assert.IsType<TomlBoolean>(table.Get("enabled")).Value);
        Assert.False(Assert.IsType<TomlBoolean>(table.Get("disabled")).Value);
    }

    [Fact]
    public void ParsesStringEscapesAndLiteralStrings()
    {
        var table = Parse("""
            escaped = "line\nbreak\ttab \"quoted\" \\ backslash \u00e9"
            literal = 'C:\path\no\escapes'
            """);

        Assert.Equal("line\nbreak\ttab \"quoted\" \\ backslash é", Str(table, "escaped"));
        Assert.Equal(@"C:\path\no\escapes", Str(table, "literal"));
    }

    [Fact]
    public void ParsesMultilineStrings()
    {
        var table = Parse(""""
            basic = """
            first
            second\
              joined
            """
            literal = '''
            raw \n stays
            '''
            """");

        Assert.Equal("first\nsecondjoined", Str(table, "basic").Replace("\r\n", "\n").TrimEnd('\n'));
        Assert.Equal("raw \\n stays", Str(table, "literal").TrimEnd('\n'));
    }

    [Fact]
    public void ParsesArraysIncludingMultilineAndTrailingCommas()
    {
        var table = Parse("""
            ports = ["8080:8080", "9090:9090",]
            nested = [
              "a", # comment inside
              "b",
            ]
            """);

        var ports = Assert.IsType<TomlArray>(table.Get("ports"));
        Assert.Equal(["8080:8080", "9090:9090"], ports.Items.Select(i => Assert.IsType<TomlString>(i).Value));
        Assert.Equal(2, Assert.IsType<TomlArray>(table.Get("nested")).Items.Count);
    }

    [Fact]
    public void ParsesInlineTablesAndDottedKeys()
    {
        var table = Parse("""
            service.name = "api"
            env = { LOG_LEVEL = "info", PORT = 3000 }
            """);

        var service = Assert.IsType<TomlTable>(table.Get("service"));
        Assert.Equal("api", Str(service, "name"));

        var env = Assert.IsType<TomlTable>(table.Get("env"));
        Assert.Equal("info", Str(env, "LOG_LEVEL"));
        Assert.Equal(3000, Assert.IsType<TomlInteger>(env.Get("PORT")).Value);
    }

    [Fact]
    public void ParsesTablesAndArraysOfTables()
    {
        var table = Parse("""
            name = "acme"

            [build]
            attr_prefix = "narwhal.images"

            [[service]]
            name = "api"
            ports = ["8080:8080"]

            [service.healthcheck]
            command = "curl -fsS localhost:8080"
            retries = 3

            [[service]]
            name = "db"
            """);

        Assert.Equal("acme", Str(table, "name"));
        var build = Assert.IsType<TomlTable>(table.Get("build"));
        Assert.Equal("narwhal.images", Str(build, "attr_prefix"));

        var services = Assert.IsType<TomlArray>(table.Get("service")).Items;
        Assert.Equal(2, services.Count);

        var api = Assert.IsType<TomlTable>(services[0]);
        Assert.Equal("api", Str(api, "name"));
        var health = Assert.IsType<TomlTable>(api.Get("healthcheck"));
        Assert.Equal(3, Assert.IsType<TomlInteger>(health.Get("retries")).Value);

        var db = Assert.IsType<TomlTable>(services[1]);
        Assert.Equal("db", Str(db, "name"));
    }

    [Fact]
    public void SupportsQuotedAndDashedKeys()
    {
        var table = Parse("""
            "odd key" = "value"
            dashed-key = "dash"
            """);

        Assert.Equal("value", Str(table, "odd key"));
        Assert.Equal("dash", Str(table, "dashed-key"));
    }

    [Fact]
    public void IgnoresCommentsAndBlankLines()
    {
        var table = Parse("""

            # leading comment
            name = "x" # trailing comment

            """);

        Assert.Equal("x", Str(table, "name"));
    }

    [Fact]
    public void ReportsTheLineOfATomlError()
    {
        var error = Assert.Throws<TomlException>(() => Parse("""
            name = "ok"
            broken =
            """));

        Assert.Equal(2, error.Line);
        Assert.Contains("expected a value", error.Message);
        Assert.NotNull(error.Snippet);
    }

    [Fact]
    public void RejectsDuplicateKeys()
    {
        var error = Assert.Throws<TomlException>(() => Parse("""
            name = "a"
            name = "b"
            """));

        Assert.Contains("duplicate key", error.Message);
    }

    [Fact]
    public void AcceptsCrLfLineEndings()
    {
        var table = Parse("name = \"crlf\"\r\ncount = 2\r\n");
        Assert.Equal("crlf", Str(table, "name"));
        Assert.Equal(2, Assert.IsType<TomlInteger>(table.Get("count")).Value);
    }
}
