using System.Text;
using System.Text.Json;
using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class StatusArtifactTests
{
    private static TargetExpectation Target() => new()
    {
        Project = ReconcileTestData.Project,
        Provider = "aws",
        Region = "us-east-1",
        Arch = "amd64",
        Selector = new Dictionary<string, string>(StringComparer.Ordinal) { ["stack"] = "prod" },
    };

    [Fact]
    public void VerifiesASignedStatusArtifact()
    {
        var artifact = StatusTestData.Artifact();

        var verified = StatusArtifactVerifier.Verify(
            artifact.Payload,
            artifact.Signature,
            StatusTestData.Layers(artifact.Manifest),
            StatusTestData.Keys(),
            Target(),
            CloudArtifactTestData.Now,
            artifact.ManifestDigest);

        Assert.Equal(StatusTestData.HostId, verified.Document.Host.Id);
        Assert.Equal(CloudArtifactTestData.KeyId1, Assert.Single(verified.VerifyingKeyIds));
    }

    [Fact]
    public void RejectsATamperedPayload()
    {
        var artifact = StatusTestData.Artifact();
        var tampered = (byte[])artifact.Payload.Clone();
        tampered[0] ^= 0x01;

        var error = Assert.Throws<CloudArtifactException>(() => StatusArtifactVerifier.Verify(
            tampered,
            artifact.Signature,
            StatusTestData.Layers(artifact.Manifest),
            StatusTestData.Keys(),
            Target(),
            CloudArtifactTestData.Now,
            artifact.ManifestDigest));

        Assert.Equal(CloudArtifactErrorCodes.DigestMismatch, error.Code);
    }

    [Fact]
    public void RejectsATamperedSignature()
    {
        var artifact = StatusTestData.Artifact();
        var text = Encoding.UTF8.GetString(artifact.Signature);
        var marker = "\"sig\":\"";
        var index = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var mutated = text[..index] + (text[index] == 'A' ? 'B' : 'A') + text[(index + 1)..];
        var signature = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(mutated));
        var layers = new[]
        {
            new ArtifactLayer(
                StatusArtifactLayout.PayloadMediaType,
                CloudEncoding.Sha256Digest(artifact.Payload),
                artifact.Payload.Length),
            new ArtifactLayer(
                StatusArtifactLayout.SignatureMediaType,
                CloudEncoding.Sha256Digest(signature),
                signature.Length),
        };

        var error = Assert.Throws<CloudArtifactException>(() => StatusArtifactVerifier.Verify(
            artifact.Payload,
            signature,
            layers,
            StatusTestData.Keys(),
            Target(),
            CloudArtifactTestData.Now,
            artifact.ManifestDigest));

        Assert.Equal(CloudArtifactErrorCodes.SignatureInvalid, error.Code);
    }

    [Fact]
    public void RejectsAnUnknownStatusKey()
    {
        var artifact = StatusTestData.Artifact();

        var error = Assert.Throws<CloudArtifactException>(() => StatusArtifactVerifier.Verify(
            artifact.Payload,
            artifact.Signature,
            StatusTestData.Layers(artifact.Manifest),
            StatusTestData.Keys(CloudArtifactTestData.Seed2),
            Target(),
            CloudArtifactTestData.Now,
            artifact.ManifestDigest));

        Assert.Equal(CloudArtifactErrorCodes.UnknownKey, error.Code);
    }

    [Fact]
    public void RejectsAnExpiredStatusKey()
    {
        var artifact = StatusTestData.Artifact();
        var publicKey = Ed25519.PublicKeyFromSeed(CloudArtifactTestData.Seed1);
        var keys = StatusKeySet.FromKeys([new TrustedKey
        {
            KeyId = KeyId.Derive(publicKey),
            PublicKey = publicKey,
            NotAfter = CloudArtifactTestData.Now.AddMinutes(-1),
        }]);

        var error = Assert.Throws<CloudArtifactException>(() => StatusArtifactVerifier.Verify(
            artifact.Payload,
            artifact.Signature,
            StatusTestData.Layers(artifact.Manifest),
            keys,
            Target(),
            CloudArtifactTestData.Now,
            artifact.ManifestDigest));

        Assert.Equal(CloudArtifactErrorCodes.KeyExpired, error.Code);
    }

    [Fact]
    public void RejectsAHostKeyIdThatDidNotSign()
    {
        var document = StatusTestData.Document(statusKeyId: CloudArtifactTestData.KeyId2);
        var artifact = StatusTestData.Artifact(CloudArtifactTestData.Seed1, document);

        var error = Assert.Throws<CloudArtifactException>(() => StatusArtifactVerifier.Verify(
            artifact.Payload,
            artifact.Signature,
            StatusTestData.Layers(artifact.Manifest),
            StatusTestData.Keys(CloudArtifactTestData.Seed1, CloudArtifactTestData.Seed2),
            Target(),
            CloudArtifactTestData.Now,
            artifact.ManifestDigest));

        Assert.Equal(CloudArtifactErrorCodes.SignatureInvalid, error.Code);
        Assert.Contains("status_key_id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsATargetMismatch()
    {
        var artifact = StatusTestData.Artifact();
        var target = new TargetExpectation
        {
            Project = ReconcileTestData.Project,
            Provider = "aws",
            Region = "eu-west-1",
            Arch = "amd64",
            Selector = new Dictionary<string, string>(StringComparer.Ordinal) { ["stack"] = "prod" },
        };

        var error = Assert.Throws<CloudArtifactException>(() => StatusArtifactVerifier.Verify(
            artifact.Payload,
            artifact.Signature,
            StatusTestData.Layers(artifact.Manifest),
            StatusTestData.Keys(),
            target,
            CloudArtifactTestData.Now,
            artifact.ManifestDigest));

        Assert.Equal(CloudArtifactErrorCodes.TargetMismatch, error.Code);
    }

    [Fact]
    public void RejectsNonCanonicalSignedPayload()
    {
        var document = StatusTestData.Document();
        var canonical = StatusDocumentCodec.Encode(document);
        var payload = new byte[canonical.Length + 1];
        canonical.CopyTo(payload, 0);
        payload[^1] = (byte)' ';
        var signer = StatusSigningKey.FromSeed(CloudArtifactTestData.Seed1);
        var signature = signer.Sign(payload);
        var layers = new[]
        {
            new ArtifactLayer(StatusArtifactLayout.PayloadMediaType, CloudEncoding.Sha256Digest(payload), payload.Length),
            new ArtifactLayer(StatusArtifactLayout.SignatureMediaType, CloudEncoding.Sha256Digest(signature), signature.Length),
        };

        var error = Assert.Throws<CloudArtifactException>(() => StatusArtifactVerifier.Verify(
            payload,
            signature,
            layers,
            StatusTestData.Keys(),
            Target(),
            CloudArtifactTestData.Now,
            StatusTestData.Digest('d')));

        Assert.Equal(CloudArtifactErrorCodes.NonCanonical, error.Code);
    }

    [Fact]
    public void PrimaryManifestUsesArtifactTypeAndEmptyConfig()
    {
        var manifest = StatusManifest.Build(
            StatusTestData.Digest('a'),
            10,
            StatusTestData.Digest('b'),
            20,
            184,
            fallback: false);

        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        Assert.Equal(StatusManifest.ManifestMediaType, root.GetProperty("mediaType").GetString());
        Assert.Equal(StatusManifest.ArtifactType, root.GetProperty("artifactType").GetString());
        Assert.Equal(StatusManifest.EmptyConfigMediaType, root.GetProperty("config").GetProperty("mediaType").GetString());
        Assert.Equal(
            "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
            root.GetProperty("config").GetProperty("digest").GetString());
        Assert.Equal(2, root.GetProperty("config").GetProperty("size").GetInt64());
        Assert.Equal(
            CloudEncoding.Sha256Digest("{}"u8.ToArray()),
            root.GetProperty("config").GetProperty("digest").GetString());
        Assert.Equal("184", root.GetProperty("annotations").GetProperty(StatusManifest.StatusSeqAnnotation).GetString());
        var layers = root.GetProperty("layers");
        Assert.Equal(2, layers.GetArrayLength());
        Assert.Equal(StatusArtifactLayout.PayloadMediaType, layers[0].GetProperty("mediaType").GetString());
        Assert.Equal(StatusArtifactLayout.SignatureMediaType, layers[1].GetProperty("mediaType").GetString());
    }

    [Fact]
    public void FallbackManifestOmitsArtifactTypeAndUsesImageConfig()
    {
        var manifest = StatusManifest.Build(
            StatusTestData.Digest('a'),
            10,
            StatusTestData.Digest('b'),
            20,
            184,
            fallback: true);

        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("artifactType", out _));
        Assert.Equal(StatusManifest.ImageConfigMediaType, root.GetProperty("config").GetProperty("mediaType").GetString());
    }

    [Fact]
    public void ManifestBytesAreReproducible()
    {
        var first = StatusManifest.Build(
            StatusTestData.Digest('a'),
            10,
            StatusTestData.Digest('b'),
            20,
            184,
            fallback: false);
        var second = StatusManifest.Build(
            StatusTestData.Digest('a'),
            10,
            StatusTestData.Digest('b'),
            20,
            184,
            fallback: false);

        Assert.Equal(first, second);
        Assert.Equal(
            "sha256:de40aa62f8df3ac516f692bf36933262828b8dfb8ee87c6e6be1ad7ce994757b",
            CloudEncoding.Sha256Digest(first));
    }
}
