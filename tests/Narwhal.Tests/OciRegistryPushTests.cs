using System.Net;
using System.Text;
using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class OciRegistryPushTests
{
    private const string Repository = "shop/state/target/status";

    [Fact]
    public async Task UploadsMissingBlobsWithTheTwoStepFlow()
    {
        var blob = "{\"status\":true}"u8.ToArray();
        var digest = CloudEncoding.Sha256Digest(blob);
        var methods = new List<string>();
        var uploaded = Array.Empty<byte>();

        var handler = new FakeHttpHandler(request =>
        {
            methods.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            if (request.Method == HttpMethod.Head)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Location = new Uri("/v2/shop/state/target/status/blobs/uploads/session", UriKind.Relative);
                return response;
            }

            uploaded = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Insecure = true },
            handler: handler);

        await client.PushBlobAsync(Repository, blob, CancellationToken.None);

        Assert.Equal(3, methods.Count);
        Assert.StartsWith("HEAD /v2/" + Repository + "/blobs/", methods[0], StringComparison.Ordinal);
        Assert.StartsWith("POST /v2/" + Repository + "/blobs/uploads/", methods[1], StringComparison.Ordinal);
        Assert.StartsWith("PUT /v2/" + Repository + "/blobs/uploads/session?digest=", methods[2], StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(digest), methods[2], StringComparison.Ordinal);
        Assert.Equal(blob, uploaded);
    }

    [Fact]
    public async Task SkipsAnExistingBlob()
    {
        var handler = new FakeHttpHandler(request =>
            request.Method == HttpMethod.Head
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : throw new InvalidOperationException("unexpected upload"));

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Insecure = true },
            handler: handler);

        await client.PushBlobAsync(Repository, "{}"u8.ToArray(), CancellationToken.None);
    }

    [Fact]
    public async Task RejectsA201UploadStartWithoutADigestPost()
    {
        var handler = new FakeHttpHandler(request => request.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.Created));

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Insecure = true },
            handler: handler);

        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.PushBlobAsync(Repository, "{}"u8.ToArray(), CancellationToken.None));
        Assert.Contains("upload session", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsARedirectOnAnUploadWithoutFollowingIt()
    {
        var postRequests = 0;
        var handler = new FakeHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            postRequests++;
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("/upload/elsewhere", UriKind.Relative);
            return redirect;
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Insecure = true },
            handler: handler);

        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.PushBlobAsync(Repository, "{}"u8.ToArray(), CancellationToken.None));
        Assert.Contains("redirect rejected", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, postRequests);
    }

    [Fact]
    public async Task RejectsARedirectOnAManifestPutWithoutFollowingIt()
    {
        var puts = 0;
        var handler = new FakeHttpHandler(request =>
        {
            puts++;
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("/manifests/elsewhere", UriKind.Relative);
            return redirect;
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Insecure = true },
            handler: handler);

        var manifest = StatusManifest.Build(
            CloudEncoding.Sha256Digest([1]),
            1,
            CloudEncoding.Sha256Digest([2]),
            1,
            1,
            fallback: false);
        var error = await Assert.ThrowsAsync<RegistryException>(() => client.PushManifestAsync(
            Repository,
            "latest",
            manifest,
            StatusManifest.ManifestMediaType,
            CancellationToken.None));

        Assert.Contains("redirect rejected", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, puts);
    }

    [Fact]
    public async Task TreatsARedirectedHeadAuthFailureAsAnUnknownBlob()
    {
        var posts = 0;
        var puts = 0;
        var handler = new FakeHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Head && request.RequestUri!.Host == "registry.test")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new Uri("https://storage.test/blob", UriKind.Absolute);
                return redirect;
            }

            if (request.Method == HttpMethod.Head)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Location = new Uri("/upload/session", UriKind.Relative);
                return response;
            }

            puts++;
            var completed = new HttpResponseMessage(HttpStatusCode.Created);
            completed.Headers.TryAddWithoutValidation(
                "Docker-Content-Digest",
                CloudEncoding.Sha256Digest("{}"u8.ToArray()));
            return completed;
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Insecure = true },
            handler: handler);

        await client.PushBlobAsync(Repository, "{}"u8.ToArray(), CancellationToken.None);

        Assert.Equal(1, posts);
        Assert.Equal(1, puts);
    }

    [Fact]
    public async Task VerifiesTheClosingUploadDigestHeader()
    {
        var handler = new FakeHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Location = new Uri("/upload/session", UriKind.Relative);
                return response;
            }

            var completed = new HttpResponseMessage(HttpStatusCode.Created);
            completed.Headers.TryAddWithoutValidation("Docker-Content-Digest", Digest('f'));
            return completed;
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Insecure = true },
            handler: handler);

        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.PushBlobAsync(Repository, "{}"u8.ToArray(), CancellationToken.None));
        Assert.Contains("digest header", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushesAManifestAndReturnsItsDigest()
    {
        var manifest = "{\"schemaVersion\":2}"u8.ToArray();
        var expected = CloudEncoding.Sha256Digest(manifest);
        var handler = new FakeHttpHandler(request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/v2/" + Repository + "/manifests/latest", request.RequestUri!.PathAndQuery);
            Assert.Equal(StatusManifest.ManifestMediaType, request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(manifest, request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            var response = new HttpResponseMessage(HttpStatusCode.Created);
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", expected);
            return response;
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Insecure = true },
            handler: handler);

        var result = await client.PushManifestAsync(
            Repository,
            "latest",
            manifest,
            StatusManifest.ManifestMediaType,
            CancellationToken.None);

        Assert.Equal(expected, result.Digest);
        Assert.Equal("latest", result.Reference);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task ReportsARejectedManifestShape(HttpStatusCode status)
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(status));
        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Insecure = true },
            handler: handler);

        var error = await Assert.ThrowsAsync<RegistryManifestRejectedException>(() => client.PushManifestAsync(
            Repository,
            "latest",
            "{\"schemaVersion\":2}"u8.ToArray(),
            StatusManifest.ManifestMediaType,
            CancellationToken.None));

        Assert.Equal((int)status, error.StatusCode);
    }

    [Fact]
    public async Task RejectsAnUploadLocationOnAnotherOrigin()
    {
        var handler = new FakeHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Location = new Uri("https://attacker.test/upload", UriKind.Absolute);
                return response;
            }

            throw new InvalidOperationException("unexpected follow-up");
        });

        using var client = new OciRegistryClient(
            "registry.test",
            new RegistryAuthConfig { Insecure = true },
            handler: handler);

        var error = await Assert.ThrowsAsync<RegistryException>(
            () => client.PushBlobAsync(Repository, "{}"u8.ToArray(), CancellationToken.None));
        Assert.Contains("registry origin", error.Message, StringComparison.Ordinal);
    }

    private static string Digest(char value) => "sha256:" + new string(value, 64);
}
