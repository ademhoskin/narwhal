using System.Globalization;
using System.Text;
using Narwhal.Config;
using Xunit;
using Xunit.Sdk;

namespace Narwhal.Tests;

/// <summary>
/// Seeded, hermetic properties for the hand-written TOML parser. Each case
/// renders a document from a model and asserts the parser rebuilds that model,
/// so failures name the seed, the value path and the document.
/// </summary>
public class PropertyTests
{
    private const int Cases = 100;

    [Fact]
    public void GeneratedDocumentsParseToTheirModel()
    {
        for (var seed = 1; seed <= Cases; seed++)
        {
            var random = new Random(seed * 7919);
            var (text, expected) = Generate(random, seed);

            TomlTable actual;
            try
            {
                actual = TomlParser.Parse(text);
            }
            catch (Exception e)
            {
                throw new XunitException($"seed {seed}: generated document failed to parse: {e}\n--- doc ---\n{text}");
            }

            AssertTomlEqual(expected, actual, "$", seed, text);
        }
    }

    [Fact]
    public void GeneratedValidDocumentsNeverThrowOnReparse()
    {
        for (var seed = 1; seed <= Cases; seed++)
        {
            var random = new Random(seed * 104729);
            var (text, _) = Generate(random, seed);

            var first = TomlParser.Parse(text);
            var second = TomlParser.Parse(text);

            AssertTomlEqual(first, second, "$", seed, text);
        }
    }

    private static (string Text, TomlTable Expected) Generate(Random random, int seed)
    {
        var root = new TomlTable();
        var text = new StringBuilder();
        text.Append("# generated document ").Append(seed).AppendLine();
        text.AppendLine();

        var title = RandomText(random, 0, 24, allowSpecial: true);
        Add(root, "title", new TomlString(title));
        text.Append("title = ").Append(Basic(title)).AppendLine("   # escaped title");

        var literal = RandomText(random, 0, 16, allowSpecial: false);
        Add(root, "literal", new TomlString(literal));
        text.Append("literal = ").Append(Literal(literal)).AppendLine();

        var (integerText, integerValue) = RandomInteger(random);
        Add(root, "count", new TomlInteger(integerValue));
        text.Append("count = ").Append(integerText).AppendLine();

        var (floatText, floatValue) = RandomFloat(random);
        Add(root, "ratio", new TomlFloat(floatValue));
        text.Append("ratio = ").Append(floatText).AppendLine();

        var enabled = random.Next(2) == 0;
        Add(root, "enabled", new TomlBoolean(enabled));
        text.Append("enabled = ").Append(enabled ? "true" : "false").AppendLine();

        Add(root, "created", new TomlDateTime("1979-05-27T07:32:00Z"));
        text.AppendLine("created = 1979-05-27T07:32:00Z");

        var meta = new TomlTable();
        Add(root, "meta", meta);
        Add(meta, "owner", new TomlString("team"));
        text.AppendLine("meta.owner = \"team\"");

        var items = Enumerable.Range(0, random.Next(0, 5))
            .Select(_ => RandomText(random, 1, 10, allowSpecial: true))
            .ToList();
        Add(root, "ports", new TomlArray(items.Select(i => (TomlValue)new TomlString(i)).ToList()));
        text.AppendLine("ports = [");
        foreach (var item in items)
            text.Append("  ").Append(Basic(item)).AppendLine(", # item");
        text.AppendLine("]");

        var env = new TomlTable();
        var envEntries = new List<string>();
        if (random.Next(3) != 0)
        {
            var level = RandomText(random, 1, 8, allowSpecial: false);
            Add(env, "LOG_LEVEL", new TomlString(level));
            envEntries.Add("LOG_LEVEL = " + Basic(level));
        }
        if (random.Next(3) != 0)
        {
            var workers = random.Next(0, 32);
            Add(env, "WORKERS", new TomlInteger(workers));
            envEntries.Add("WORKERS = " + workers.ToString(CultureInfo.InvariantCulture));
        }
        if (random.Next(3) == 0)
        {
            var debug = random.Next(2) == 0;
            Add(env, "DEBUG", new TomlBoolean(debug));
            envEntries.Add("DEBUG = " + (debug ? "true" : "false"));
        }
        Add(root, "env", env);
        text.Append("env = { ").Append(string.Join(", ", envEntries)).AppendLine(" }");

        var build = new TomlTable();
        Add(root, "build", build);
        Add(build, "attr_prefix", new TomlString("narwhal.images"));
        text.AppendLine("[build]");
        text.AppendLine("attr_prefix = \"narwhal.images\"");

        var systems = RandomSystems(random);
        Add(build, "systems", new TomlArray(systems.Select(s => (TomlValue)new TomlString(s)).ToList()));
        text.Append("systems = [")
            .Append(string.Join(", ", systems.Select(s => "\"" + s + "\"")))
            .AppendLine("]");

        var deep = new TomlTable();
        var nested = new TomlTable();
        Add(root, "deep", deep);
        Add(deep, "nested", nested);
        Add(nested, "key", new TomlInteger(7));
        text.AppendLine("[deep.nested]");
        text.AppendLine("key = 7");

        var serviceCount = random.Next(1, 4);
        var services = new TomlArray([]);
        Add(root, "service", services);
        for (var index = 0; index < serviceCount; index++)
        {
            var service = new TomlTable();
            services.Items.Add(service);

            var name = $"svc-{index}-{random.Next(0, 1000)}";
            Add(service, "name", new TomlString(name));
            text.AppendLine("[[service]]");
            text.Append("name = ").Append(Basic(name)).AppendLine();

            var servicePorts = Enumerable.Range(0, random.Next(0, 3))
                .Select(_ => $"{random.Next(1024, 65535)}:{random.Next(1, 65535)}")
                .ToList();
            Add(service, "ports",
                new TomlArray(servicePorts.Select(p => (TomlValue)new TomlString(p)).ToList()));
            text.Append("ports = [")
                .Append(string.Join(", ", servicePorts.Select(p => "\"" + p + "\"")))
                .AppendLine("]");

            if (random.Next(2) == 0)
            {
                var health = new TomlTable();
                Add(service, "healthcheck", health);
                Add(health, "command", new TomlString("true"));
                Add(health, "retries", new TomlInteger(3));
                text.AppendLine("[service.healthcheck]");
                text.AppendLine("command = \"true\"");
                text.AppendLine("retries = 3");
            }
        }

        return (text.ToString(), root);
    }

    private static List<string> RandomSystems(Random random)
    {
        var supported = NixPlatform.SupportedSystems.ToList();
        var count = random.Next(1, 4);
        var picked = new List<string>();
        while (picked.Count < count)
        {
            var candidate = supported[random.Next(supported.Count)];
            if (!picked.Contains(candidate, StringComparer.Ordinal))
                picked.Add(candidate);
        }
        return picked;
    }

    private static (string Text, long Value) RandomInteger(Random random)
    {
        switch (random.Next(5))
        {
            case 0:
            {
                var value = random.Next(0, 1_000_000);
                return (value.ToString(CultureInfo.InvariantCulture), value);
            }
            case 1:
            {
                var value = -random.Next(0, 1_000_000);
                return (value.ToString(CultureInfo.InvariantCulture), value);
            }
            case 2:
            {
                var value = random.Next(0, 0xFFFF);
                return ($"0x{value:x}", value);
            }
            case 3:
            {
                var value = random.Next(0, 0xFFF);
                return ($"0o{Convert.ToString(value, 8)}", value);
            }
            default:
                return RandomUnderscoredInteger(random);
        }
    }

    private static (string Text, long Value) RandomUnderscoredInteger(Random random)
    {
        // Rebuild the value so the text and the expectation cannot drift apart.
        var thousands = random.Next(1, 999);
        var units = random.Next(0, 1000);
        return ($"{thousands}_{units:000}", thousands * 1000L + units);
    }

    private static (string Text, double Value) RandomFloat(Random random)
    {
        (string Text, double Value)[] options =
        [
            ("0.0", 0.0),
            ("1.5", 1.5),
            ("-2.25", -2.25),
            ("1e3", 1000.0),
            ("-2.5e-2", -0.025),
            ("1_000.5", 1000.5),
            ("3.14159", 3.14159),
        ];
        return options[random.Next(options.Length)];
    }

    private const string PlainChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 _-.#=,é";

    private const string SpecialChars = "\\\"\n\t";

    private static string RandomText(Random random, int min, int max, bool allowSpecial)
    {
        var length = random.Next(min, max + 1);
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            if (allowSpecial && random.Next(5) == 0)
                builder.Append(SpecialChars[random.Next(SpecialChars.Length)]);
            else
                builder.Append(PlainChars[random.Next(PlainChars.Length)]);
        }
        return builder.ToString();
    }

    private static string Basic(string value) =>
        "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal) + "\"";

    private static string Literal(string value) => "'" + value + "'";

    private static void Add(TomlTable table, string key, TomlValue value) => table.Entries[key] = value;

    private static void AssertTomlEqual(TomlValue expected, TomlValue actual, string path, int seed, string document)
    {
        var context = $"seed {seed}, path {path}\n--- doc ---\n{document}";

        switch (expected)
        {
            case TomlString value:
                if (actual is not TomlString actualString)
                    throw new XunitException($"{context}\nexpected a string, found {actual.Kind}");
                Assert.True(value.Value == actualString.Value,
                    $"{context}\nexpected string {value.Value}, found {actualString.Value}");
                break;

            case TomlInteger value:
                if (actual is not TomlInteger actualInteger)
                    throw new XunitException($"{context}\nexpected an integer, found {actual.Kind}");
                Assert.True(value.Value == actualInteger.Value,
                    $"{context}\nexpected integer {value.Value}, found {actualInteger.Value}");
                break;

            case TomlFloat value:
                if (actual is not TomlFloat actualFloat)
                    throw new XunitException($"{context}\nexpected a float, found {actual.Kind}");
                Assert.True(value.Value == actualFloat.Value,
                    $"{context}\nexpected float {value.Value:R}, found {actualFloat.Value:R}");
                break;

            case TomlBoolean value:
                if (actual is not TomlBoolean actualBoolean)
                    throw new XunitException($"{context}\nexpected a boolean, found {actual.Kind}");
                Assert.True(value.Value == actualBoolean.Value,
                    $"{context}\nexpected boolean {value.Value}, found {actualBoolean.Value}");
                break;

            case TomlDateTime value:
                if (actual is not TomlDateTime actualDateTime)
                    throw new XunitException($"{context}\nexpected a datetime, found {actual.Kind}");
                Assert.True(value.Raw == actualDateTime.Raw,
                    $"{context}\nexpected datetime {value.Raw}, found {actualDateTime.Raw}");
                break;

            case TomlArray value:
                if (actual is not TomlArray actualArray)
                    throw new XunitException($"{context}\nexpected an array, found {actual.Kind}");
                Assert.True(value.Items.Count == actualArray.Items.Count,
                    $"{context}\nexpected {value.Items.Count} items, found {actualArray.Items.Count}");
                for (var i = 0; i < value.Items.Count; i++)
                    AssertTomlEqual(value.Items[i], actualArray.Items[i], $"{path}[{i}]", seed, document);
                break;

            case TomlTable value:
                if (actual is not TomlTable actualTable)
                    throw new XunitException($"{context}\nexpected a table, found {actual.Kind}");
                var expectedKeys = value.Entries.Keys.Order(StringComparer.Ordinal).ToList();
                var actualKeys = actualTable.Entries.Keys.Order(StringComparer.Ordinal).ToList();
                Assert.True(expectedKeys.SequenceEqual(actualKeys, StringComparer.Ordinal),
                    $"{context}\nexpected keys [{string.Join(", ", expectedKeys)}], found [{string.Join(", ", actualKeys)}]");
                foreach (var (key, child) in value.Entries)
                    AssertTomlEqual(child, actualTable.Get(key)!, $"{path}.{key}", seed, document);
                break;

            default:
                throw new XunitException($"{context}\nunexpected model type {expected.GetType().Name}");
        }
    }
}
