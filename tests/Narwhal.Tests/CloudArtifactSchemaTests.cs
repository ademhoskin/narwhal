using System.Text;
using Narwhal.Cloud;
using Xunit;

namespace Narwhal.Tests;

public class CloudArtifactSchemaTests
{
    private static CloudArtifactException Fails(string code, string json)
    {
        var payload = CloudArtifactTestData.Canonicalize(json);
        return AssertFails(code, payload, CloudArtifactTestData.Envelope(payload));
    }

    private static CloudArtifactException FailsRaw(string code, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        return AssertFails(code, payload, CloudArtifactTestData.Envelope(payload));
    }

    private static CloudArtifactException AssertFails(string code, byte[] payload, byte[] envelope)
    {
        var error = Assert.Throws<CloudArtifactException>(() =>
            DesiredArtifactVerifier.Verify(payload, envelope, CloudArtifactTestData.Context(payload, envelope)));
        Assert.Equal(code, error.Code);
        return error;
    }

    [Fact]
    public void RejectsUnknownTopLevelField()
    {
        var json = "{" + "\"extra\":1," + CloudArtifactTestData.PayloadJson()[1..];
        Fails(CloudArtifactErrorCodes.SchemaInvalid, json);
    }

    [Fact]
    public void RejectsUnknownNestedField()
    {
        var services = Service(Image(",\"privileged\":true"));
        Fails(CloudArtifactErrorCodes.SchemaInvalid, CloudArtifactTestData.PayloadJson(services: services));
    }

    [Fact]
    public void RejectsDuplicateKeysEvenWithAValidSignature()
    {
        var json = CloudArtifactTestData.PayloadJson();
        var marker = "\"generation\":42,";
        var index = json.IndexOf(marker, StringComparison.Ordinal);
        var duplicate = json[..(index + marker.Length)] + "\"generation\":43," + json[(index + marker.Length)..];

        FailsRaw(CloudArtifactErrorCodes.JsonInvalid, duplicate);
    }

    [Fact]
    public void RejectsTrailingBytesInThePayload()
    {
        var json = Encoding.UTF8.GetString(CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson())) + "{}";
        FailsRaw(CloudArtifactErrorCodes.JsonInvalid, json);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9007199254740992)]
    public void RejectsOutOfRangeGeneration(long generation)
    {
        Fails(CloudArtifactErrorCodes.SchemaInvalid, CloudArtifactTestData.PayloadJson(generation: generation));
    }

    [Theory]
    [InlineData("Shop")]
    [InlineData("-shop")]
    [InlineData("shop-")]
    [InlineData("shop/name")]
    [InlineData("")]
    public void RejectsInvalidProject(string project)
    {
        Fails(CloudArtifactErrorCodes.SchemaInvalid, CloudArtifactTestData.PayloadJson(project: project));
    }

    [Theory]
    [InlineData("2026-11-24T18:30:00Z", true)]
    [InlineData("2026-11-24T18:30:00.123Z", true)]
    [InlineData("2026-11-24T18:30:00+00:00", false)]
    [InlineData("2026-11-24 18:30:00Z", false)]
    [InlineData("2026-13-24T18:30:00Z", false)]
    [InlineData("2026-11-24T18:30:60Z", false)]
    public void ValidatesCreatedTimestamp(string timestamp, bool accepted)
    {
        var json = CloudArtifactTestData.PayloadJson()
            .Replace("2026-11-24T18:30:00Z", timestamp, StringComparison.Ordinal);

        if (accepted)
        {
            var payload = CloudArtifactTestData.Canonicalize(json);
            var envelope = CloudArtifactTestData.Envelope(payload);
            var verified = DesiredArtifactVerifier.Verify(
                payload,
                envelope,
                CloudArtifactTestData.Context(payload, envelope));
            Assert.Equal(timestamp, verified.Document.Created);
        }
        else
        {
            Fails(CloudArtifactErrorCodes.SchemaInvalid, json);
        }
    }

    [Fact]
    public void RejectsMismatchedTrustKeyId()
    {
        var trust = $"[{{\"key_id\":\"{CloudArtifactTestData.KeyId2}\",\"public_key\":\"{CloudArtifactTestData.PublicKey1Base64}\"}}]";
        Fails(CloudArtifactErrorCodes.SchemaInvalid, CloudArtifactTestData.PayloadJson(trust: trust));
    }

    [Fact]
    public void RejectsEmptyTrustArray()
    {
        Fails(CloudArtifactErrorCodes.SchemaInvalid, CloudArtifactTestData.PayloadJson(trust: "[]"));
    }

    [Fact]
    public void RejectsNonStringEnvValue()
    {
        var services = Service(Image(",\"env\":{\"A\":1}"));
        Fails(CloudArtifactErrorCodes.SchemaInvalid, CloudArtifactTestData.PayloadJson(services: services));
    }

    [Fact]
    public void RejectsDuplicateDependsOn()
    {
        var services = "{\"api\":{" + Image(",\"depends_on\":[\"db\",\"db\"]") + "},\"db\":{" + Image() + "}}";
        Fails(CloudArtifactErrorCodes.SchemaInvalid, CloudArtifactTestData.PayloadJson(services: services));
    }

    [Fact]
    public void RejectsDuplicateIngressSource()
    {
        var ingress = """[{"protocol":"tcp","port":8080,"source":["203.0.113.0/24","203.0.113.0/24"]}]""";
        Fails(CloudArtifactErrorCodes.SchemaInvalid, CloudArtifactTestData.PayloadJson(ingress: ingress));
    }

    [Fact]
    public void RejectsEmptyServices()
    {
        Fails(CloudArtifactErrorCodes.SchemaInvalid, CloudArtifactTestData.PayloadJson(services: "{}"));
    }

    [Fact]
    public void RejectsInvalidRepository()
    {
        var images = ImageSpec("Registry/shop/api:tag", "linux/amd64");
        Fails(CloudArtifactErrorCodes.SchemaInvalid, CloudArtifactTestData.PayloadJson(images: images));
    }

    [Fact]
    public void RejectsBadHealthRanges()
    {
        var services = Service(Image(",\"health\":{\"command\":\"true\",\"interval\":301,\"timeout\":1,\"retries\":1}"));
        Fails(CloudArtifactErrorCodes.SchemaInvalid, CloudArtifactTestData.PayloadJson(services: services));
    }

    [Fact]
    public void RejectsBadDeployAndAgentValues()
    {
        Fails(CloudArtifactErrorCodes.SchemaInvalid, CloudArtifactTestData.PayloadJson(deploy: """{"wait_seconds":0}"""));

        var agent = """{"version":"1","repository":"r","digest":"sha256:short","binary_sha256":"abc"}""";
        Fails(CloudArtifactErrorCodes.SchemaInvalid, CloudArtifactTestData.PayloadJson(agent: agent));
    }

    [Fact]
    public void RejectsMissingTargetField()
    {
        var json = CloudArtifactTestData.PayloadJson()
            .Replace("\"provider\":\"aws\",", "", StringComparison.Ordinal);
        Fails(CloudArtifactErrorCodes.SchemaInvalid, json);
    }

    [Fact]
    public void RejectsUnknownSchemaVersion()
    {
        var json = CloudArtifactTestData.PayloadJson()
            .Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal);
        Fails(CloudArtifactErrorCodes.SchemaInvalid, json);
    }

    [Fact]
    public void RejectsUnknownSchemaDiscriminator()
    {
        var json = CloudArtifactTestData.PayloadJson()
            .Replace("narwhal.desired/v1", "narwhal.desired/v2", StringComparison.Ordinal);
        Fails(CloudArtifactErrorCodes.SchemaInvalid, json);
    }

    [Fact]
    public void RejectsImagePlatformMismatch()
    {
        var images = ImageSpec("registry.example.com/shop/api", "linux/arm64");
        Fails(CloudArtifactErrorCodes.SemanticInvalid, CloudArtifactTestData.PayloadJson(images: images));
    }

    [Fact]
    public void RejectsUnknownServiceImage()
    {
        var otherDigest = "sha256:" + new string('c', 64);
        var services = "{\"api\":{\"image\":\"" + otherDigest + "\"}}";
        Fails(CloudArtifactErrorCodes.SemanticInvalid, CloudArtifactTestData.PayloadJson(services: services));
    }

    [Fact]
    public void RejectsUnknownDependency()
    {
        var services = Service(Image(",\"depends_on\":[\"db\"]"));
        Fails(CloudArtifactErrorCodes.SemanticInvalid, CloudArtifactTestData.PayloadJson(services: services));
    }

    [Fact]
    public void RejectsDependencyCycle()
    {
        var services = "{\"api\":{" + Image(",\"depends_on\":[\"db\"]") + "},\"db\":{" + Image(",\"depends_on\":[\"api\"]") + "}}";
        Fails(CloudArtifactErrorCodes.SemanticInvalid, CloudArtifactTestData.PayloadJson(services: services));
    }

    [Fact]
    public void RejectsEnvAndSecretOverlap()
    {
        var services = Service(Image(",\"env\":{\"PASSWORD\":\"literal\"},\"secrets\":{\"PASSWORD\":{\"source\":\"file\",\"id\":\"/run/secret\"}}"));
        Fails(CloudArtifactErrorCodes.SemanticInvalid, CloudArtifactTestData.PayloadJson(services: services));
    }

    [Theory]
    [InlineData("/etc/passwd:/etc/passwd")]
    [InlineData("/srv/narwhal/../etc:/etc")]
    [InlineData("/srv/other:/etc")]
    [InlineData("bad source:/etc")]
    [InlineData("volume")]
    public void RejectsUnsafeVolumes(string volume)
    {
        var services = Service(Image(",\"volumes\":[\"" + volume + "\"]"));
        Fails(CloudArtifactErrorCodes.SemanticInvalid, CloudArtifactTestData.PayloadJson(services: services));
    }

    [Theory]
    [InlineData("shop-data:/var/lib/api")]
    [InlineData("/srv/narwhal/api:/var/lib/api:ro")]
    public void AcceptsSafeVolumes(string volume)
    {
        var services = Service(Image(",\"volumes\":[\"" + volume + "\"]"));
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson(services: services));
        var envelope = CloudArtifactTestData.Envelope(payload);
        var verified = DesiredArtifactVerifier.Verify(
            payload,
            envelope,
            CloudArtifactTestData.Context(payload, envelope));
        Assert.Equal(volume, Assert.Single(verified.Document.Services["api"].Volumes));
    }

    [Fact]
    public void RejectsOutOfRangeServicePort()
    {
        var services = Service(Image(",\"ports\":[\"70000:70000\"]"));
        Fails(CloudArtifactErrorCodes.SemanticInvalid, CloudArtifactTestData.PayloadJson(services: services));
    }

    [Theory]
    [InlineData("999.1.1.1/24")]
    [InlineData("203.0.113.0/33")]
    [InlineData("2001:db8::/129")]
    [InlineData("203.0.113.0")]
    [InlineData("203.0.113.0/24/8")]
    public void RejectsInvalidCidr(string source)
    {
        var ingress = "[{\"protocol\":\"tcp\",\"port\":8080,\"source\":[\"" + source + "\"]}]";
        Fails(CloudArtifactErrorCodes.SemanticInvalid, CloudArtifactTestData.PayloadJson(ingress: ingress));
    }

    [Fact]
    public void AcceptsMixedAddressFamiliesInOneRule()
    {
        var ingress = """[{"protocol":"tcp","port":8080,"source":["203.0.113.0/24","2001:db8::/32"]}]""";
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson(ingress: ingress));
        var envelope = CloudArtifactTestData.Envelope(payload);
        var verified = DesiredArtifactVerifier.Verify(
            payload,
            envelope,
            CloudArtifactTestData.Context(payload, envelope));
        Assert.Equal(2, Assert.Single(verified.Document.Target.Ingress).Source.Count);
    }

    [Fact]
    public void RejectsIngressPort22()
    {
        var ingress = """[{"protocol":"tcp","port":22,"source":["203.0.113.0/24"]}]""";
        Fails(CloudArtifactErrorCodes.SemanticInvalid, CloudArtifactTestData.PayloadJson(ingress: ingress));
    }

    [Fact]
    public void RejectsPreviousGenerationNotLower()
    {
        var previous = PreviousArtifactJson(42);
        Fails(CloudArtifactErrorCodes.SemanticInvalid, CloudArtifactTestData.PayloadJson(previous: previous));
    }

    [Fact]
    public void AcceptsAValidPreviousReference()
    {
        var previous = PreviousArtifactJson(41);
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson(previous: previous));
        var envelope = CloudArtifactTestData.Envelope(payload);
        var verified = DesiredArtifactVerifier.Verify(
            payload,
            envelope,
            CloudArtifactTestData.Context(payload, envelope));
        Assert.Equal(41, verified.Document.Previous!.Generation);
        Assert.Same(verified.Document.Previous, verified.Previous);
    }

    [Fact]
    public void RejectsDuplicateKeysInReplacementTrust()
    {
        var entry = CloudArtifactTestData.DefaultTrustEntry(CloudArtifactTestData.Seed1);
        Fails(CloudArtifactErrorCodes.SemanticInvalid, CloudArtifactTestData.PayloadJson(trust: $"[{entry},{entry}]"));
    }

    [Fact]
    public void AcceptsDistinctEnvAndSecretKeys()
    {
        var services = Service(Image(",\"env\":{\"A\":\"1\"},\"secrets\":{\"B\":{\"source\":\"file\",\"id\":\"/run/b\"}}"));
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson(services: services));
        var envelope = CloudArtifactTestData.Envelope(payload);
        var verified = DesiredArtifactVerifier.Verify(
            payload,
            envelope,
            CloudArtifactTestData.Context(payload, envelope));
        Assert.Equal("1", verified.Document.Services["api"].Env!["A"]);
        Assert.NotNull(verified.Document.Services["api"].Secrets!["B"]);
    }

    private static string Image(string extra = "") =>
        "\"image\":\"" + CloudArtifactTestData.ImageDigest + "\"" + extra;

    private static string Service(string body) => "{\"api\":{" + body + "}}";

    private static string ImageSpec(string repository, string platform) =>
        "{\"" + CloudArtifactTestData.ImageDigest + "\":{\"repository\":\"" + repository +
        "\",\"platform\":\"" + platform + "\",\"size\":1}}";

    private static string PreviousArtifactJson(long generation) =>
        "{\"generation\":" + generation +
        ",\"digest\":\"sha256:" + new string('b', 64) +
        "\",\"manifest_digest\":\"sha256:" + new string('c', 64) + "\"}";

}
