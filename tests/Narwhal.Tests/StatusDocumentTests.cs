using System.Text;
using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class StatusDocumentTests
{
    [Fact]
    public void EncodesAndDecodesCanonicalStatus()
    {
        var document = StatusTestData.Document(withError: true);
        var payload = StatusDocumentCodec.Encode(document);

        Assert.True(CanonicalJson.IsCanonical(payload));
        Assert.Equal(payload, StatusDocumentCodec.Encode(document));

        var decoded = StatusDocumentCodec.Decode(payload);
        Assert.Equal("narwhal.status/v1", decoded.Schema);
        Assert.Equal(1, decoded.Version);
        Assert.Equal(ReconcileTestData.Project, decoded.Project);
        Assert.Equal(184, decoded.StatusSeq);
        Assert.Equal(3, decoded.Epoch);
        Assert.Equal(StatusTestData.HostId, decoded.Host.Id);
        Assert.Equal(StatusTestData.BootId, decoded.Host.BootId);
        Assert.Equal(CloudArtifactTestData.KeyId1, decoded.Host.StatusKeyId);
        Assert.Equal(42, decoded.HighestGeneration);
        Assert.Equal(ReconcilePhases.Idle, decoded.Phase);
        Assert.Equal(42, decoded.Observed?.Generation);
        Assert.Equal(42, decoded.Active?.Generation);
        Assert.Equal(1, decoded.Attempts?.Count);
        Assert.Equal("healthy", decoded.Services["api"].Health);
        Assert.Equal("health_timeout", decoded.LastError?.Code);
        Assert.Single(decoded.LogsTail);
    }

    [Fact]
    public void WritesExplicitNullsForTheDocumentedShape()
    {
        var payload = Encoding.UTF8.GetString(StatusDocumentCodec.Encode(StatusTestData.Document()));

        Assert.Contains("\"blocked_generation\":null", payload, StringComparison.Ordinal);
        Assert.Contains("\"last_error\":null", payload, StringComparison.Ordinal);
        Assert.Contains("\"agent_rollback_version\":null", payload, StringComparison.Ordinal);
        Assert.Contains("\"blocked_agent_digest\":null", payload, StringComparison.Ordinal);
        Assert.Contains("\"status_key_id\":\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"schema\":\"narwhal.status/v1\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNonCanonicalBytes()
    {
        var payload = StatusDocumentCodec.Encode(StatusTestData.Document());
        var spaced = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload).Replace("{", "{ ", StringComparison.Ordinal));

        var error = Assert.Throws<CloudArtifactException>(() => StatusDocumentCodec.Decode(spaced));
        Assert.Equal(CloudArtifactErrorCodes.NonCanonical, error.Code);
    }

    [Fact]
    public void RejectsDuplicateJsonKeys()
    {
        var error = Assert.Throws<CloudArtifactException>(
            () => StatusDocumentCodec.Decode("{\"schema\":\"narwhal.status/v1\",\"schema\":\"narwhal.status/v1\"}"u8));

        Assert.Equal(CloudArtifactErrorCodes.JsonInvalid, error.Code);
    }

    [Fact]
    public void AcceptsEveryArtifactErrorCodeAsLastError()
    {
        foreach (var field in typeof(CloudArtifactErrorCodes).GetFields())
        {
            var document = StatusTestData.Document(withError: true);
            document.LastError!.Code = (string)field.GetValue(null)!;

            var decoded = StatusDocumentCodec.Decode(StatusDocumentCodec.Encode(document));

            Assert.Equal(document.LastError.Code, decoded.LastError?.Code);
        }
    }

    [Theory]
    [InlineData("health_failed")]
    [InlineData("health_exited")]
    [InlineData("rollback_failed")]
    [InlineData("cache_mismatch")]
    [InlineData("previous_unavailable")]
    [InlineData("bind_mount_missing")]
    [InlineData("state_io_failed")]
    [InlineData("status_io_failed")]
    [InlineData("secret_source_unsupported")]
    [InlineData("secret_resolution_failed")]
    public void AcceptsEngineRecoveryErrorCodes(string code)
    {
        var document = StatusTestData.Document(withError: true);
        document.LastError!.Code = code;

        var decoded = StatusDocumentCodec.Decode(StatusDocumentCodec.Encode(document));

        Assert.Equal(code, decoded.LastError?.Code);
    }

    [Fact]
    public void RejectsAnUnknownErrorCodeBeforeSigning()
    {
        var document = StatusTestData.Document(withError: true);
        document.LastError!.Code = "not_a_published_code";

        var error = Assert.Throws<CloudArtifactException>(() => StatusDocumentCodec.Encode(document));
        Assert.Equal(CloudArtifactErrorCodes.SchemaInvalid, error.Code);
    }

    [Fact]
    public void RejectsAMissingRequiredField()
    {
        var payload = StatusDocumentCodec.Encode(StatusTestData.Document());
        var text = Encoding.UTF8.GetString(payload).Replace("\"phase\":\"idle\",", "", StringComparison.Ordinal);
        var mutated = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(text));

        var error = Assert.Throws<CloudArtifactException>(() => StatusDocumentCodec.Decode(mutated));
        Assert.Equal(CloudArtifactErrorCodes.SchemaInvalid, error.Code);
    }

    [Fact]
    public void RejectsAnUnknownField()
    {
        var payload = StatusDocumentCodec.Encode(StatusTestData.Document());
        var text = Encoding.UTF8.GetString(payload);
        var mutated = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(
            text[..^1] + ",\"unexpected\":true}"));

        var error = Assert.Throws<CloudArtifactException>(() => StatusDocumentCodec.Decode(mutated));
        Assert.Equal(CloudArtifactErrorCodes.SchemaInvalid, error.Code);
    }

    [Fact]
    public void RejectsTooManyLogLines()
    {
        var document = StatusTestData.Document();
        document.LogsTail = Enumerable.Range(0, 201)
            .Select(index => new StatusLogLine
            {
                Service = "api",
                At = "2026-11-24T18:31:05Z",
                Line = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .ToList();

        var payload = StatusDocumentCodec.Encode(document);
        var error = Assert.Throws<CloudArtifactException>(() => StatusDocumentCodec.Decode(payload));
        Assert.Contains("200", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnUnknownPhase()
    {
        var document = StatusTestData.Document();
        document.Phase = "invented";
        var payload = StatusDocumentCodec.Encode(document);

        var error = Assert.Throws<CloudArtifactException>(() => StatusDocumentCodec.Decode(payload));
        Assert.Contains("phase", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DerivesTheDocumentedStatusRepository()
    {
        var target = new TargetConfig
        {
            Provider = "aws",
            Region = "us-east-1",
            Arch = "amd64",
            Selector = new Dictionary<string, string>(StringComparer.Ordinal) { ["stack"] = "prod" },
        };

        Assert.Equal(
            "shop/state/aws-us-east-1-amd64-37cbdd65/status",
            StatusRepository.Derive("shop", target));
    }
}
