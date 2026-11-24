using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Narwhal.Cli;
using Narwhal.Cloud;

namespace Narwhal.Reconcile;

/// <summary>A resolved OCI artifact manifest and its selected payload/signature layers.</summary>
public sealed record RegistryManifest(
    string Digest,
    byte[] Bytes,
    IReadOnlyList<ArtifactLayer> Layers,
    SelectedArtifactLayers Selection);

public interface IRegistryClient
{
    Task<RegistryManifest> GetManifestAsync(string repository, string reference, CancellationToken cancellationToken);
    Task<byte[]> GetBlobAsync(string repository, string digest, CancellationToken cancellationToken);
}

/// <summary>The result of pushing an OCI manifest.</summary>
public sealed record RegistryPushResult(string Digest, string Reference);

/// <summary>A registry transport, authentication, or protocol error.</summary>
public sealed class RegistryException(string message, bool auth = false, int? statusCode = null) : Exception(message)
{
    public bool Auth { get; } = auth;
    public int? StatusCode { get; } = statusCode;
}

/// <summary>The registry rejected a manifest shape, so the documented fallback may be attempted.</summary>
public sealed class RegistryManifestRejectedException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Minimal OCI Distribution client with basic and bearer-token authentication.</summary>
public sealed class OciRegistryClient : IRegistryClient, IStatusRegistry, IDisposable
{
    private const string ManifestMediaType = "application/vnd.oci.image.manifest.v1+json";
    private const string DockerManifestMediaType = "application/vnd.docker.distribution.manifest.v2+json";
    private const int MaxRedirects = 5;
    public const long MaximumBlobBytes = 256L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _baseAddress;
    private readonly Uri _baseUri;
    private readonly RegistryAuthConfig _auth;
    private readonly Reporter? _reporter;
    private readonly Dictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);
    private RegistryCredential? _credential;

    public OciRegistryClient(
        string registry,
        RegistryAuthConfig auth,
        Reporter? reporter = null,
        HttpMessageHandler? handler = null)
    {
        var scheme = auth.Insecure ? "http" : "https";
        _baseAddress = $"{scheme}://{registry}";
        _baseUri = new Uri(_baseAddress);
        _auth = auth;
        _reporter = reporter;
        _http = handler is null
            ? new HttpClient(CreateDefaultHandler())
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    internal static HttpClientHandler CreateDefaultHandler() =>
        new() { AllowAutoRedirect = false };

    public static OciRegistryClient Create(AgentConfig config, Reporter reporter, HttpMessageHandler? handler = null) =>
        new(config.StateRegistry ?? config.ImageRegistry, config.Auth, reporter, handler);

    public async Task<RegistryManifest> GetManifestAsync(string repository, string reference, CancellationToken cancellationToken)
    {
        var (digest, bytes) = await FetchManifestBytesAsync(repository, reference, cancellationToken);
        var layers = OciManifestParser.Parse(bytes, repository, reference);
        return new RegistryManifest(digest, bytes, layers, DesiredArtifactLayout.Select(layers));
    }

    /// <summary>Fetches the status artifact manifest, selecting the status payload and signature layers.</summary>
    public async Task<RegistryManifest> GetStatusManifestAsync(string repository, string reference, CancellationToken cancellationToken)
    {
        var (digest, bytes) = await FetchManifestBytesAsync(repository, reference, cancellationToken);
        var layers = OciManifestParser.Parse(bytes, repository, reference);
        return new RegistryManifest(digest, bytes, layers, StatusArtifactLayout.Select(layers));
    }

    private async Task<(string Digest, byte[] Bytes)> FetchManifestBytesAsync(
        string repository,
        string reference,
        CancellationToken cancellationToken)
    {
        var path = $"/v2/{repository}/manifests/{reference}";
        using var response = await SendWithAuthAsync(HttpMethod.Get, path, repository, "pull", acceptManifest: true, cancellationToken);
        var bytes = await ReadBodyAsync(response, path, cancellationToken);
        var digest = CloudEncoding.Sha256Digest(bytes);

        var header = response.Headers.TryGetValues("Docker-Content-Digest", out var values)
            ? values.FirstOrDefault()
            : null;
        if (header is not null && !string.Equals(header, digest, StringComparison.Ordinal))
            throw new RegistryException($"manifest digest header does not match the manifest bytes for {repository}:{reference}");

        if (CloudPatterns.Digest().IsMatch(reference) && !string.Equals(reference, digest, StringComparison.Ordinal))
            throw new RegistryException($"manifest bytes do not match the pinned digest for {repository}:{reference}");

        return (digest, bytes);
    }

    public async Task<byte[]> GetBlobAsync(string repository, string digest, CancellationToken cancellationToken)
    {
        using var response = await SendWithAuthAsync(
            HttpMethod.Get,
            $"/v2/{repository}/blobs/{digest}",
            repository,
            "pull",
            acceptManifest: false,
            cancellationToken);
        var bytes = await ReadBodyCappedAsync(
            response,
            $"/v2/{repository}/blobs/{digest}",
            MaximumBlobBytes,
            cancellationToken);
        var actual = CloudEncoding.Sha256Digest(bytes);
        if (!string.Equals(actual, digest, StringComparison.Ordinal))
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.DigestMismatch,
                $"blob {digest} in {repository} hashed to {actual}");
        return bytes;
    }

    public async Task<bool> BlobExistsAsync(string repository, string digest, CancellationToken cancellationToken)
    {
        using var response = await SendWithAuthAsync(
            HttpMethod.Head,
            $"/v2/{repository}/blobs/{digest}",
            repository,
            "pull",
            acceptManifest: false,
            cancellationToken,
            isExpectedFailure: static response => response.StatusCode is
                HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Uploads a blob when it is absent, using the OCI two-step upload flow.</summary>
    public async Task PushBlobAsync(string repository, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var digest = CloudEncoding.Sha256Digest(bytes.Span);
        if (await BlobExistsAsync(repository, digest, cancellationToken))
            return;

        using var start = await SendWithAuthAsync(
            HttpMethod.Post,
            $"/v2/{repository}/blobs/uploads/",
            repository,
            "pull,push",
            acceptManifest: false,
            cancellationToken);
        if (start.StatusCode != HttpStatusCode.Accepted)
            throw new RegistryException(
                $"registry did not start an upload session for {repository} (status {(int)start.StatusCode})");

        var location = start.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(location))
            throw new RegistryException($"registry did not return an upload location for {repository}");

        var uploadPath = ResolveUploadLocation(location);
        uploadPath += (uploadPath.Contains('?') ? "&" : "?") +
                      "digest=" + Uri.EscapeDataString(digest);

        using var completed = await SendWithAuthAsync(
            HttpMethod.Put,
            uploadPath,
            repository,
            "pull,push",
            acceptManifest: false,
            cancellationToken,
            body: bytes.ToArray(),
            contentType: "application/octet-stream");

        var header = completed.Headers.TryGetValues("Docker-Content-Digest", out var values)
            ? values.FirstOrDefault()
            : null;
        if (header is not null && !string.Equals(header, digest, StringComparison.Ordinal))
            throw new RegistryException($"uploaded blob digest header does not match {digest}");
    }

    /// <summary>Pushes a manifest by tag or digest. Rejection returns <see cref="RegistryManifestRejectedException"/>.</summary>
    public async Task<RegistryPushResult> PushManifestAsync(
        string repository,
        string reference,
        byte[] manifest,
        string mediaType,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithAuthAsync(
            HttpMethod.Put,
            $"/v2/{repository}/manifests/{reference}",
            repository,
            "pull,push",
            acceptManifest: false,
            cancellationToken,
            body: manifest,
            contentType: mediaType,
            isExpectedFailure: static response => (int)response.StatusCode is 400 or 415 or 422);

        if (!response.IsSuccessStatusCode)
        {
            throw new RegistryManifestRejectedException(
                (int)response.StatusCode,
                $"registry rejected the manifest for {repository}:{reference} with status {(int)response.StatusCode}");
        }

        var digest = CloudEncoding.Sha256Digest(manifest);
        var header = response.Headers.TryGetValues("Docker-Content-Digest", out var values)
            ? values.FirstOrDefault()
            : null;
        if (header is not null && !string.Equals(header, digest, StringComparison.Ordinal))
            throw new RegistryException($"manifest digest header does not match the manifest bytes for {repository}:{reference}");

        return new RegistryPushResult(digest, reference);
    }

    private string ResolveUploadLocation(string location)
    {
        if (location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var absolute = new Uri(location, UriKind.Absolute);
            var baseUri = new Uri(_baseAddress);
            if (!string.Equals(absolute.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(absolute.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
                absolute.Port != baseUri.Port)
            {
                throw new RegistryException("registry upload location must stay on the registry origin");
            }

            return absolute.PathAndQuery;
        }

        if (location.StartsWith("//", StringComparison.Ordinal))
            throw new RegistryException("registry upload location must stay on the registry origin");

        return location.StartsWith('/') ? location : "/" + location;
    }

    private async Task<HttpResponseMessage> SendWithAuthAsync(
        HttpMethod method,
        string path,
        string repository,
        string actions,
        bool acceptManifest,
        CancellationToken cancellationToken,
        byte[]? body = null,
        string? contentType = null,
        Func<HttpResponseMessage, bool>? isExpectedFailure = null)
    {
        var scope = $"repository:{repository}:{actions}";
        var token = CurrentToken(scope);
        RegistryCredential? basic = null;
        if (token is null)
        {
            if (!string.IsNullOrEmpty(_auth.Token))
                token = _auth.Token;
            else
                basic = await ResolveCredentialAsync(cancellationToken);
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (response, onBaseOrigin) = await SendFollowingRedirectsAsync(
                method,
                new Uri(_baseAddress + path),
                path,
                acceptManifest,
                token,
                basic,
                body,
                contentType,
                cancellationToken);

            if (!onBaseOrigin)
            {
                if (isExpectedFailure is not null && isExpectedFailure(response))
                    return response;
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    var statusCode = (int)response.StatusCode;
                    response.Dispose();
                    throw new RegistryException(
                        "redirected registry request was rejected",
                        auth: true,
                        statusCode: statusCode);
                }

                if (!response.IsSuccessStatusCode)
                {
                    using (response)
                    {
                        throw new RegistryException(
                            $"registry returned {(int)response.StatusCode} for {path}",
                            statusCode: (int)response.StatusCode);
                    }
                }

                return response;
            }

            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            {
                if (isExpectedFailure is not null && isExpectedFailure(response))
                    return response;

                if (!response.IsSuccessStatusCode)
                {
                    using (response)
                    {
                        throw new RegistryException(
                            $"registry returned {(int)response.StatusCode} for {path}",
                            auth: response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                            statusCode: (int)response.StatusCode);
                    }
                }

                return response;
            }

            var challenge = BearerChallenge.TryParse(response.Headers.WwwAuthenticate);
            response.Dispose();
            if (attempt == 1 || challenge is null)
            {
                throw new RegistryException(
                    $"registry rejected credentials for {repository}",
                    auth: true);
            }

            token = await FetchTokenAsync(challenge, scope, cancellationToken);
        }

        throw new RegistryException($"registry rejected credentials for {repository}", auth: true);
    }

    private async Task<(HttpResponseMessage Response, bool OnBaseOrigin)> SendFollowingRedirectsAsync(
        HttpMethod method,
        Uri initial,
        string path,
        bool acceptManifest,
        string? token,
        RegistryCredential? basic,
        byte[]? body,
        string? contentType,
        CancellationToken cancellationToken)
    {
        var uri = initial;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(method, uri);
            if (acceptManifest)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ManifestMediaType));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(DockerManifestMediaType));
            }

            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                if (contentType is not null)
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            }

            if (IsBaseOrigin(uri))
            {
                if (token is not null)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                else if (basic is not null)
                {
                    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basic.Username}:{basic.Secret}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                throw new RegistryException($"registry request failed: {e.Message}");
            }

            if (!IsRedirect(response.StatusCode))
                return (response, IsBaseOrigin(uri));

            if (method != HttpMethod.Get && method != HttpMethod.Head)
            {
                response.Dispose();
                throw new RegistryException($"registry redirect rejected for {path}");
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new RegistryException($"registry redirect has no location for {uri}");
            if (redirects == MaxRedirects)
                throw new RegistryException($"registry redirect limit exceeded for {path}");

            var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
            ValidateRedirect(uri, next);
            uri = next;
        }
    }

    private bool IsBaseOrigin(Uri uri) =>
        string.Equals(uri.Scheme, _baseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == _baseUri.Port;

    private static void ValidateRedirect(Uri from, Uri to)
    {
        if (to.Scheme != Uri.UriSchemeHttp && to.Scheme != Uri.UriSchemeHttps)
            throw new RegistryException($"registry redirect uses an unsupported scheme: {to.Scheme}");
        if (from.Scheme == Uri.UriSchemeHttps && to.Scheme == Uri.UriSchemeHttp)
            throw new RegistryException("registry redirect must not downgrade HTTPS");
        if (to.UserInfo.Length > 0 || to.Fragment.Length > 0)
            throw new RegistryException("registry redirect must not carry userinfo or a fragment");
    }

    private string? CurrentToken(string scope)
    {
        if (_tokens.TryGetValue(scope, out var cached) && DateTimeOffset.UtcNow < cached.RefreshAt)
            return cached.Token;
        return null;
    }

    private async Task<string> FetchTokenAsync(BearerChallenge challenge, string scope, CancellationToken cancellationToken)
    {
        Uri realm;
        try
        {
            realm = new Uri(challenge.Realm, UriKind.Absolute);
        }
        catch (UriFormatException)
        {
            throw new RegistryException("registry bearer realm is not an absolute URI", auth: true);
        }

        ValidateRealm(realm);
        RegistryCredential? credential;
        try
        {
            credential = await ResolveCredentialAsync(cancellationToken);
        }
        catch (RegistryException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or
                                  JsonException or FormatException or InvalidOperationException)
        {
            throw new RegistryException($"registry credential resolution failed: {e.Message}", auth: true);
        }

        var service = challenge.Service ?? "";
        var requestedScope = challenge.Scope ?? scope;
        var url = challenge.Realm +
                  (challenge.Realm.Contains('?', StringComparison.Ordinal) ? "&" : "?") +
                  "service=" + Uri.EscapeDataString(service) +
                  "&scope=" + Uri.EscapeDataString(requestedScope);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (credential is not null)
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Secret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException or IOException) &&
                                  !cancellationToken.IsCancellationRequested)
        {
            throw new RegistryException($"registry token request failed: {e.Message}", auth: true);
        }

        using (response)
        {
            if (IsRedirect(response.StatusCode))
                throw new RegistryException("registry token redirect rejected", auth: true);
            if (!response.IsSuccessStatusCode)
                throw new RegistryException("registry token request failed", auth: true);

            byte[] bytes;
            try
            {
                bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }
            catch (Exception e) when ((e is HttpRequestException or TaskCanceledException or IOException) &&
                                      !cancellationToken.IsCancellationRequested)
            {
                throw new RegistryException($"cannot read the registry token response: {e.Message}", auth: true);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(bytes);
            }
            catch (JsonException)
            {
                throw new RegistryException("registry token response is not JSON", auth: true);
            }

            using (document)
            {
                var root = document.RootElement;
                string? token;
                int lifetime;
                try
                {
                    token = root.TryGetProperty("token", out var direct)
                        ? direct.GetString()
                        : root.TryGetProperty("access_token", out var access) ? access.GetString() : null;
                    lifetime = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
                        ? Math.Max(1, seconds)
                        : 60;
                }
                catch (InvalidOperationException)
                {
                    throw new RegistryException("registry token response has an invalid shape", auth: true);
                }

                if (string.IsNullOrEmpty(token))
                    throw new RegistryException("registry token response did not contain a token", auth: true);

                _reporter?.Detail($"registry token acquired for {requestedScope}");
                _tokens[scope] = new CachedToken(token, DateTimeOffset.UtcNow.AddSeconds(lifetime * 0.8));
                return token;
            }
        }
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpResponseMessage response,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException or IOException) &&
                                  !cancellationToken.IsCancellationRequested)
        {
            throw new RegistryException($"cannot read the registry response for {path}: {e.Message}");
        }
    }

    private static async Task<byte[]> ReadBodyCappedAsync(
        HttpResponseMessage response,
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new RegistryException($"blob at {path} exceeds the {maximumBytes}-byte limit");

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
                throw new RegistryException($"blob at {path} exceeds the {maximumBytes}-byte limit");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is 301 or 302 or 303 or 307 or 308;

    private void ValidateRealm(Uri realm)
    {
        if (realm.UserInfo.Length > 0 || realm.Fragment.Length > 0)
            throw new RegistryException("registry bearer realm must not carry userinfo or a fragment", auth: true);
        if (realm.Scheme != Uri.UriSchemeHttps && !(realm.Scheme == Uri.UriSchemeHttp && _auth.Insecure))
            throw new RegistryException("registry bearer realm must use HTTPS", auth: true);
    }

    private async Task<RegistryCredential?> ResolveCredentialAsync(CancellationToken cancellationToken)
    {
        return _credential ??= await RegistryCredentialsResolver.ResolveAsync(_auth, _baseAddress, cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    private sealed record CachedToken(string Token, DateTimeOffset RefreshAt);
}

internal static class OciManifestParser
{
    public static IReadOnlyList<ArtifactLayer> Parse(byte[] bytes, string repository, string reference)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException e)
        {
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.LayerInvalid,
                $"manifest for {repository}:{reference} is not JSON: {e.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Invalid("manifest is not an object");
            if (!root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion != 2)
                throw Invalid("manifest schemaVersion must be 2");
            if (!root.TryGetProperty("layers", out var layersElement) || layersElement.ValueKind != JsonValueKind.Array)
                throw Invalid("manifest has no layers array");

            var layers = new List<ArtifactLayer>();
            foreach (var layer in layersElement.EnumerateArray())
            {
                if (layer.ValueKind != JsonValueKind.Object)
                    throw Invalid("manifest layer is not an object");
                var mediaType = String(layer, "mediaType");
                var digest = String(layer, "digest");
                if (!CloudPatterns.Digest().IsMatch(digest))
                    throw Invalid($"manifest layer digest is invalid: {digest}");
                if (!layer.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size) || size < 0)
                    throw Invalid("manifest layer size is invalid");
                layers.Add(new ArtifactLayer(mediaType, digest, size));
            }

            return layers;
        }

        static string String(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : throw Invalid($"manifest layer is missing '{name}'");

        static CloudArtifactException Invalid(string message) =>
            new(CloudArtifactErrorCodes.LayerInvalid, message);
    }
}

internal sealed record RegistryCredential(string Username, string Secret);

internal static class RegistryCredentialsResolver
{
    public static async Task<RegistryCredential?> ResolveAsync(
        RegistryAuthConfig auth,
        string registryUrl,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(auth.Username) && auth.Password is not null)
            return new RegistryCredential(auth.Username, auth.Password);
        if (!string.IsNullOrEmpty(auth.Token))
            return new RegistryCredential("token", auth.Token);
        if (!string.IsNullOrEmpty(auth.CredentialHelper))
            return await CredentialHelper.ResolveAsync(auth.CredentialHelper, registryUrl, cancellationToken);
        return null;
    }

    public static string BasicToken(string username, string secret) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{secret}"));
}

internal sealed class BearerChallenge
{
    public required string Realm { get; init; }
    public string? Service { get; init; }
    public string? Scope { get; init; }

    public static BearerChallenge? TryParse(IEnumerable<AuthenticationHeaderValue> headers)
    {
        foreach (var header in headers)
        {
            if (!string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(header.Parameter))
                continue;

            var fields = ParseFields(header.Parameter);
            if (!fields.TryGetValue("realm", out var realm) || string.IsNullOrEmpty(realm))
                continue;

            return new BearerChallenge
            {
                Realm = realm,
                Service = fields.GetValueOrDefault("service"),
                Scope = fields.GetValueOrDefault("scope"),
            };
        }

        return null;
    }

    private static Dictionary<string, string> ParseFields(string value)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < value.Length)
        {
            while (index < value.Length && (value[index] == ',' || char.IsWhiteSpace(value[index])))
                index++;
            var equals = value.IndexOf('=', index);
            if (equals < 0)
                break;
            var name = value[index..equals].Trim();
            index = equals + 1;
            if (index < value.Length && value[index] == '"')
            {
                index++;
                var end = value.IndexOf('"', index);
                if (end < 0)
                    break;
                fields[name] = value[index..end];
                index = end + 1;
            }
            else
            {
                var end = value.IndexOf(',', index);
                if (end < 0)
                    end = value.Length;
                fields[name] = value[index..end].Trim();
                index = end;
            }
        }

        return fields;
    }
}

internal static class CredentialHelper
{
    public static async Task<RegistryCredential?> ResolveAsync(string helper, string registry, CancellationToken cancellationToken)
    {
        var executable = helper.Contains('/') ? helper : "docker-credential-" + helper;
        try
        {
            var startInfo = new ProcessStartInfo(executable, "get")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(startInfo);
            if (process is null)
                throw new RegistryException($"credential helper '{helper}' could not be started", auth: true);

            await process.StandardInput.WriteAsync((registry + "\n").AsMemory(), cancellationToken);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new RegistryException($"credential helper '{helper}' failed with exit {process.ExitCode}", auth: true);

            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var username = root.TryGetProperty("Username", out var user) ? user.GetString() : null;
            var secret = root.TryGetProperty("Secret", out var value) ? value.GetString() : null;
            if (string.IsNullOrEmpty(username) || secret is null)
                throw new RegistryException($"credential helper '{helper}' returned no credentials", auth: true);
            return new RegistryCredential(username, secret);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or JsonException or FormatException)
        {
            throw new RegistryException($"credential helper '{helper}' failed: {e.Message}", auth: true);
        }
    }
}
