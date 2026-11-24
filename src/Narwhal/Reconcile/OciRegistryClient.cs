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

/// <summary>A registry transport, authentication, or protocol error.</summary>
public sealed class RegistryException(string message, bool auth = false) : Exception(message)
{
    public bool Auth { get; } = auth;
}

/// <summary>Minimal OCI Distribution client with basic and bearer-token authentication.</summary>
public sealed class OciRegistryClient : IRegistryClient, IDisposable
{
    private const string ManifestMediaType = "application/vnd.oci.image.manifest.v1+json";
    private const string DockerManifestMediaType = "application/vnd.docker.distribution.manifest.v2+json";

    private readonly HttpClient _http;
    private readonly string _baseAddress;
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
        _auth = auth;
        _reporter = reporter;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public static OciRegistryClient Create(AgentConfig config, Reporter reporter, HttpMessageHandler? handler = null) =>
        new(config.StateRegistry ?? config.ImageRegistry, config.Auth, reporter, handler);

    public async Task<RegistryManifest> GetManifestAsync(string repository, string reference, CancellationToken cancellationToken)
    {
        var path = $"/v2/{repository}/manifests/{reference}";
        using var response = await SendWithAuthAsync(HttpMethod.Get, path, repository, "pull", acceptManifest: true, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var digest = CloudEncoding.Sha256Digest(bytes);

        var header = response.Headers.TryGetValues("Docker-Content-Digest", out var values)
            ? values.FirstOrDefault()
            : null;
        if (header is not null && !string.Equals(header, digest, StringComparison.Ordinal))
            throw new RegistryException($"manifest digest header does not match the manifest bytes for {repository}:{reference}");

        if (CloudPatterns.Digest().IsMatch(reference) && !string.Equals(reference, digest, StringComparison.Ordinal))
            throw new RegistryException($"manifest bytes do not match the pinned digest for {repository}:{reference}");

        var layers = OciManifestParser.Parse(bytes, repository, reference);
        var selection = DesiredArtifactLayout.Select(layers);
        return new RegistryManifest(digest, bytes, layers, selection);
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
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var actual = CloudEncoding.Sha256Digest(bytes);
        if (!string.Equals(actual, digest, StringComparison.Ordinal))
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.DigestMismatch,
                $"blob {digest} in {repository} hashed to {actual}");
        return bytes;
    }

    private async Task<HttpResponseMessage> SendWithAuthAsync(
        HttpMethod method,
        string path,
        string repository,
        string actions,
        bool acceptManifest,
        CancellationToken cancellationToken)
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
            using var request = new HttpRequestMessage(method, _baseAddress + path);
            if (acceptManifest)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ManifestMediaType));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(DockerManifestMediaType));
            }

            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            else if (basic is not null)
            {
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basic.Username}:{basic.Secret}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
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

            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            {
                if (!response.IsSuccessStatusCode)
                {
                    using (response)
                    {
                        throw new RegistryException(
                            $"registry returned {(int)response.StatusCode} for {path}",
                            auth: response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
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
        var credential = await ResolveCredentialAsync(cancellationToken);
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

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new RegistryException("registry token request failed", auth: true);

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var token = root.TryGetProperty("token", out var direct)
            ? direct.GetString()
            : root.TryGetProperty("access_token", out var access) ? access.GetString() : null;
        if (string.IsNullOrEmpty(token))
            throw new RegistryException("registry token response did not contain a token", auth: true);

        var lifetime = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? Math.Max(1, seconds)
            : 60;
        _reporter?.Detail($"registry token acquired for {requestedScope}");
        _tokens[scope] = new CachedToken(token, DateTimeOffset.UtcNow.AddSeconds(lifetime * 0.8));
        return token;
    }

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
