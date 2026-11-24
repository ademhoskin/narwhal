using System.Text;
using Narwhal.Cloud;
using Xunit;

namespace Narwhal.Tests;

public class CloudArtifactCanonicalizationTests
{
    private static string Canonical(string json) => Encoding.UTF8.GetString(CloudArtifactTestData.Canonicalize(json));

    [Fact]
    public void SortsObjectKeysByUtf16CodeUnits()
    {
        Assert.Equal(
            """{"z":1,"é":2,"😀":3}""",
            Canonical("""{"😀":3,"é":2,"z":1}"""));
    }

    [Fact]
    public void RemovesInsignificantWhitespace()
    {
        Assert.Equal(
            """{"a":[1,2],"b":{"c":true,"d":null}}""",
            Canonical(" { \"b\" : { \"d\" : null , \"c\" : true } , \"a\" : [ 1 , 2 ] } "));
    }

    [Fact]
    public void UsesJcsStringEscapes()
    {
        var input = "{\"s\":\"a\\u0001b\\\"c\\\\d\\ne\\u001ff\"}";
        var expected = "{\"s\":\"a\\u0001b\\\"c\\\\d\\ne\\u001ff\"}";
        Assert.Equal(expected, Canonical(input));
    }

    [Fact]
    public void LeavesNonAsciiLiteral()
    {
        Assert.Equal("""{"s":"héllo"}""", Canonical("""{"s":"héllo"}"""));
    }

    [Theory]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("""{"a":{"b":1,"b":2}}""")]
    [InlineData("""{"a":1,"\u0061":2}""")]
    public void RejectsDuplicateKeysAtAnyDepth(string json)
    {
        var error = Assert.Throws<CloudArtifactException>(() => CloudArtifactTestData.Canonicalize(json));
        Assert.Equal(CloudArtifactErrorCodes.JsonInvalid, error.Code);
    }

    [Theory]
    [InlineData("""{"a":1} garbage""")]
    [InlineData("""{"a":1}{"b":2}""")]
    [InlineData("""{"a":1} 0""")]
    public void RejectsTrailingBytes(string json)
    {
        var error = Assert.Throws<CloudArtifactException>(() => CloudArtifactTestData.Canonicalize(json));
        Assert.Equal(CloudArtifactErrorCodes.JsonInvalid, error.Code);
    }

    [Theory]
    [InlineData("""{"a":1.0}""")]
    [InlineData("""{"a":1e2}""")]
    [InlineData("""{"a":-0.5}""")]
    [InlineData("""{"a":9223372036854775808}""")]
    public void RejectsNonIntegerNumbers(string json)
    {
        var error = Assert.Throws<CloudArtifactException>(() => CloudArtifactTestData.Canonicalize(json));
        Assert.Equal(CloudArtifactErrorCodes.JsonInvalid, error.Code);
    }

    [Theory]
    [InlineData("""{"a":"\ud800"}""")]
    [InlineData("""{"a":"\udc00"}""")]
    public void RejectsLoneSurrogates(string json)
    {
        var error = Assert.Throws<CloudArtifactException>(() => CloudArtifactTestData.Canonicalize(json));
        Assert.Equal(CloudArtifactErrorCodes.JsonInvalid, error.Code);
    }

    [Fact]
    public void RejectsInvalidUtf8()
    {
        var bytes = new byte[] { (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'"', 0xff, (byte)'"', (byte)'}' };
        Assert.Throws<CloudArtifactException>(() => CanonicalJson.Canonicalize(bytes));
    }

    [Fact]
    public void DetectsNonCanonicalBytes()
    {
        Assert.False(CanonicalJson.IsCanonical(Encoding.UTF8.GetBytes("""{"a": 1}""")));
        Assert.False(CanonicalJson.IsCanonical(Encoding.UTF8.GetBytes("""{"b":1,"a":1}""")));
        Assert.True(CanonicalJson.IsCanonical(Encoding.UTF8.GetBytes("""{"a":1}""")));
    }

    [Fact]
    public void CanonicalizationIsIdempotentForRandomDocuments()
    {
        var random = new Random(424242);
        for (var i = 0; i < 200; i++)
        {
            var json = RandomJson(random, 0);
            var once = CloudArtifactTestData.Canonicalize(json);
            var twice = CanonicalJson.Canonicalize(once);
            Assert.Equal(once, twice);
        }
    }

    [Fact]
    public void KeyOrderDoesNotChangeCanonicalBytes()
    {
        var random = new Random(7);
        for (var i = 0; i < 100; i++)
        {
            var left = RandomJson(random, 0);
            var right = RandomJson(random, 0);
            var leftCanonical = CloudArtifactTestData.Canonicalize(left);
            var rightCanonical = CloudArtifactTestData.Canonicalize(right);
            Assert.Equal(leftCanonical, rightCanonical);
        }
    }

    private static string RandomJson(Random random, int depth)
    {
        // Every generated document has the same key set but a shuffled member order.
        if (depth > 2)
            return "\"leaf\"";

        var keys = new[] { "z", "a", "m" };
        random.Shuffle(keys);
        var members = keys.Select(key =>
        {
            var value = key switch
            {
                "a" => "1",
                "m" => "\"text\"",
                _ => depth < 2 ? RandomJson(random, depth + 1) : "null",
            };
            return $"\"{key}\":{value}";
        });
        return "{" + string.Join(',', members) + "}";
    }
}

internal static class RandomExtensions
{
    public static void Shuffle<T>(this Random random, T[] items)
    {
        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
