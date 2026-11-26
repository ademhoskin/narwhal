using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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

/// <summary>OCI Distribution client backed by the <c>oras</c> CLI, invoked without a shell.</summary>
public sealed class OciRegistryClient : IRegistryClient, IStatusRegistry, IDisposable
{
    public const long MaximumBlobBytes = 256L * 1024 * 1024;
    public const long MaximumManifestBytes = 64L * 1024 * 1024;

    private const string ExecutableVariable = "NARWHAL_ORAS";
    private const int MaximumCapturedChars = 8 * 1024;
    private const int MaximumCommandOutputChars = 256 * 1024;
    // Every operation runs one or more oras processes, and a status publish runs
    // several, so the per-command timeout is deliberately short; the aggregate
    // sequence is bounded by the number of blobs plus the manifest.
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(60);
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const uint AnyExecuteBit = 0b001_001_001;
    private const uint DirectoryType = 0x4000;

    /// <summary>Credentials the agent reads for itself; the child never needs them.</summary>
    private static readonly string[] StrippedEnvironment =
    [
        "NARWHAL_REGISTRY_USERNAME",
        "NARWHAL_REGISTRY_PASSWORD",
        "NARWHAL_REGISTRY_TOKEN",
        "NARWHAL_REGISTRY_CREDENTIAL_HELPER",
        "NARWHAL_REGISTRY_INSECURE",
        "NARWHAL_IMAGE_REGISTRY_USERNAME",
        "NARWHAL_IMAGE_REGISTRY_PASSWORD",
        "NARWHAL_IMAGE_REGISTRY_TOKEN",
        "NARWHAL_IMAGE_REGISTRY_CREDENTIAL_HELPER",
        "NARWHAL_IMAGE_REGISTRY_INSECURE",
        "DOCKER_AUTH_CONFIG",
    ];

    private readonly string _registry;
    private readonly RegistryAuthConfig _auth;
    private readonly ProcessRunner _runner;
    private readonly TimeSpan _commandTimeout;
    private readonly Lazy<string> _workDirectory;
    private readonly Lazy<string> _authFile;
    private readonly string[] _secrets;
    private string? _credentialHelperSuffix;

    public OciRegistryClient(string registry, RegistryAuthConfig auth, Reporter? reporter = null, bool verbose = false)
        : this(registry, auth, reporter, verbose, commandTimeout: null)
    {
    }

    internal OciRegistryClient(
        string registry,
        RegistryAuthConfig auth,
        Reporter? reporter,
        bool verbose,
        TimeSpan? commandTimeout)
    {
        if (string.IsNullOrWhiteSpace(registry))
            throw new ArgumentException("registry must not be empty", nameof(registry));

        _registry = registry.TrimEnd('/');
        _auth = auth;
        _runner = new ProcessRunner(reporter ?? SilentReporter(), verbose);
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
        _workDirectory = new Lazy<string>(CreateWorkDirectory);
        _authFile = new Lazy<string>(CreateAuthFile);
        _secrets = new[] { auth.Password, auth.Token }
            .Where(secret => !string.IsNullOrEmpty(secret))
            .Select(secret => secret!)
            .ToArray();
    }

    public static OciRegistryClient Create(AgentConfig config, Reporter reporter, bool verbose = false) =>
        new(config.StateRegistry ?? config.ImageRegistry, config.Auth, reporter, verbose);

    public Task<RegistryManifest> GetManifestAsync(
        string repository,
        string reference,
        CancellationToken cancellationToken) =>
        FetchManifestAsync(repository, reference, DesiredArtifactLayout.Select, cancellationToken);

    /// <summary>Fetches the status artifact manifest, selecting the status payload and signature layers.</summary>
    public Task<RegistryManifest> GetStatusManifestAsync(
        string repository,
        string reference,
        CancellationToken cancellationToken) =>
        FetchManifestAsync(repository, reference, StatusArtifactLayout.Select, cancellationToken);

    private async Task<RegistryManifest> FetchManifestAsync(
        string repository,
        string reference,
        Func<IReadOnlyList<ArtifactLayer>, SelectedArtifactLayers> select,
        CancellationToken cancellationToken)
    {
        var path = TempPath("manifest-", ".json");
        var target = Reference(repository, reference);
        byte[] bytes;
        string? reportedDigest;
        try
        {
            var result = await RunAsync(
                ["manifest", "fetch", "--output", path, "--descriptor"],
                target,
                stream: false,
                cancellationToken);

            if (!result.Ok)
                ThrowFor(result, "fetch manifest", target, manifestPush: false);

            bytes = await ReadFileAsync(path, MaximumManifestBytes, "the fetched manifest", cancellationToken);
            reportedDigest = DescriptorDigest(result);
        }
        finally
        {
            DeleteQuietly(path);
        }

        var digest = CloudEncoding.Sha256Digest(bytes);
        if (reportedDigest is not null && !string.Equals(reportedDigest, digest, StringComparison.Ordinal))
            throw new RegistryException(
                $"manifest digest reported by the registry does not match the manifest bytes for {repository}:{reference}");

        if (CloudPatterns.Digest().IsMatch(reference) && !string.Equals(reference, digest, StringComparison.Ordinal))
            throw new RegistryException($"manifest bytes do not match the pinned digest for {repository}:{reference}");

        var layers = OciManifestParser.Parse(bytes, repository, reference);
        return new RegistryManifest(digest, bytes, layers, select(layers));
    }

    public async Task<byte[]> GetBlobAsync(string repository, string digest, CancellationToken cancellationToken)
    {
        var path = TempPath("blob-", ".bin");
        byte[] bytes;
        try
        {
            var target = Reference(repository, digest);
            var result = await RunAsync(
                ["blob", "fetch", "--no-tty", "--output", path],
                target,
                stream: false,
                cancellationToken);

            if (!result.Ok)
                ThrowFor(result, "fetch blob", target, manifestPush: false);

            bytes = await ReadFileAsync(path, MaximumBlobBytes, $"blob {digest}", cancellationToken);
        }
        finally
        {
            DeleteQuietly(path);
        }

        var actual = CloudEncoding.Sha256Digest(bytes);
        if (!string.Equals(actual, digest, StringComparison.Ordinal))
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.DigestMismatch,
                $"blob {digest} in {repository} hashed to {actual}");
        return bytes;
    }

    public async Task<bool> BlobExistsAsync(string repository, string digest, CancellationToken cancellationToken)
    {
        var target = Reference(repository, digest);
        var result = await RunAsync(
            ["blob", "fetch", "--descriptor", "--no-tty"],
            target,
            stream: false,
            cancellationToken);

        if (result.Ok)
            return true;

        var message = result.StdErr.Length > 0 ? result.StdErr : result.StdOut;
        if (LooksLikeAuthFailure(message) || LooksLikeNotFound(message))
            return false;

        ThrowFor(result, "probe blob", target, manifestPush: false);
        return false;
    }

    /// <summary>Uploads a blob. oras skips the upload when the registry already has it.</summary>
    public async Task PushBlobAsync(string repository, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var digest = CloudEncoding.Sha256Digest(bytes.Span);
        var path = WriteTempFile("blob-", ".bin", bytes.Span);
        try
        {
            var target = Reference(repository, digest);
            var result = await RunAsync(
                ["blob", "push", "--no-tty"],
                target,
                stream: true,
                cancellationToken,
                trailing: path);

            if (!result.Ok)
                ThrowFor(result, "push blob", target, manifestPush: false);

            await VerifyPushedBlobAsync(target, digest, cancellationToken);
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    /// <summary>Pushes a manifest by tag or digest. Rejection returns <see cref="RegistryManifestRejectedException"/>.</summary>
    public async Task<RegistryPushResult> PushManifestAsync(
        string repository,
        string reference,
        byte[] manifest,
        string mediaType,
        CancellationToken cancellationToken)
    {
        var path = WriteTempFile("manifest-push-", ".json", manifest);
        try
        {
            var target = Reference(repository, reference);
            var result = await RunAsync(
                ["manifest", "push", "--media-type", mediaType],
                target,
                stream: true,
                cancellationToken,
                trailing: path);

            if (!result.Ok)
                ThrowFor(result, "push manifest", target, manifestPush: true);
        }
        finally
        {
            DeleteQuietly(path);
        }

        return new RegistryPushResult(CloudEncoding.Sha256Digest(manifest), reference);
    }

    private string Reference(string repository, string reference) =>
        CloudPatterns.Digest().IsMatch(reference)
            ? $"{_registry}/{repository}@{reference}"
            : $"{_registry}/{repository}:{reference}";

    private async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> head,
        string target,
        bool stream,
        CancellationToken cancellationToken,
        string? trailing = null)
    {
        var args = BuildArguments(head, target, trailing);
        try
        {
            return await _runner.RunAsync(
                Executable,
                args,
                new ProcessOptions
                {
                    Input = Stdin,
                    Stream = stream,
                    Timeout = _commandTimeout,
                    Environment = ChildEnvironment,
                    RemoveEnvironment = StrippedEnvironment,
                    MaxOutputChars = MaximumCommandOutputChars,
                },
                cancellationToken);
        }
        catch (ToolNotFoundException e)
        {
            throw new RegistryException(e.Message);
        }
        catch (ProcessTimeoutException)
        {
            throw new RegistryException($"oras {head[0]} {head[1]} {target} timed out after {_commandTimeout.TotalSeconds:0}s");
        }
    }

    private List<string> BuildArguments(IReadOnlyList<string> head, string target, string? trailing)
    {
        var args = new List<string>(head.Count + 10);
        args.AddRange(head);
        if (_auth.Insecure)
            args.Add("--plain-http");

        // Never let oras fall back to the user's Docker config, even with no auth.
        args.Add("--registry-config");
        args.Add(AuthFile);

        if (string.IsNullOrEmpty(_auth.Token) &&
            !string.IsNullOrEmpty(_auth.Username) &&
            _auth.Password is not null)
        {
            args.Add("-u");
            args.Add(_auth.Username);
            args.Add("--password-stdin");
        }

        args.Add(target);
        if (trailing is not null)
            args.Add(trailing);
        return args;
    }

    private string? Stdin
    {
        get
        {
            if (!string.IsNullOrEmpty(_auth.Token))
                return null;
            if (!string.IsNullOrEmpty(_auth.Username) && _auth.Password is not null)
                return _auth.Password;
            return null;
        }
    }

    private string AuthFile => _authFile.Value;

    internal static string Executable =>
        Environment.GetEnvironmentVariable(ExecutableVariable) ?? "oras";

    private void ThrowFor(ProcessResult result, string action, string target, bool manifestPush)
    {
        var text = Redact(Bound(result.StdErr.Length > 0 ? result.StdErr : result.StdOut));
        var status = ParseStatusCode(text);

        if (status is 401 or 403 || LooksLikeAuthFailure(text))
            throw new RegistryException(
                $"oras {action} {target} failed: {text}",
                auth: true,
                statusCode: status ?? 401);

        if (manifestPush && (status is 400 or 415 or 422 || LooksLikeManifestRejection(text)))
            throw new RegistryManifestRejectedException(
                status ?? 400,
                $"registry rejected the manifest for {target}: {text}");

        if (status == 404 || LooksLikeNotFound(text))
            throw new RegistryException(
                $"oras {action} {target} failed: {text}",
                statusCode: status ?? 404);

        throw new RegistryException(
            $"oras {action} {target} failed with exit {result.ExitCode}: {text}",
            statusCode: status);
    }

    private static bool LooksLikeAuthFailure(string text) =>
        Contains(text, "unauthorized") ||
        Contains(text, "authentication required") ||
        Contains(text, "denied");

    private static bool LooksLikeNotFound(string text) =>
        Contains(text, "not found") ||
        Contains(text, "name unknown") ||
        Contains(text, "manifest unknown") ||
        Contains(text, "blob unknown");

    private static bool LooksLikeManifestRejection(string text) =>
        Contains(text, "manifest invalid") ||
        Contains(text, "manifest unverified") ||
        Contains(text, "manifest blob unknown") ||
        Contains(text, "unsupported") ||
        Contains(text, "size invalid") ||
        Contains(text, "digest invalid");

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static int? ParseStatusCode(string text)
    {
        const string marker = "status code ";
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var start = index + marker.Length;
            if (start + 3 <= text.Length &&
                int.TryParse(text.AsSpan(start, 3), out var status) &&
                status is >= 400 and <= 599)
                return status;
            index = text.IndexOf(marker, start, StringComparison.OrdinalIgnoreCase);
        }

        return null;
    }

    private string Bound(string text) =>
        text.Length <= MaximumCapturedChars
            ? text
            : text[..MaximumCapturedChars] + "\n...(output truncated)";

    private string Redact(string text)
    {
        foreach (var secret in _secrets)
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        return text;
    }

    private string CreateWorkDirectory()
    {
        var name = "oras-" + Guid.NewGuid().ToString("N");
        foreach (var (parent, leaf) in WorkDirectoryCandidates(name))
        {
            try
            {
                if (leaf == name)
                {
                    CreatePrivateDirectory(parent);
                    if (!IsPrivateDirectory(parent))
                        continue;
                }

                var candidate = Path.Combine(parent, leaf);
                CreatePrivateDirectory(candidate);
                if (!IsPrivateDirectory(candidate))
                    continue;

                CreatePrivateDirectory(Path.Combine(candidate, "docker"));
                return candidate;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Try the next runtime directory.
            }
        }

        throw new RegistryException("cannot create a private directory for oras");
    }

    private static IEnumerable<(string Parent, string Leaf)> WorkDirectoryCandidates(string name)
    {
        if (OperatingSystem.IsLinux())
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(runtime))
                yield return (Path.Combine(runtime, "narwhal"), name);

            yield return (Path.Combine("/run/user/" + GetEuid().ToString(CultureInfo.InvariantCulture), "narwhal"), name);
        }

        // Unique leaf under the shared temp directory: never chmod the parent.
        yield return (Path.GetTempPath(), "narwhal-" + name);
    }

    private static bool IsPrivateDirectory(string path)
    {
        if (!OperatingSystem.IsLinux())
            return true;

        try
        {
            var metadata = PosixFile.StatPath(path);
            return (metadata.Mode & PosixFile.TypeMask) == DirectoryType &&
                   !metadata.IsGroupOrOtherWritable &&
                   metadata.Uid == GetEuid();
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, PrivateDirectoryMode);
        File.SetUnixFileMode(path, PrivateDirectoryMode);
    }

    private string CreateAuthFile()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (!string.IsNullOrEmpty(_auth.Token))
            {
                writer.WriteStartObject("auths");
                writer.WriteStartObject(_registry);
                writer.WriteString("registrytoken", _auth.Token);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            else if (!string.IsNullOrEmpty(_auth.CredentialHelper))
            {
                writer.WriteStartObject("credHelpers");
                writer.WriteString(_registry, CredentialHelperSuffix(_auth.CredentialHelper));
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        // An empty object still blocks the ambient Docker config (H2).
        var path = Path.Combine(_workDirectory.Value, "auth.json");
        WritePrivateFile(path, buffer.ToArray());
        return path;
    }

    /// <summary>oras only understands helper names, so an absolute helper is exposed as a PATH shim.</summary>
    private string CredentialHelperSuffix(string helper)
    {
        if (!helper.Contains('/') && !helper.Contains('\\'))
            return helper;

        var target = ValidateCredentialHelper(Path.GetFullPath(helper));
        var suffix = "narwhal";
        var link = Path.Combine(_workDirectory.Value, "docker-credential-" + suffix);
        try
        {
            File.Delete(link);
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RegistryException($"credential helper '{helper}' could not be wired up: {e.Message}", auth: true);
        }

        _credentialHelperSuffix = suffix;
        return suffix;
    }

    private static string ValidateCredentialHelper(string path)
    {
        if (!File.Exists(path))
            throw new RegistryException($"credential helper '{path}' does not exist", auth: true);

        string target;
        try
        {
            target = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RegistryException($"credential helper '{path}' could not be resolved: {e.Message}", auth: true);
        }

        if (!File.Exists(target))
            throw new RegistryException($"credential helper '{path}' does not exist", auth: true);

        if (!OperatingSystem.IsLinux())
            return target;

        PosixFile.Metadata metadata;
        try
        {
            metadata = PosixFile.StatPath(target);
        }
        catch (IOException e)
        {
            throw new RegistryException($"credential helper '{path}' could not be inspected: {e.Message}", auth: true);
        }

        if (!metadata.IsRegular)
            throw new RegistryException($"credential helper '{path}' is not a regular file", auth: true);
        if ((metadata.Mode & AnyExecuteBit) == 0)
            throw new RegistryException($"credential helper '{path}' is not executable", auth: true);
        if (metadata.IsGroupOrOtherWritable)
            throw new RegistryException($"credential helper '{path}' is writable by group or others", auth: true);
        var euid = GetEuid();
        if (metadata.Uid != euid && metadata.Uid != 0)
            throw new RegistryException($"credential helper '{path}' is not owned by root or the current user", auth: true);

        return target;
    }

    private IReadOnlyDictionary<string, string> ChildEnvironment
    {
        get
        {
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DOCKER_CONFIG"] = Path.Combine(_workDirectory.Value, "docker"),
            };

            if (_credentialHelperSuffix is not null)
            {
                var path = Environment.GetEnvironmentVariable("PATH");
                environment["PATH"] = string.IsNullOrEmpty(path)
                    ? _workDirectory.Value
                    : _workDirectory.Value + ":" + path;
            }

            return environment;
        }
    }

    private async Task VerifyPushedBlobAsync(string target, string digest, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            ["blob", "fetch", "--descriptor", "--no-tty"],
            target,
            stream: false,
            cancellationToken);

        if (!result.Ok)
            ThrowFor(result, "verify blob", target, manifestPush: false);

        var reported = DescriptorDigest(result);
        if (reported is null)
            throw new RegistryException($"oras did not report a descriptor for pushed blob {target}");
        if (!string.Equals(reported, digest, StringComparison.Ordinal))
            throw new RegistryException(
                $"registry reported digest {reported} for pushed blob {target}, expected {digest}");
    }

    private static string? DescriptorDigest(ProcessResult result)
    {
        var text = result.StdOut.Trim();
        if (text.Length == 0)
            return null;

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("digest", out var digest) &&
                   digest.ValueKind == JsonValueKind.String
                ? digest.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<byte[]> ReadFileAsync(
        string path,
        long limit,
        string description,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new RegistryException($"oras did not write {description}");
        if (info.Length > limit)
            throw new RegistryException($"{description} exceeds the {limit}-byte limit");

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.LongLength > limit)
            throw new RegistryException($"{description} exceeds the {limit}-byte limit");
        return bytes;
    }

    private string TempPath(string prefix, string extension) =>
        Path.Combine(_workDirectory.Value, prefix + Guid.NewGuid().ToString("N") + extension);

    private string WriteTempFile(string prefix, string extension, ReadOnlySpan<byte> bytes)
    {
        var path = TempPath(prefix, extension);
        WritePrivateFile(path, bytes);
        return path;
    }

    private static void WritePrivateFile(string path, ReadOnlySpan<byte> bytes)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = PrivateFileMode;

        using var stream = new FileStream(path, options);
        stream.Write(bytes);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is removed with the work directory on dispose.
        }
    }

    private static Reporter SilentReporter() =>
        new(json: false, verbose: false, stdout: TextWriter.Null, stderr: TextWriter.Null);

    private static uint GetEuid() => OperatingSystem.IsLinux() ? geteuid() : 0;

    [DllImport("libc")]
    private static extern uint geteuid();

    public void Dispose()
    {
        if (!_workDirectory.IsValueCreated)
            return;

        try
        {
            Directory.Delete(_workDirectory.Value, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of the temp directory.
        }
    }
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

/// <summary>Resolves image-pull credentials for podman; oras accepts the same config.</summary>
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
