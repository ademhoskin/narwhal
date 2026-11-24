using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class OciRegistryClientTests
{
    private const string Repository = "shop/state/target/desired";

    [Fact]
    public async Task ResolvesAManifestAndVerifiesItsDigest()
    {
        var artifact = ReconcileTestData.Artifact(CloudArtifactTestData.PayloadJson());
        var handler = new FakeHttpHandler(request =>
        {
            Assert.Equal("http", request.RequestUri!.Scheme);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(artifact.Manifest.Bytes),
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", artifact.Manifest.Digest);
            return response;
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Insecure = true },
            handler: handler);
        var manifest = await client.GetManifestAsync(Repository, "desired", CancellationToken.None);

        Assert.Equal(artifact.Manifest.Digest, manifest.Digest);
        Assert.Equal(artifact.Manifest.Selection, manifest.Selection);
    }

    [Fact]
    public async Task FollowsTheBearerTokenChallenge()
    {
        var artifact = ReconcileTestData.Artifact(CloudArtifactTestData.PayloadJson());
        var tokenRequests = 0;
        var manifestRequests = 0;
        var handler = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                tokenRequests++;
                Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
                return Json(new HttpResponseMessage(HttpStatusCode.OK), """{"token":"abc","expires_in":300}""");
            }

            manifestRequests++;
            if (manifestRequests == 1)
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Bearer",
                    "realm=\"https://auth.test/token\",service=\"registry.test\",scope=\"repository:shop/state/target/desired:pull\""));
                return challenge;
            }

            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("abc", request.Headers.Authorization!.Parameter);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(artifact.Manifest.Bytes),
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", artifact.Manifest.Digest);
            return response;
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig
            {
                Insecure = true,
                Username = "agent",
                Password = "password",
            },
            handler: handler);

        var manifest = await client.GetManifestAsync(Repository, "desired", CancellationToken.None);

        Assert.Equal(artifact.Manifest.Digest, manifest.Digest);
        Assert.Equal(1, tokenRequests);
        Assert.Equal(2, manifestRequests);
    }

    [Theory]
    [InlineData("http://registry.test/token", "must use HTTPS")]
    [InlineData("https://agent:password@registry.test/token", "must not carry userinfo")]
    [InlineData("https://registry.test/token#fragment", "must not carry userinfo or a fragment")]
    public async Task RejectsUnsafeBearerRealms(string realm, string message)
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                "Bearer",
                $"realm=\"{realm}\",service=\"registry.test\""));
            return challenge;
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Username = "agent", Password = "password" },
            handler: handler);

        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, "desired", CancellationToken.None));
        Assert.True(error.Auth);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendsPreemptiveBasicCredentials()
    {
        var artifact = ReconcileTestData.Artifact(CloudArtifactTestData.PayloadJson());
        var handler = new FakeHttpHandler(request =>
        {
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(artifact.Manifest.Bytes),
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", artifact.Manifest.Digest);
            return response;
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Insecure = true, Username = "agent", Password = "password" },
            handler: handler);

        await client.GetManifestAsync(Repository, "desired", CancellationToken.None);
    }

    [Fact]
    public async Task RejectsAZeroOrDuplicatePayloadLayer()
    {
        var empty = "{\"schemaVersion\":2,\"layers\":[]}"u8.ToArray();
        var duplicate = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":2,\"layers\":[" +
            "{\"mediaType\":\"application/vnd.narwhal.desired.v1+json\",\"digest\":\"" + Digest('a') + "\",\"size\":1}," +
            "{\"mediaType\":\"application/vnd.narwhal.desired.v1+json\",\"digest\":\"" + Digest('b') + "\",\"size\":1}," +
            "{\"mediaType\":\"application/vnd.narwhal.signature.v1+json\",\"digest\":\"" + Digest('c') + "\",\"size\":1}]}");

        await AssertLayerFailure(empty);
        await AssertLayerFailure(duplicate);
    }

    [Fact]
    public async Task RejectsAManifestHeaderDigestMismatch()
    {
        var artifact = ReconcileTestData.Artifact(CloudArtifactTestData.PayloadJson());
        var handler = new FakeHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(artifact.Manifest.Bytes),
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", Digest('f'));
            return response;
        });

        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true }, handler: handler);
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, "desired", CancellationToken.None));
        Assert.Contains("digest header", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsPinnedBytesThatDoNotMatchTheDigest()
    {
        var artifact = ReconcileTestData.Artifact(CloudArtifactTestData.PayloadJson());
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(artifact.Manifest.Bytes),
        });

        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true }, handler: handler);
        await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, Digest('a'), CancellationToken.None));
    }

    [Fact]
    public async Task RejectsABlobWhoseBytesDoNotMatchItsDigest()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("not-the-blob"u8.ToArray()),
        });

        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true }, handler: handler);
        var error = await Assert.ThrowsAsync<CloudArtifactException>(
            () => client.GetBlobAsync(Repository, Digest('a'), CancellationToken.None));
        Assert.Equal(CloudArtifactErrorCodes.DigestMismatch, error.Code);
    }

    [Fact]
    public async Task ReportsAuthenticationFailures()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true }, handler: handler);
        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, "desired", CancellationToken.None));
        Assert.True(error.Auth);
    }

    private static async Task AssertLayerFailure(byte[] manifest)
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(manifest),
        });
        using var client = new OciRegistryClient("registry.test", new RegistryAuthConfig { Insecure = true }, handler: handler);
        var error = await Assert.ThrowsAsync<CloudArtifactException>(
            () => client.GetManifestAsync(Repository, "desired", CancellationToken.None));
        Assert.Equal(CloudArtifactErrorCodes.LayerInvalid, error.Code);
    }

    private static HttpResponseMessage Json(HttpResponseMessage response, string json)
    {
        response.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return response;
    }

    private static string Digest(char value) => "sha256:" + new string(value, 64);
}
