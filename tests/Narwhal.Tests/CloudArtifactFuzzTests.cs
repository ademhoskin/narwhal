using System.Text;
using Narwhal.Cloud;
using Xunit;

namespace Narwhal.Tests;

public class CloudArtifactFuzzTests
{
    [Fact]
    public void AnySingleByteMutationFailsVerification()
    {
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson());
        var envelope = CloudArtifactTestData.Envelope(payload);

        for (var i = 0; i < payload.Length; i++)
        {
            var mutated = (byte[])payload.Clone();
            mutated[i] ^= 0x5a;
            var context = CloudArtifactTestData.Context(mutated, envelope);
            Assert.Throws<CloudArtifactException>(() => DesiredArtifactVerifier.Verify(mutated, envelope, context));
        }
    }

    [Fact]
    public void TruncatedPayloadsNeverVerify()
    {
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson());
        var envelope = CloudArtifactTestData.Envelope(payload);

        for (var length = 0; length < payload.Length; length += 7)
        {
            var truncated = payload[..length];
            var context = CloudArtifactTestData.Context(truncated, envelope);
            Assert.Throws<CloudArtifactException>(() => DesiredArtifactVerifier.Verify(truncated, envelope, context));
        }
    }

    [Fact]
    public void ResignedTruncatedPayloadsFailStrictParsing()
    {
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson());

        for (var length = 0; length < payload.Length; length += 31)
        {
            var truncated = payload[..length];
            var envelope = CloudArtifactTestData.Envelope(truncated);
            var context = CloudArtifactTestData.Context(truncated, envelope);
            Assert.Throws<CloudArtifactException>(() => DesiredArtifactVerifier.Verify(truncated, envelope, context));
        }
    }

    [Fact]
    public void RandomDuplicateKeysAreRejected()
    {
        var random = new Random(99117);
        for (var i = 0; i < 300; i++)
        {
            var keys = Enumerable.Range(0, random.Next(1, 6))
                .Select(_ => RandomKey(random))
                .ToArray();
            var duplicate = keys[random.Next(keys.Length)];
            var members = keys.Select(key => $"\"{key}\":{random.Next(100)}").ToList();
            members.Insert(random.Next(members.Count + 1), $"\"{duplicate}\":{random.Next(100)}");

            var json = "{" + string.Join(',', members) + "}";
            var error = Assert.Throws<CloudArtifactException>(() => CloudArtifactTestData.Canonicalize(json));
            Assert.Equal(CloudArtifactErrorCodes.JsonInvalid, error.Code);
        }
    }

    [Fact]
    public void CanonicalBytesAreStableUnderRandomKeyShuffles()
    {
        var random = new Random(31337);
        for (var i = 0; i < 100; i++)
        {
            var members = new[]
            {
                "\"alpha\":1",
                "\"beta\":[true,false,null]",
                "\"gamma\":{\"z\":\"é\",\"a\":2}",
            };
            random.Shuffle(members);
            var canonical = CloudArtifactTestData.Canonicalize("{" + string.Join(',', members) + "}");
            Assert.Equal(
                """{"alpha":1,"beta":[true,false,null],"gamma":{"a":2,"z":"é"}}""",
                Encoding.UTF8.GetString(canonical));
        }
    }

    [Theory]
    [InlineData(1, 0, null, GenerationDecision.Accept)]
    [InlineData(42, 42, "same", GenerationDecision.Idempotent)]
    [InlineData(42, 42, "different", GenerationDecision.Conflict)]
    [InlineData(41, 42, "same", GenerationDecision.Replay)]
    [InlineData(42, 42, "same", GenerationDecision.Blocked)]
    public void GenerationBoundaries(
        long generation,
        long highest,
        string? digestKind,
        GenerationDecision expected)
    {
        var payloadDigest = "sha256:" + new string('a', 64);
        var state = new GenerationState
        {
            HighestGeneration = highest,
            HighestPayloadDigest = digestKind is null
                ? null
                : "sha256:" + new string(digestKind == "same" ? 'a' : 'b', 64),
            BlockedGeneration = expected == GenerationDecision.Blocked ? generation : null,
        };

        Assert.Equal(expected, GenerationCheck.Evaluate(generation, payloadDigest, state));
    }

    private static string RandomKey(Random random)
    {
        var length = random.Next(1, 6);
        var builder = new StringBuilder();
        for (var i = 0; i < length; i++)
            builder.Append((char)('a' + random.Next(26)));
        return builder.ToString();
    }
}
