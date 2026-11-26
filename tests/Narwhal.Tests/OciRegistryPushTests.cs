using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

/// <summary>Push behavior of the oras-backed registry client.</summary>
[Collection("oras-subprocess")]
public class OciRegistryPushTests
{
    private const string Repository = "shop/state/target/status";

    [Fact]
    public async Task PushesABlobWithItsDigestAndFile()
    {
        var blob = "{\"status\":true}"u8.ToArray();
        var digest = CloudEncoding.Sha256Digest(blob);
        using var oras = new FakeOras();
        oras.Descriptor(digest, blob.Length);
        oras.CaptureLastArgument();

        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true });
        await client.PushBlobAsync(Repository, blob, CancellationToken.None);

        var args = oras.FirstArguments;
        Assert.Equal(8, args.Length);
        Assert.Equal(["blob", "push", "--no-tty", "--plain-http"], args[..4]);
        Assert.Equal("--registry-config", args[4]);
        Assert.Equal("registry.test/" + Repository + "@" + digest, args[6]);
        Assert.EndsWith(".bin", args[7], StringComparison.Ordinal);
        Assert.Equal(blob, File.ReadAllBytes(oras.CapturePath));
    }

    [Fact]
    public async Task OmitsPlainHttpWhenTheRegistryIsNotInsecure()
    {
        using var oras = new FakeOras();
        var blob = "{}"u8.ToArray();
        oras.Descriptor(CloudEncoding.Sha256Digest(blob), blob.Length);
        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig());
        await client.PushBlobAsync(Repository, blob, CancellationToken.None);

        Assert.DoesNotContain("--plain-http", oras.Arguments);
    }

    [Fact]
    public async Task PushesAManifestWithItsMediaTypeAndReturnsItsDigest()
    {
        var manifest = "{\"schemaVersion\":2}"u8.ToArray();
        using var oras = new FakeOras();
        oras.CaptureLastArgument();

        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true });
        var result = await client.PushManifestAsync(
            Repository,
            "latest",
            manifest,
            StatusManifest.ManifestMediaType,
            CancellationToken.None);

        var args = oras.FirstArguments;
        Assert.Equal(9, args.Length);
        Assert.Equal(["manifest", "push", "--media-type", StatusManifest.ManifestMediaType, "--plain-http"], args[..5]);
        Assert.Equal("--registry-config", args[5]);
        Assert.Equal("registry.test/" + Repository + ":latest", args[7]);
        Assert.EndsWith(".json", args[8], StringComparison.Ordinal);
        Assert.Equal(manifest, File.ReadAllBytes(oras.CapturePath));
        Assert.Equal(CloudEncoding.Sha256Digest(manifest), result.Digest);
        Assert.Equal("latest", result.Reference);
    }

    [Fact]
    public async Task PushesAManifestByDigestReference()
    {
        var manifest = "{\"schemaVersion\":2}"u8.ToArray();
        var digest = CloudEncoding.Sha256Digest(manifest);
        using var oras = new FakeOras();

        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true });
        await client.PushManifestAsync(
            Repository,
            digest,
            manifest,
            StatusManifest.ManifestMediaType,
            CancellationToken.None);

        Assert.Equal("registry.test/" + Repository + "@" + digest, oras.FirstArguments[^2]);
    }

    [Fact]
    public async Task MapsAnUnauthorizedBlobPushToAnAuthFailure()
    {
        using var oras = new FakeOras();
        oras.Fail(1, "Error response from registry: unauthorized: authentication required");

        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.PushBlobAsync(Repository, "{}"u8.ToArray(), CancellationToken.None));

        Assert.True(error.Auth);
    }

    [Fact]
    public async Task MapsAManifestRejectionToTheFallbackException()
    {
        using var oras = new FakeOras();
        oras.Fail(1, "Error response from registry: manifest invalid: manifest invalid: map[]");

        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryManifestRejectedException>(() => client.PushManifestAsync(
            Repository,
            "latest",
            "{\"schemaVersion\":2}"u8.ToArray(),
            StatusManifest.ManifestMediaType,
            CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(415)]
    [InlineData(422)]
    public async Task MapsParsedManifestRejectionStatuses(int status)
    {
        using var oras = new FakeOras();
        oras.Fail(1, $"Error response from registry: unexpected response status code {status}");

        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryManifestRejectedException>(() => client.PushManifestAsync(
            Repository,
            "latest",
            "{\"schemaVersion\":2}"u8.ToArray(),
            StatusManifest.ManifestMediaType,
            CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task TreatsManifestUnverifiedAsARejection()
    {
        using var oras = new FakeOras();
        oras.Fail(1, "Error response from registry: manifest unverified");

        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryManifestRejectedException>(() => client.PushManifestAsync(
            Repository,
            "latest",
            "{\"schemaVersion\":2}"u8.ToArray(),
            StatusManifest.ManifestMediaType,
            CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task FailsClosedWhenThePushedBlobDigestDoesNotMatch()
    {
        var blob = "{}"u8.ToArray();
        using var oras = new FakeOras();
        oras.Output([]);
        oras.Descriptor("sha256:" + new string('f', 64), blob.Length);

        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.PushBlobAsync(Repository, blob, CancellationToken.None));

        Assert.Contains("expected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailsClosedWhenTheRegistryReportsNoBlobDescriptor()
    {
        using var oras = new FakeOras();
        oras.Output([]);

        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.PushBlobAsync(Repository, "{}"u8.ToArray(), CancellationToken.None));

        Assert.Contains("did not report a descriptor", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapsAnUnsupportedMediaTypeTo415()
    {
        using var oras = new FakeOras();
        oras.Fail(1, "Error response from registry: response status code 415: unsupported");

        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryManifestRejectedException>(() => client.PushManifestAsync(
            Repository,
            "latest",
            "{\"schemaVersion\":2}"u8.ToArray(),
            StatusManifest.ManifestMediaType,
            CancellationToken.None));

        Assert.Equal(415, error.StatusCode);
    }

    [Fact]
    public async Task DoesNotTreatAMissingRepositoryAsAManifestRejection()
    {
        using var oras = new FakeOras();
        oras.Fail(1, "Error response from registry: name unknown: not found");

        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true });
        var error = await Assert.ThrowsAsync<RegistryException>(() => client.PushManifestAsync(
            Repository,
            "latest",
            "{\"schemaVersion\":2}"u8.ToArray(),
            StatusManifest.ManifestMediaType,
            CancellationToken.None));

        Assert.Equal(404, error.StatusCode);
        Assert.False(error.Auth);
    }
}
