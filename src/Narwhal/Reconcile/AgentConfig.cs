using System.Text.RegularExpressions;
using Narwhal.Cloud;
using Narwhal.Config;

namespace Narwhal.Reconcile;

/// <summary>A local agent configuration or trust error. Maps to exit code 1.</summary>
public sealed class AgentConfigException(string message) : Exception(message);

/// <summary>The host identity and repository layout used by one reconcile cycle.</summary>
public sealed class AgentConfig
{
    public required string Project { get; init; }
    public required string ImageRegistry { get; init; }
    public string? StateRegistry { get; init; }
    public required string DesiredRepository { get; init; }
    public string? StatusRepository { get; init; }
    public required TargetConfig Target { get; init; }
    public int PollSeconds { get; init; } = 30;
    public int LockTimeoutSeconds { get; init; } = 120;
    public string Mode { get; init; } = "daemon";
    public required string TrustDirectory { get; init; }
    public required string StateDirectory { get; init; }
    public string? StatusKeyPath { get; init; }
    /// <summary>Provider-reported host identity, when bootstrap supplied one.</summary>
    public string? HostId { get; init; }
    /// <summary>Registered per-host status public keys used to verify status artifacts.</summary>
    public required StatusKeySet StatusKeys { get; init; }
    /// <summary>Extra literal patterns redacted from status error text and log tails.</summary>
    public IReadOnlyList<string> LogRedactPatterns { get; init; } = [];
    public RegistryAuthConfig Auth { get; init; } = new();

    /// <summary>
    /// Image-pull credentials from the <c>[image_registry_auth]</c> table or <c>NARWHAL_IMAGE_REGISTRY_*</c>
    /// variables. When <c>image_registry</c> equals <c>state_registry</c> and no image credentials are
    /// configured, the state-registry credentials are reused.
    /// </summary>
    public RegistryAuthConfig ImageRegistryAuth { get; init; } = new();

    public TargetExpectation Expectation => new()
    {
        Project = Project,
        Provider = Target.Provider,
        Region = Target.Region,
        Arch = Target.Arch,
        Selector = Target.Selector,
    };

    public string Network => "narwhal-" + Project;
}

public sealed class TargetConfig
{
    public required string Provider { get; init; }
    public required string Region { get; init; }
    public required string Arch { get; init; }
    public required IReadOnlyDictionary<string, string> Selector { get; init; }
}

/// <summary>Registry authentication from a TOML table or prefixed environment variables.</summary>
public sealed class RegistryAuthConfig
{
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Token { get; init; }
    public string? CredentialHelper { get; init; }
    public bool Insecure { get; init; }

    /// <summary>True when the password came from agent.toml rather than the environment.</summary>
    public bool PasswordFromFile { get; init; }

    /// <summary>True when the token came from agent.toml rather than the environment.</summary>
    public bool TokenFromFile { get; init; }

    /// <summary>True when the table or an environment variable supplied a value.</summary>
    public bool Configured { get; init; }
}

/// <summary>Strict reader for <c>/etc/narwhal/agent.toml</c>.</summary>
/// <remarks>
/// State-registry credentials come from <c>[registry_auth]</c> or <c>NARWHAL_REGISTRY_*</c>.
/// Image-pull credentials come from <c>[image_registry_auth]</c> or <c>NARWHAL_IMAGE_REGISTRY_*</c>.
/// Both tables accept <c>username</c>, <c>password</c>, <c>token</c>, <c>credential_helper</c>, and
/// <c>insecure</c>.
/// </remarks>
public static class AgentConfigLoader
{
    public const string DefaultPath = "/etc/narwhal/agent.toml";
    public const string DefaultTrustDirectory = "/etc/narwhal/trust";
    public const string DefaultStateDirectory = "/var/lib/narwhal";
    public const string DefaultStatusKeyPath = "/etc/narwhal/status-key";

    private static readonly string[] RootKeys =
    [
        "project", "image_registry", "state_registry", "desired_repo", "status_repo",
        "poll_seconds", "lock_timeout_seconds", "mode", "trust_dir", "state_dir", "status_key",
        "host_id", "status_keys", "status_public_keys", "log_redact",
        "target", "registry_auth", "image_registry_auth",
    ];

    private static readonly string[] TargetKeys =
    [
        "provider", "region", "arch", "selector", "mode", "trust_dir", "state_dir", "status_key",
        "status_keys", "status_public_keys", "log_redact",
    ];

    private static readonly string[] AuthKeys = ["username", "password", "token", "credential_helper", "insecure"];

    private static readonly string[] Providers = ["aws", "digitalocean", "gcp"];
    private static readonly string[] Architectures = ["amd64", "arm64"];
    private static readonly string[] Modes = ["daemon", "timer"];

    public static AgentConfig Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        string text;
        try
        {
            if (!File.Exists(fullPath))
                throw new AgentConfigException($"agent config not found: {fullPath}");
            text = File.ReadAllText(fullPath);
        }
        catch (AgentConfigException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AgentConfigException($"cannot read {fullPath}: {e.Message}");
        }

        var config = Parse(text, fullPath);
        ValidatePlaintextSecretPermissions(fullPath, config);
        return config;
    }

    private static void ValidatePlaintextSecretPermissions(string path, AgentConfig config)
    {
        if (OperatingSystem.IsWindows() ||
            (!config.Auth.PasswordFromFile && !config.Auth.TokenFromFile &&
             !config.ImageRegistryAuth.PasswordFromFile && !config.ImageRegistryAuth.TokenFromFile))
            return;

        UnixFileMode mode;
        try
        {
            mode = File.GetUnixFileMode(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AgentConfigException($"cannot check permissions on {path}: {e.Message}");
        }

        const UnixFileMode shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & shared) != 0)
            throw new AgentConfigException(
                $"{path}: agent.toml contains a plaintext registry secret but is not mode 0600");
    }

    public static AgentConfig Parse(string text, string path = "agent.toml")
    {
        TomlTable root;
        try
        {
            root = TomlParser.Parse(text);
        }
        catch (TomlException e)
        {
            throw new AgentConfigException($"{path}:{e.Line}:{e.Column}: {e.Message}\n{e.Snippet}");
        }

        var read = new Reader(root, path);
        read.EnsureKnownKeys(RootKeys);

        var project = read.RequiredString("project");
        if (!CloudPatterns.ProjectName().IsMatch(project))
            throw read.Error("project", "expected lowercase letters, digits, '.', '_' or '-'");

        var imageRegistry = ValidateRegistry(read.RequiredString("image_registry"), read, "image_registry");
        var stateRegistry = read.OptionalString("state_registry") is { } value
            ? ValidateRegistry(value, read, "state_registry")
            : null;

        var desiredRepository = ValidateRepository(read.RequiredString("desired_repo"), project, read, "desired_repo");
        var statusRepository = read.OptionalString("status_repo") is { } statusRepo
            ? ValidateRepository(statusRepo, project, read, "status_repo")
            : null;

        var targetTable = read.RequiredTable("target");
        var targetRead = new Reader(targetTable, path, "target");
        targetRead.EnsureKnownKeys(TargetKeys);

        var provider = targetRead.RequiredString("provider");
        if (!Providers.Contains(provider, StringComparer.Ordinal))
            throw targetRead.Error("provider", $"expected one of {string.Join(", ", Providers)}");
        var region = targetRead.RequiredString("region");
        if (!Regex.IsMatch(region, "^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant))
            throw targetRead.Error("region", "expected lowercase letters, digits, and '-'");
        var arch = targetRead.RequiredString("arch");
        if (!Architectures.Contains(arch, StringComparer.Ordinal))
            throw targetRead.Error("arch", $"expected one of {string.Join(", ", Architectures)}");

        var selectorTable = targetRead.RequiredTable("selector");
        var selector = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, entry) in selectorTable.Entries)
        {
            if (entry is not TomlString selectorValue || selectorValue.Value.Length == 0)
                throw targetRead.Error($"selector.{key}", "expected a non-empty string");
            selector.Add(key, selectorValue.Value);
        }

        if (selector.Count == 0)
            throw targetRead.Error("selector", "expected at least one selector entry");

        var mode = ReadMode(read, targetRead);
        var trustDirectory = ReadDirectory(read, targetRead, "trust_dir", DefaultTrustDirectory);
        var stateDirectory = ReadDirectory(read, targetRead, "state_dir", DefaultStateDirectory);
        var statusKeyPath = ReadOptionalAbsolutePath(read, targetRead, "status_key") ?? DefaultStatusKeyPath;
        var hostId = ReadHostId(read);
        var statusKeys = ReadStatusKeys(read, targetRead, path);
        var logRedact = ReadStringArray(targetRead, "log_redact", ReadStringArray(read, "log_redact"));

        var pollSeconds = ReadInteger(read, "poll_seconds", 30, 1, 86_400);
        var lockTimeoutSeconds = ReadInteger(read, "lock_timeout_seconds", 120, 0, 3_600);

        var auth = ReadAuth(read, "registry_auth", "NARWHAL_REGISTRY_");
        var imageAuth = ReadAuth(read, "image_registry_auth", "NARWHAL_IMAGE_REGISTRY_");
        var desiredRegistry = stateRegistry ?? imageRegistry;
        if (!imageAuth.Configured &&
            string.Equals(imageRegistry, desiredRegistry, StringComparison.OrdinalIgnoreCase))
            imageAuth = auth;

        return new AgentConfig
        {
            Project = project,
            ImageRegistry = imageRegistry,
            StateRegistry = stateRegistry,
            DesiredRepository = desiredRepository,
            StatusRepository = statusRepository,
            Target = new TargetConfig
            {
                Provider = provider,
                Region = region,
                Arch = arch,
                Selector = selector,
            },
            PollSeconds = (int)pollSeconds,
            LockTimeoutSeconds = (int)lockTimeoutSeconds,
            Mode = mode,
            TrustDirectory = trustDirectory,
            StateDirectory = stateDirectory,
            StatusKeyPath = statusKeyPath,
            HostId = hostId,
            StatusKeys = statusKeys,
            LogRedactPatterns = logRedact,
            Auth = auth,
            ImageRegistryAuth = imageAuth,
        };
    }

    private static string? ReadHostId(Reader read)
    {
        var value = read.OptionalString("host_id");
        if (value is null)
            return null;
        if (value.Length > 256 || !Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._:-]{0,255}$", RegexOptions.CultureInvariant))
            throw read.Error("host_id", "expected 1-256 characters of letters, digits, '.', '_', ':' or '-'");
        return value;
    }

    private static IReadOnlyList<string> ReadStringArray(
        Reader read,
        string key,
        IReadOnlyList<string>? fallback = null)
    {
        var value = read.Get(key);
        if (value is null)
            return fallback ?? [];
        if (value is not TomlArray array)
            throw read.Error(key, "expected an array of strings");

        var values = new List<string>(array.Items.Count);
        for (var i = 0; i < array.Items.Count; i++)
        {
            if (array.Items[i] is not TomlString text || text.Value.Length == 0 || text.Value.Length > 1024)
                throw read.Error($"{key}[{i}]", "expected a non-empty string of at most 1024 characters");
            values.Add(text.Value);
        }

        return values;
    }

    private static StatusKeySet ReadStatusKeys(Reader root, Reader target, string path)
    {
        var keys = new Dictionary<string, TrustedKey>(StringComparer.Ordinal);
        MergeStatusKeyTable(root, "status_keys", "status_keys", keys, path);
        MergeStatusKeyTable(target, "status_keys", "target.status_keys", keys, path);
        MergeStatusPublicKeys(root, "status_public_keys", "status_public_keys", keys, path);
        MergeStatusPublicKeys(target, "status_public_keys", "target.status_public_keys", keys, path);
        return StatusKeySet.FromKeys(keys.Values);
    }

    private static void MergeStatusKeyTable(
        Reader reader,
        string key,
        string name,
        Dictionary<string, TrustedKey> keys,
        string path)
    {
        var value = reader.Get(key);
        if (value is null)
            return;
        if (value is not TomlTable table)
            throw new AgentConfigException($"{path}: {name}: expected a table of status public keys");

        foreach (var (keyId, entry) in table.Entries)
        {
            if (!CloudPatterns.KeyId().IsMatch(keyId))
                throw new AgentConfigException($"{path}: {name}.{keyId}: expected 16 lowercase hex characters");

            string publicKeyText;
            string? notAfterText = null;
            if (entry is TomlString text)
            {
                publicKeyText = text.Value;
            }
            else if (entry is TomlTable keyTable)
            {
                foreach (var nested in keyTable.Entries.Keys)
                {
                    if (nested is not ("public_key" or "not_after"))
                        throw new AgentConfigException($"{path}: {name}.{keyId}.{nested}: unknown key");
                }

                publicKeyText = keyTable.Get("public_key") is TomlString configuredKey ? configuredKey.Value
                    : throw new AgentConfigException($"{path}: {name}.{keyId}.public_key: expected a string");
                if (keyTable.Get("not_after") is { } configuredNotAfter)
                {
                    if (configuredNotAfter is not TomlString timestamp)
                        throw new AgentConfigException($"{path}: {name}.{keyId}.not_after: expected a string");
                    notAfterText = timestamp.Value;
                }
            }
            else
            {
                throw new AgentConfigException($"{path}: {name}.{keyId}: expected a public key string or table");
            }

            if (!CloudEncoding.TryDecodeBase64(publicKeyText, out var publicKey) || publicKey.Length != 32)
                throw new AgentConfigException($"{path}: {name}.{keyId}: expected canonical base64 for a 32-byte key");
            if (!string.Equals(KeyId.Derive(publicKey), keyId, StringComparison.Ordinal))
                throw new AgentConfigException($"{path}: {name}.{keyId}: key ID does not match the public key");

            DateTimeOffset? notAfter = null;
            if (notAfterText is not null)
            {
                if (!CloudEncoding.TryParseUtcTimestamp(notAfterText, out var parsed))
                    throw new AgentConfigException($"{path}: {name}.{keyId}.not_after: expected an RFC 3339 UTC timestamp");
                notAfter = parsed;
            }

            AddStatusKey(keys, new TrustedKey { KeyId = keyId, PublicKey = publicKey, NotAfter = notAfter }, path);
        }
    }

    private static void MergeStatusPublicKeys(
        Reader reader,
        string key,
        string name,
        Dictionary<string, TrustedKey> keys,
        string path)
    {
        var value = reader.Get(key);
        if (value is null)
            return;
        if (value is not TomlArray array)
            throw new AgentConfigException($"{path}: {name}: expected an array of base64 public keys");

        for (var i = 0; i < array.Items.Count; i++)
        {
            if (array.Items[i] is not TomlString text)
                throw new AgentConfigException($"{path}: {name}[{i}]: expected a string");
            if (!CloudEncoding.TryDecodeBase64(text.Value, out var publicKey) || publicKey.Length != 32)
                throw new AgentConfigException($"{path}: {name}[{i}]: expected canonical base64 for a 32-byte key");
            AddStatusKey(keys, new TrustedKey { KeyId = KeyId.Derive(publicKey), PublicKey = publicKey }, path);
        }
    }

    private static void AddStatusKey(Dictionary<string, TrustedKey> keys, TrustedKey key, string path)
    {
        if (keys.TryGetValue(key.KeyId, out var existing))
        {
            if (!existing.PublicKey.AsSpan().SequenceEqual(key.PublicKey) || existing.NotAfter != key.NotAfter)
                throw new AgentConfigException($"{path}: status key {key.KeyId} is configured twice with different values");
            return;
        }

        keys.Add(key.KeyId, key);
    }

    private static RegistryAuthConfig ReadAuth(Reader read, string tableName, string environmentPrefix)
    {
        var envUsername = Environment.GetEnvironmentVariable(environmentPrefix + "USERNAME");
        var envPassword = Environment.GetEnvironmentVariable(environmentPrefix + "PASSWORD");
        var envToken = Environment.GetEnvironmentVariable(environmentPrefix + "TOKEN");
        var envHelper = Environment.GetEnvironmentVariable(environmentPrefix + "CREDENTIAL_HELPER");
        var envInsecure = Environment.GetEnvironmentVariable(environmentPrefix + "INSECURE");
        var configured = envUsername is not null || envPassword is not null || envToken is not null ||
                         envHelper is not null || envInsecure is not null;
        var environment = new RegistryAuthConfig
        {
            Username = envUsername,
            Password = envPassword,
            Token = envToken,
            CredentialHelper = envHelper,
            Insecure = envInsecure is "1" or "true",
            Configured = configured,
        };

        if (read.Get(tableName) is null)
            return environment;

        var table = read.RequiredTable(tableName);
        var authRead = new Reader(table, read.Path, tableName);
        authRead.EnsureKnownKeys(AuthKeys);

        var username = authRead.OptionalString("username");
        var password = authRead.OptionalString("password");
        if ((username is null) != (password is null))
            throw authRead.Error("username", "username and password must be set together");

        var token = authRead.OptionalString("token");
        return new RegistryAuthConfig
        {
            Username = username ?? environment.Username,
            Password = password ?? environment.Password,
            PasswordFromFile = password is not null,
            Token = token ?? environment.Token,
            TokenFromFile = token is not null,
            CredentialHelper = authRead.OptionalString("credential_helper") ?? environment.CredentialHelper,
            Insecure = authRead.OptionalBool("insecure") ?? environment.Insecure,
            Configured = true,
        };
    }

    private static string ValidateRegistry(string value, Reader read, string key)
    {
        if (!Regex.IsMatch(value, "^[a-z0-9]([a-z0-9.-]*[a-z0-9])?(:[0-9]{1,5})?$", RegexOptions.CultureInvariant))
            throw read.Error(key, "expected a registry host with an optional port and no scheme");
        return value;
    }

    private static string ValidateRepository(string value, string project, Reader read, string key)
    {
        if (!CloudPatterns.Repository().IsMatch(value))
            throw read.Error(key, "expected a repository name without a tag or digest");
        if (!value.StartsWith(project + "/", StringComparison.Ordinal))
            throw read.Error(key, $"repository must start with '{project}/'");
        return value;
    }

    private static string ReadMode(Reader root, Reader target)
    {
        var mode = root.OptionalString("mode") ?? target.OptionalString("mode") ?? "daemon";
        if (!Modes.Contains(mode, StringComparer.Ordinal))
            throw root.Error("mode", $"expected one of {string.Join(", ", Modes)}");
        return mode;
    }

    private static string ReadDirectory(Reader root, Reader target, string key, string fallback)
    {
        var path = root.OptionalString(key) ?? target.OptionalString(key) ?? fallback;
        if (!Path.IsPathRooted(path))
            throw root.Error(key, "expected an absolute path");
        return path;
    }

    private static string? ReadOptionalAbsolutePath(Reader root, Reader target, string key)
    {
        var path = root.OptionalString(key) ?? target.OptionalString(key);
        if (path is null)
            return null;
        if (!Path.IsPathRooted(path))
            throw root.Error(key, "expected an absolute path");
        return path;
    }

    private static long ReadInteger(Reader read, string key, long fallback, long min, long max)
    {
        if (read.Get(key) is null)
            return fallback;
        var value = read.RequiredInteger(key);
        if (value < min || value > max)
            throw read.Error(key, $"expected a number between {min} and {max}");
        return value;
    }

    private sealed class Reader(TomlTable table, string path, string prefix = "")
    {
        public string Path => path;

        public TomlValue? Get(string key) => table.Get(key);

        public void EnsureKnownKeys(IReadOnlyList<string> known)
        {
            foreach (var key in table.Entries.Keys)
            {
                if (!known.Contains(key, StringComparer.Ordinal))
                    throw Error(key, "unknown key");
            }
        }

        public string RequiredString(string key) =>
            OptionalString(key) ?? throw Error(key, "expected a string");

        public string? OptionalString(string key)
        {
            var value = table.Get(key);
            if (value is null)
                return null;
            return value is TomlString text && text.Value.Length > 0
                ? text.Value
                : throw Error(key, "expected a non-empty string");
        }

        public long RequiredInteger(string key)
        {
            var value = table.Get(key);
            return value is TomlInteger integer
                ? integer.Value
                : throw Error(key, "expected an integer");
        }

        public bool? OptionalBool(string key)
        {
            var value = table.Get(key);
            if (value is null)
                return null;
            return value is TomlBoolean boolean
                ? boolean.Value
                : throw Error(key, "expected a boolean");
        }

        public TomlTable RequiredTable(string key)
        {
            var value = table.Get(key);
            return value as TomlTable ?? throw Error(key, "expected a table");
        }

        public AgentConfigException Error(string key, string message)
        {
            var name = prefix.Length == 0 ? key : $"{prefix}.{key}";
            return new AgentConfigException($"{path}: {name}: {message}");
        }
    }
}
