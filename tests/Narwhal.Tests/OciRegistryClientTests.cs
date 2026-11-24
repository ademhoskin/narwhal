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

    [Fact]
    public async Task WrapsAnUnreachableTokenEndpoint()
    {
        var handler = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
                throw new HttpRequestException("token host is unreachable");
            var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                "Bearer",
                "realm=\"https://auth.test/token\",service=\"registry.test\""));
            return challenge;
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Username = "agent", Password = "password" },
            handler: handler);

        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, "desired", CancellationToken.None));
        Assert.True(error.Auth);
        Assert.Contains("token request failed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrapsANonJsonTokenResponse()
    {
        var handler = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
                return Json(new HttpResponseMessage(HttpStatusCode.OK), "not json");
            var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                "Bearer",
                "realm=\"https://auth.test/token\",service=\"registry.test\""));
            return challenge;
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Username = "agent", Password = "password" },
            handler: handler);

        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, "desired", CancellationToken.None));
        Assert.True(error.Auth);
        Assert.Contains("not JSON", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"token":123}""")]
    public async Task WrapsAMalformedTokenShape(string body)
    {
        var handler = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
                return Json(new HttpResponseMessage(HttpStatusCode.OK), body);
            var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                "Bearer",
                "realm=\"https://auth.test/token\",service=\"registry.test\""));
            return challenge;
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Username = "agent", Password = "password" },
            handler: handler);

        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, "desired", CancellationToken.None));
        Assert.True(error.Auth);
        Assert.Contains("invalid shape", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsTooManyRedirects()
    {
        var requests = 0;
        var handler = new FakeHttpHandler(request =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(request.RequestUri!.PathAndQuery, UriKind.Relative);
            return response;
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Insecure = true, Username = "agent", Password = "password" },
            handler: handler);

        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, "desired", CancellationToken.None));
        Assert.Contains("redirect limit exceeded", error.Message, StringComparison.Ordinal);
        Assert.Equal(6, requests);
    }

    [Fact]
    public void DefaultHandlerDoesNotFollowRedirectsAutomatically()
    {
        using var handler = OciRegistryClient.CreateDefaultHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task FollowsABlobRedirectWithoutSendingCredentialsAcrossOrigins()
    {
        var body = "redirected payload"u8.ToArray();
        var digest = CloudEncoding.Sha256Digest(body);
        HttpRequestMessage? redirected = null;
        var handler = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.Host == "registry.test")
            {
                Assert.NotNull(request.Headers.Authorization);
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new Uri("https://storage.test/blob", UriKind.Absolute);
                return redirect;
            }

            redirected = request;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Username = "agent", Password = "password" },
            handler: handler);

        var bytes = await client.GetBlobAsync(Repository, digest, CancellationToken.None);

        Assert.Equal(body, bytes);
        Assert.NotNull(redirected);
        Assert.Equal("storage.test", redirected!.RequestUri!.Host);
        Assert.Null(redirected.Headers.Authorization);
    }

    [Fact]
    public async Task RetainsCredentialsOnASameOriginRedirect()
    {
        var body = "same origin payload"u8.ToArray();
        var requests = 0;
        var handler = new FakeHttpHandler(request =>
        {
            requests++;
            Assert.Equal("registry.test", request.RequestUri!.Host);
            Assert.NotNull(request.Headers.Authorization);
            if (requests == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new Uri("https://registry.test/blob", UriKind.Absolute);
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Username = "agent", Password = "password" },
            handler: handler);

        var bytes = await client.GetBlobAsync(Repository, CloudEncoding.Sha256Digest(body), CancellationToken.None);

        Assert.Equal(body, bytes);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task PullsAVerifiedDesiredArtifactThroughRedirectedBlobs()
    {
        var artifact = ReconcileTestData.Artifact(CloudArtifactTestData.PayloadJson());
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [CloudEncoding.Sha256Digest(artifact.Payload)] = artifact.Payload,
            [CloudEncoding.Sha256Digest(artifact.Signature)] = artifact.Signature,
        };
        var redirects = 0;
        var handler = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.Host == "registry.test" &&
                request.RequestUri.AbsolutePath.Contains("/manifests/", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(artifact.Manifest.Bytes),
                };
                response.Headers.TryAddWithoutValidation("Docker-Content-Digest", artifact.Manifest.Digest);
                return response;
            }

            if (request.RequestUri.Host == "registry.test")
            {
                redirects++;
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new Uri(
                    "https://storage.test" + request.RequestUri.PathAndQuery,
                    UriKind.Absolute);
                return redirect;
            }

            var blobDigest = request.RequestUri.AbsolutePath[(request.RequestUri.AbsolutePath.LastIndexOf('/') + 1)..];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(blobs[blobDigest]) };
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Username = "agent", Password = "password" },
            handler: handler);

        var manifest = await client.GetManifestAsync(Repository, "desired", CancellationToken.None);
        var payload = await client.GetBlobAsync(Repository, manifest.Selection.Payload.Digest, CancellationToken.None);
        var signature = await client.GetBlobAsync(Repository, manifest.Selection.Signature.Digest, CancellationToken.None);
        var expected = CloudArtifactTestData.Context(payload, signature);
        var verified = DesiredArtifactVerifier.Verify(payload, signature, new ArtifactVerificationContext
        {
            TrustSet = expected.TrustSet,
            Target = expected.Target,
            Now = expected.Now,
            Generation = expected.Generation,
            Layers = manifest.Layers,
        });

        Assert.Equal(2, redirects);
        Assert.Equal(artifact.PayloadDigest, verified.PayloadDigest);
    }

    [Fact]
    public async Task RejectsATokenRedirectWithoutFollowingIt()
    {
        var tokenRequests = 0;
        var handler = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                tokenRequests++;
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://attacker.test/token", UriKind.Absolute);
                return redirect;
            }

            var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                "Bearer",
                "realm=\"https://auth.test/token\",service=\"registry.test\""));
            return challenge;
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Username = "agent", Password = "password" },
            handler: handler);

        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.GetManifestAsync(Repository, "desired", CancellationToken.None));
        Assert.True(error.Auth);
        Assert.Contains("token redirect", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, tokenRequests);
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
