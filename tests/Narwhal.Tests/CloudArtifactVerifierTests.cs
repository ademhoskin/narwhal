using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using Narwhal.Cloud;
using Xunit;

namespace Narwhal.Tests;

public class CloudArtifactVerifierTests
{
    private static VerifiedDesiredArtifact VerifyJson(
        string json,
        byte[][]? seeds = null,
        TrustSet? trust = null,
        TargetExpectation? target = null,
        GenerationState? generation = null)
    {
        var payload = CloudArtifactTestData.Canonicalize(json);
        var envelope = CloudArtifactTestData.Envelope(payload, seeds ?? [CloudArtifactTestData.Seed1]);
        return DesiredArtifactVerifier.Verify(
            payload,
            envelope,
            CloudArtifactTestData.Context(payload, envelope, trust, target, generation));
    }

    private static CloudArtifactException AssertFails(string code, Action action)
    {
        var error = Assert.Throws<CloudArtifactException>(action);
        Assert.Equal(code, error.Code);
        return error;
    }

    [Fact]
    public void AcceptsAHappyPathArtifact()
    {
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson());
        var envelope = CloudArtifactTestData.Envelope(payload);

        var verified = DesiredArtifactVerifier.Verify(
            payload,
            envelope,
            CloudArtifactTestData.Context(payload, envelope));

        Assert.Equal(GenerationDecision.Accept, verified.Generation);
        Assert.Equal("shop", verified.Document.Project);
        Assert.Equal(42, verified.Document.Generation);
        Assert.Equal("aws", verified.Document.Target.Provider);
        Assert.Equal("prod", verified.Document.Target.Selector["stack"]);
        Assert.Equal("8080:8080", Assert.Single(verified.Document.Services["api"].Ports));
        Assert.Equal(CloudArtifactTestData.ImageDigest, verified.Document.Services["api"].Image);
        Assert.Contains(CloudArtifactTestData.ImageDigest, verified.Document.Images.Keys);
        Assert.Equal([CloudArtifactTestData.KeyId1], verified.VerifyingKeyIds);
        Assert.Null(verified.Previous);
        Assert.NotNull(verified.NextTrustSet);
        Assert.Equal(CloudArtifactTestData.KeyId1, Assert.Single(verified.NextTrustSet.Keys).KeyId);

        var expectedDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(payload));
        Assert.Equal(expectedDigest, verified.PayloadDigest);
    }

    [Fact]
    public void AcceptsAPayloadWithoutATrustArray()
    {
        var json = CloudArtifactTestData.PayloadJson(includeTrust: false);
        var verified = VerifyJson(json);

        Assert.Null(verified.NextTrustSet);
        Assert.Equal(GenerationDecision.Accept, verified.Generation);
    }

    [Fact]
    public void AcceptsExplicitNullPrevious()
    {
        var verified = VerifyJson(CloudArtifactTestData.PayloadJson(previous: "null"));
        Assert.Null(verified.Document.Previous);
    }

    [Fact]
    public void AcceptsAnAgentSelectorSuperset()
    {
        var verified = VerifyJson(
            CloudArtifactTestData.PayloadJson(),
            target: CloudArtifactTestData.Target(
                selector: new Dictionary<string, string> { ["stack"] = "prod", ["env"] = "staging" }));
        Assert.Equal("shop", verified.Document.Project);
    }

    [Fact]
    public void IdempotentGenerationAndDigest()
    {
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson());
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(payload));
        var envelope = CloudArtifactTestData.Envelope(payload);
        var context = CloudArtifactTestData.Context(
            payload,
            envelope,
            generation: new GenerationState
            {
                HighestGeneration = 42,
                HighestPayloadDigest = digest,
            });

        var verified = DesiredArtifactVerifier.Verify(
            payload,
            envelope,
            context);

        Assert.Equal(GenerationDecision.Idempotent, verified.Generation);
    }

    [Fact]
    public void RejectsGenerationReplay()
    {
        AssertFails(
            CloudArtifactErrorCodes.GenerationReplay,
            () => VerifyJson(
                CloudArtifactTestData.PayloadJson(),
                generation: new GenerationState { HighestGeneration = 43 }));
    }

    [Fact]
    public void RejectsGenerationConflict()
    {
        AssertFails(
            CloudArtifactErrorCodes.GenerationConflict,
            () => VerifyJson(
                CloudArtifactTestData.PayloadJson(),
                generation: new GenerationState
                {
                    HighestGeneration = 42,
                    HighestPayloadDigest = "sha256:" + new string('b', 64),
                }));
    }

    [Fact]
    public void RejectsBlockedGeneration()
    {
        AssertFails(
            CloudArtifactErrorCodes.GenerationBlocked,
            () => VerifyJson(
                CloudArtifactTestData.PayloadJson(),
                generation: new GenerationState
                {
                    HighestGeneration = 42,
                    HighestPayloadDigest = "sha256:" + new string('b', 64),
                    BlockedGeneration = 42,
                }));
    }

    [Fact]
    public void RejectsAnEnvelopeDigestMismatch()
    {
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson());
        var envelope = Encoding.UTF8.GetString(CloudArtifactTestData.Envelope(payload))
            .Replace(
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(payload)),
                "sha256:" + new string('b', 64),
                StringComparison.Ordinal);
        var envelopeBytes = Encoding.UTF8.GetBytes(envelope);

        AssertFails(
            CloudArtifactErrorCodes.DigestMismatch,
            () => DesiredArtifactVerifier.Verify(
                payload,
                envelopeBytes,
                CloudArtifactTestData.Context(payload, envelopeBytes)));
    }

    [Fact]
    public void RejectsASignatureOverDifferentBytes()
    {
        var signedPayload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson(generation: 41));
        var otherPayload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson(generation: 43));
        var envelope = Encoding.UTF8.GetString(CloudArtifactTestData.Envelope(signedPayload))
            .Replace(
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(signedPayload)),
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(otherPayload)),
                StringComparison.Ordinal);
        var envelopeBytes = Encoding.UTF8.GetBytes(envelope);

        AssertFails(
            CloudArtifactErrorCodes.SignatureInvalid,
            () => DesiredArtifactVerifier.Verify(
                otherPayload,
                envelopeBytes,
                CloudArtifactTestData.Context(otherPayload, envelopeBytes)));
    }

    [Fact]
    public void RejectsAnUnknownKey()
    {
        AssertFails(
            CloudArtifactErrorCodes.SignatureInvalid,
            () => VerifyJson(
                CloudArtifactTestData.PayloadJson(),
                trust: CloudArtifactTestData.Trust(CloudArtifactTestData.Seed2)));
    }

    [Fact]
    public void RejectsASignatureUnderADifferentKeyId()
    {
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson());
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(payload));
        var signature = CloudEncoding.ToBase64Url(CloudArtifactTestData.SignPayload(CloudArtifactTestData.Seed2, payload));
        var envelope = Encoding.UTF8.GetBytes(
            $"{{\"schema\":\"narwhal.signature/v1\",\"payload_digest\":\"{digest}\"," +
            $"\"signatures\":[{{\"alg\":\"ed25519\",\"key_id\":\"{CloudArtifactTestData.KeyId1}\",\"sig\":\"{signature}\"}}]}}");

        AssertFails(
            CloudArtifactErrorCodes.SignatureInvalid,
            () => DesiredArtifactVerifier.Verify(
                payload,
                envelope,
                CloudArtifactTestData.Context(payload, envelope)));
    }

    [Fact]
    public void RejectsAnExpiredKey()
    {
        var trust = TrustSet.FromKeys([
            new TrustedKey
            {
                KeyId = CloudArtifactTestData.KeyId1,
                PublicKey = CloudArtifactTestData.PublicKey1,
                NotAfter = CloudArtifactTestData.Now.AddSeconds(-1),
            },
        ]);

        AssertFails(
            CloudArtifactErrorCodes.SignatureInvalid,
            () => VerifyJson(CloudArtifactTestData.PayloadJson(), trust: trust));
    }

    [Fact]
    public void RejectsZeroSignatures()
    {
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson());
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(payload));
        var envelope = Encoding.UTF8.GetBytes(
            $"{{\"schema\":\"narwhal.signature/v1\",\"payload_digest\":\"{digest}\",\"signatures\":[]}}");

        AssertFails(
            CloudArtifactErrorCodes.SignatureInvalid,
            () => DesiredArtifactVerifier.Verify(
                payload,
                envelope,
                CloudArtifactTestData.Context(payload, envelope)));
    }

    [Theory]
    [InlineData("""{"schema":"narwhal.signature/v1","payload_digest":"%DIGEST%","signatures":[{"alg":"ed25519","key_id":"%KEY%","sig":"AAAA"}]}""")]
    [InlineData("""{"schema":"narwhal.signature/v2","payload_digest":"%DIGEST%","signatures":[{"alg":"ed25519","key_id":"%KEY%","sig":"%SIG%"}]}""")]
    [InlineData("""{"schema":"narwhal.signature/v1","payload_digest":"%DIGEST%","signatures":[{"alg":"ed25519","key_id":"%KEY%","sig":"%SIG%"}]} trailing""")]
    public void RejectsMalformedSignatureEnvelopes(string template)
    {
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson());
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(payload));
        var signature = CloudEncoding.ToBase64Url(CloudArtifactTestData.SignPayload(CloudArtifactTestData.Seed1, payload));
        var json = template
            .Replace("%DIGEST%", digest, StringComparison.Ordinal)
            .Replace("%KEY%", CloudArtifactTestData.KeyId1, StringComparison.Ordinal)
            .Replace("%SIG%", signature, StringComparison.Ordinal);
        var envelope = Encoding.UTF8.GetBytes(json);

        AssertFails(
            CloudArtifactErrorCodes.SignatureInvalid,
            () => DesiredArtifactVerifier.Verify(
                payload,
                envelope,
                CloudArtifactTestData.Context(payload, envelope)));
    }

    [Fact]
    public void RejectsNonCanonicalPayloadBytes()
    {
        var canonical = Encoding.UTF8.GetString(CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson()));
        var nonCanonical = Encoding.UTF8.GetBytes(canonical.Replace("\"schema\":", "\"schema\": ", StringComparison.Ordinal));
        var envelope = CloudArtifactTestData.Envelope(nonCanonical);

        AssertFails(
            CloudArtifactErrorCodes.NonCanonical,
            () => DesiredArtifactVerifier.Verify(
                nonCanonical,
                envelope,
                CloudArtifactTestData.Context(nonCanonical, envelope)));
    }

    [Fact]
    public void RejectsNonCanonicalKeyOrder()
    {
        var canonical = Encoding.UTF8.GetString(CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson()));

        var first = canonical.IndexOf("\"schema\":", StringComparison.Ordinal);
        var end = canonical.IndexOf(',', first);
        var schemaMember = canonical[first..end];
        var withoutSchema = canonical.Remove(first, end + 1 - first);
        var withSchemaAtEnd = "{" + withoutSchema[1..^1] + "," + schemaMember + "}";
        Assert.NotEqual(canonical, withSchemaAtEnd);

        var moved = Encoding.UTF8.GetBytes(withSchemaAtEnd);
        var movedEnvelope = CloudArtifactTestData.Envelope(moved);
        AssertFails(
            CloudArtifactErrorCodes.NonCanonical,
            () => DesiredArtifactVerifier.Verify(
                moved,
                movedEnvelope,
                CloudArtifactTestData.Context(moved, movedEnvelope)));
    }

    [Fact]
    public void AcceptsAMatchingTarget()
    {
        var json = CloudArtifactTestData.PayloadJson();

        var verified = VerifyJson(json, target: CloudArtifactTestData.Target());
        Assert.Equal("aws", verified.Document.Target.Provider);
    }

    [Theory]
    [InlineData("gcp", "us-east-1", "amd64", "prod", CloudArtifactErrorCodes.TargetMismatch)]
    [InlineData("aws", "us-west-2", "amd64", "prod", CloudArtifactErrorCodes.TargetMismatch)]
    [InlineData("aws", "us-east-1", "arm64", "prod", CloudArtifactErrorCodes.TargetMismatch)]
    [InlineData("aws", "us-east-1", "amd64", "staging", CloudArtifactErrorCodes.TargetMismatch)]
    [InlineData("aws", "us-east-1", "amd64", "prod", CloudArtifactErrorCodes.ProjectMismatch)]
    public void RejectsTargetAndProjectMismatches(
        string provider,
        string region,
        string arch,
        string selector,
        string expectedCode)
    {
        var json = CloudArtifactTestData.PayloadJson(
            provider: provider,
            region: region,
            arch: arch,
            selector: $"{{\"stack\":\"{selector}\"}}");
        var target = expectedCode == CloudArtifactErrorCodes.ProjectMismatch
            ? CloudArtifactTestData.Target(project: "other")
            : CloudArtifactTestData.Target();

        AssertFails(expectedCode, () => VerifyJson(json, target: target));
    }

    [Fact]
    public void RotatesTrustWithADualSignedPayload()
    {
        var trust = CloudArtifactTestData.Trust(CloudArtifactTestData.Seed1);
        var json = CloudArtifactTestData.PayloadJson(
            trust: $"[{CloudArtifactTestData.DefaultTrustEntry(CloudArtifactTestData.Seed1)},{CloudArtifactTestData.DefaultTrustEntry(CloudArtifactTestData.Seed2)}]");

        var verified = VerifyJson(
            json,
            [CloudArtifactTestData.Seed1, CloudArtifactTestData.Seed2],
            trust: trust);

        Assert.Equal([CloudArtifactTestData.KeyId1], verified.VerifyingKeyIds);
        Assert.NotNull(verified.NextTrustSet);
        Assert.Equal(2, verified.NextTrustSet.Keys.Count);
        Assert.Equal(
            new[] { CloudArtifactTestData.KeyId1, CloudArtifactTestData.KeyId2 },
            verified.NextTrustSet.Keys.Select(k => k.KeyId).Order(StringComparer.Ordinal).ToArray());

        var nextJson = CloudArtifactTestData.PayloadJson(
            generation: 43,
            trust: $"[{CloudArtifactTestData.DefaultTrustEntry(CloudArtifactTestData.Seed2)}]");
        var next = VerifyJson(
            nextJson,
            [CloudArtifactTestData.Seed2],
            trust: verified.NextTrustSet,
            generation: new GenerationState
            {
                HighestGeneration = 42,
                HighestPayloadDigest = verified.PayloadDigest,
            });

        Assert.Equal(GenerationDecision.Accept, next.Generation);
        Assert.Equal(CloudArtifactTestData.KeyId2, Assert.Single(next.NextTrustSet!.Keys).KeyId);
    }

    [Fact]
    public void RevocationDropsAKeyOnlyWhenThePayloadIsNotSignedByIt()
    {
        var trust = CloudArtifactTestData.Trust(CloudArtifactTestData.Seed1, CloudArtifactTestData.Seed2);
        var revokeA = CloudArtifactTestData.PayloadJson(
            trust: $"[{CloudArtifactTestData.DefaultTrustEntry(CloudArtifactTestData.Seed2)}]");

        var verified = VerifyJson(
            revokeA,
            [CloudArtifactTestData.Seed2],
            trust: trust);

        Assert.Equal(CloudArtifactTestData.KeyId2, Assert.Single(verified.VerifyingKeyIds));
        Assert.Equal(CloudArtifactTestData.KeyId2, Assert.Single(verified.NextTrustSet!.Keys).KeyId);
    }

    [Fact]
    public void RejectsRevocationThatDropsAVerifyingKey()
    {
        var trust = CloudArtifactTestData.Trust(CloudArtifactTestData.Seed1, CloudArtifactTestData.Seed2);
        var json = CloudArtifactTestData.PayloadJson(
            trust: $"[{CloudArtifactTestData.DefaultTrustEntry(CloudArtifactTestData.Seed1)}]");

        AssertFails(
            CloudArtifactErrorCodes.SemanticInvalid,
            () => VerifyJson(
                json,
                [CloudArtifactTestData.Seed1, CloudArtifactTestData.Seed2],
                trust: trust));
    }

    [Fact]
    public void RejectsRotationToAnUnrelatedKey()
    {
        var trust = CloudArtifactTestData.Trust(CloudArtifactTestData.Seed1);
        var json = CloudArtifactTestData.PayloadJson(
            trust: $"[{CloudArtifactTestData.DefaultTrustEntry(CloudArtifactTestData.Seed2)}]");

        AssertFails(
            CloudArtifactErrorCodes.SemanticInvalid,
            () => VerifyJson(json, [CloudArtifactTestData.Seed1], trust: trust));
    }

    [Fact]
    public void ValidatesLayerDescriptors()
    {
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson());
        var envelope = CloudArtifactTestData.Envelope(payload);
        var context = CloudArtifactTestData.RawContext(
        [
            new ArtifactLayer(DesiredArtifactLayout.PayloadMediaType, CloudEncoding.Sha256Digest(payload), payload.Length),
            new ArtifactLayer(DesiredArtifactLayout.SignatureMediaType, CloudEncoding.Sha256Digest(envelope), envelope.Length),
        ]);

        var verified = DesiredArtifactVerifier.Verify(payload, envelope, context);
        Assert.Equal(GenerationDecision.Accept, verified.Generation);
    }

    [Fact]
    public void RejectsMissingAndDuplicateLayers()
    {
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson());
        var envelope = CloudArtifactTestData.Envelope(payload);
        var payloadLayer = new ArtifactLayer(
            DesiredArtifactLayout.PayloadMediaType,
            CloudEncoding.Sha256Digest(payload),
            payload.Length);
        var signatureLayer = new ArtifactLayer(
            DesiredArtifactLayout.SignatureMediaType,
            CloudEncoding.Sha256Digest(envelope),
            envelope.Length);

        AssertFails(
            CloudArtifactErrorCodes.LayerInvalid,
            () => DesiredArtifactVerifier.Verify(
                payload,
                envelope,
                CloudArtifactTestData.RawContext([signatureLayer])));
        AssertFails(
            CloudArtifactErrorCodes.LayerInvalid,
            () => DesiredArtifactVerifier.Verify(
                payload,
                envelope,
                CloudArtifactTestData.RawContext([payloadLayer, payloadLayer, signatureLayer])));
        AssertFails(
            CloudArtifactErrorCodes.LayerInvalid,
            () => DesiredArtifactVerifier.Verify(
                payload,
                envelope,
                CloudArtifactTestData.RawContext([payloadLayer, signatureLayer, signatureLayer])));
    }

    [Fact]
    public void RejectsLayerDescriptorByteMismatch()
    {
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson());
        var envelope = CloudArtifactTestData.Envelope(payload);
        var layers = new[]
        {
            new ArtifactLayer(DesiredArtifactLayout.PayloadMediaType, CloudEncoding.Sha256Digest(payload), payload.Length + 1),
            new ArtifactLayer(DesiredArtifactLayout.SignatureMediaType, CloudEncoding.Sha256Digest(envelope), envelope.Length),
        };

        AssertFails(
            CloudArtifactErrorCodes.DigestMismatch,
            () => DesiredArtifactVerifier.Verify(
                payload,
                envelope,
                CloudArtifactTestData.RawContext(layers)));
    }

    [Fact]
    public void RejectsMissingLayerDescriptors()
    {
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson());
        var envelope = CloudArtifactTestData.Envelope(payload);
        var context = new ArtifactVerificationContext
        {
            TrustSet = CloudArtifactTestData.Trust(CloudArtifactTestData.Seed1),
            Target = CloudArtifactTestData.Target(),
            Now = CloudArtifactTestData.Now,
            Generation = new GenerationState(),
            Layers = null!,
        };

        AssertFails(
            CloudArtifactErrorCodes.LayerInvalid,
            () => DesiredArtifactVerifier.Verify(payload, envelope, context));
    }

    [Fact]
    public void RejectsMissingGenerationState()
    {
        var payload = CloudArtifactTestData.Canonicalize(CloudArtifactTestData.PayloadJson());
        var envelope = CloudArtifactTestData.Envelope(payload);
        var valid = CloudArtifactTestData.Context(payload, envelope);
        var context = new ArtifactVerificationContext
        {
            TrustSet = valid.TrustSet,
            Target = valid.Target,
            Now = valid.Now,
            Layers = valid.Layers,
            Generation = null!,
        };

        AssertFails(
            CloudArtifactErrorCodes.ContextInvalid,
            () => DesiredArtifactVerifier.Verify(payload, envelope, context));
    }

    [Fact]
    public void ContextRequiresLayerDescriptorsAndGenerationState()
    {
        foreach (var name in new[] { nameof(ArtifactVerificationContext.Layers), nameof(ArtifactVerificationContext.Generation) })
        {
            var property = typeof(ArtifactVerificationContext).GetProperty(name);
            Assert.NotNull(property);
            Assert.True(Attribute.IsDefined(property, typeof(RequiredMemberAttribute)));
        }
    }

    [Fact]
    public void AcceptsAFreshHostWithExplicitEmptyGenerationState()
    {
        var verified = VerifyJson(
            CloudArtifactTestData.PayloadJson(),
            generation: new GenerationState { HighestGeneration = 0 });
        Assert.Equal(GenerationDecision.Accept, verified.Generation);
    }
}
