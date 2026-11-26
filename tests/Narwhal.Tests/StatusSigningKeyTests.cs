using System.Text;
using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class StatusSigningKeyTests
{
    [Fact]
    public void LoadsARaw32ByteSeed()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var path = Path.Combine(directory, "status-key");
        File.WriteAllBytes(path, CloudArtifactTestData.Seed1);
        SetPrivate(path);

        var key = StatusSigningKey.Load(path);

        Assert.Equal(CloudArtifactTestData.KeyId1, key.KeyId);
        Assert.Equal(CloudArtifactTestData.PublicKey1, key.PublicKey);
    }

    [Theory]
    [InlineData("hex")]
    [InlineData("base64")]
    public void LoadsTextSeedForms(string form)
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var path = Path.Combine(directory, "status-key");
        var text = form == "hex"
            ? CloudArtifactTestData.Key1SeedHex
            : Convert.ToBase64String(CloudArtifactTestData.Seed1);
        File.WriteAllText(path, text + "\n");
        SetPrivate(path);

        var key = StatusSigningKey.Load(path);

        Assert.Equal(CloudArtifactTestData.KeyId1, key.KeyId);
    }

    [Fact]
    public void RejectsAWorldReadableKey()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = ReconcileTestData.CreateStateDirectory();
        var path = Path.Combine(directory, "status-key");
        File.WriteAllBytes(path, CloudArtifactTestData.Seed1);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var error = Assert.Throws<AgentConfigException>(() => StatusSigningKey.Load(path));
        Assert.Contains("0600", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAShortSeed()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var path = Path.Combine(directory, "status-key");
        File.WriteAllBytes(path, new byte[31]);
        SetPrivate(path);

        Assert.Throws<AgentConfigException>(() => StatusSigningKey.Load(path));
    }

    [Fact]
    public void SignsTheStatusDomainSeparator()
    {
        var key = StatusSigningKey.FromSeed(CloudArtifactTestData.Seed1);
        var document = StatusTestData.Document();
        var payload = StatusDocumentCodec.Encode(document);
        var envelope = key.Sign(payload);

        var parsed = SignatureEnvelope.Parse(envelope);
        Assert.Equal(CloudEncoding.Sha256Digest(payload), parsed.PayloadDigest);
        var signature = Assert.Single(parsed.Signatures);
        Assert.Equal(key.KeyId, signature.KeyId);

        var message = new byte[StatusSigningKey.DomainSeparator.Length + payload.Length];
        Encoding.ASCII.GetBytes(StatusSigningKey.DomainSeparator, message);
        payload.CopyTo(message.AsSpan(StatusSigningKey.DomainSeparator.Length));

        Assert.True(Ed25519.Verify(key.PublicKey, message, signature.Signature));
    }

    [Fact]
    public void SignedStatusArtifactVerifies()
    {
        var key = StatusSigningKey.FromSeed(CloudArtifactTestData.Seed1);
        var document = StatusTestData.Document();
        var payload = StatusDocumentCodec.Encode(document);
        var signature = key.Sign(payload);
        var manifest = StatusManifest.Build(
            CloudEncoding.Sha256Digest(payload),
            payload.Length,
            CloudEncoding.Sha256Digest(signature),
            signature.Length,
            document.StatusSeq,
            fallback: false);

        var target = new TargetExpectation
        {
            Project = ReconcileTestData.Project,
            Provider = "aws",
            Region = "us-east-1",
            Arch = "amd64",
            Selector = new Dictionary<string, string>(StringComparer.Ordinal) { ["stack"] = "prod" },
        };

        var verified = StatusArtifactVerifier.Verify(
            payload,
            signature,
            StatusTestData.Layers(manifest),
            StatusKeySet.FromKeys([new TrustedKey { KeyId = key.KeyId, PublicKey = key.PublicKey }]),
            target,
            CloudArtifactTestData.Now,
            CloudEncoding.Sha256Digest(manifest));

        Assert.Equal(key.KeyId, Assert.Single(verified.VerifyingKeyIds));
    }

    private static void SetPrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
