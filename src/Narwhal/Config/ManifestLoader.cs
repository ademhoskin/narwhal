namespace Narwhal.Config;

/// <summary>Turns TOML into a validated <see cref="Manifest"/>, with pointed errors.</summary>
public static class ManifestLoader
{
    public const string FileName = "narwhal.toml";
    public const string LockFileName = "narwhal.lock.json";

    public static Manifest Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new ManifestException($"manifest not found: {fullPath}");

        string text;
        try
        {
            text = File.ReadAllText(fullPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ManifestException($"cannot read {fullPath}: {e.Message}");
        }

        TomlTable root;
        try
        {
            root = TomlParser.Parse(text);
        }
        catch (TomlException e)
        {
            throw new ManifestException($"{fullPath}:{e.Line}:{e.Column}: {e.Message}\n{e.Snippet}");
        }

        var directory = Path.GetDirectoryName(fullPath)!;
        var read = new Reader(root, fullPath);

        var name = read.OptionalString("name") ?? SanitizeName(Path.GetFileName(directory));
        if (!Hashing.IsValidServiceName(name))
            throw new ManifestException($"{fullPath}: project name '{name}' is not a valid container name; use lowercase letters, digits, '.', '_' or '-'");

        var build = read.Table("build", t =>
        {
            var builder = new Reader(t, fullPath, "build");
            var systems = builder.StringArray("systems");

            if (builder.Get("system") is not null)
                throw new ManifestException(
                    $"{fullPath}: build.system is not supported; use systems = [\"<nix-system>\", ...]");

            if (systems.Count == 0 && builder.Get("systems") is not null)
                throw new ManifestException($"{fullPath}: build.systems must name at least one system");

            if (systems.Count == 0)
                systems = [NixPlatform.ContainerSystem];

            return new BuildOptions
            {
                AttrPrefix = builder.OptionalString("attr_prefix") ?? "narwhal.images",
                Systems = systems,
                ExtraArgs = builder.StringArray("extra_args"),
                Impure = builder.OptionalBool("impure") ?? false,
            };
        }) ?? new BuildOptions();

        var deploy = read.Table("deploy", t =>
        {
            var reader = new Reader(t, fullPath, "deploy");
            return new DeployOptions
            {
                Network = reader.OptionalString("network"),
                Restart = reader.OptionalString("restart") ?? "unless-stopped",
                WaitSeconds = (int)(reader.OptionalInteger("wait_seconds") ?? 60),
            };
        }) ?? new DeployOptions();

        var services = new List<ServiceSpec>();
        if (root.Get("service") is { } serviceValue)
        {
            if (serviceValue is not TomlArray { } array)
                throw new ManifestException($"{fullPath}: 'service' must be an array of tables ([[service]])");

            for (var i = 0; i < array.Items.Count; i++)
            {
                if (array.Items[i] is not TomlTable table)
                    throw new ManifestException($"{fullPath}: service[{i}] must be a table");
                services.Add(ReadService(table, new Reader(table, fullPath, $"service[{i}]")));
            }
        }

        if (services.Count == 0)
            throw new ManifestException($"{fullPath}: no services defined; add at least one [[service]] table");

        var manifest = new Manifest
        {
            Name = name,
            Directory = directory,
            Path = fullPath,
            Flake = read.OptionalString("flake") ?? ".",
            Build = build,
            Deploy = deploy,
            Services = services,
            RawText = text,
        };

        Validate(manifest);
        return manifest;
    }

    /// <summary>Walk up from <paramref name="startDirectory"/> looking for narwhal.toml.</summary>
    public static string? Discover(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, FileName);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    private static ServiceSpec ReadService(TomlTable table, Reader read)
    {
        var name = read.RequiredString("name");
        if (!Hashing.IsValidServiceName(name))
            throw new ManifestException($"{read.Location}: invalid service name '{name}'; use lowercase letters, digits, '.', '_' or '-'");

        var buildTable = read.Table("build", t =>
        {
            var builder = new Reader(t, read.FilePath, $"{read.Location}.build");
            return new ServiceSpec
            {
                Name = name,
                Prebuilt = builder.OptionalBool("prebuilt") ?? false,
                Attr = builder.OptionalString("attr"),
                Flake = builder.OptionalString("flake"),
                Image = builder.OptionalString("image"),
                Tag = builder.OptionalString("tag") ?? "latest",
            };
        });

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (read.Table("env", t =>
            {
                var reader = new Reader(t, read.FilePath, $"{read.Location}.env");
                return reader.StringMap();
            }) is { } envTable)
        {
            foreach (var (key, value) in envTable)
                env[key] = value;
        }

        var health = read.Table("healthcheck", t =>
        {
            var reader = new Reader(t, read.FilePath, $"{read.Location}.healthcheck");
            return new HealthcheckSpec
            {
                Command = reader.OptionalString("command") ?? reader.OptionalString("cmd")
                    ?? throw new ManifestException($"{reader.Location}: healthcheck.command is required"),
                Interval = (int)(reader.OptionalInteger("interval") ?? 5),
                Retries = (int)(reader.OptionalInteger("retries") ?? 24),
                Timeout = (int)(reader.OptionalInteger("timeout") ?? 3),
            };
        });

        var spec = new ServiceSpec
        {
            Name = name,
            Description = read.OptionalString("description"),
            Flake = buildTable?.Flake ?? read.OptionalString("flake"),
            Attr = buildTable?.Attr ?? read.OptionalString("attr"),
            Image = buildTable?.Image ?? read.OptionalString("image"),
            Tag = buildTable?.Tag ?? read.OptionalString("tag") ?? "latest",
            Prebuilt = buildTable?.Prebuilt ?? read.OptionalBool("prebuilt") ?? false,
            Ports = read.StringArray("ports"),
            Env = env,
            Volumes = read.StringArray("volumes"),
            Command = read.StringArray("command"),
            Entrypoint = read.OptionalString("entrypoint"),
            User = read.OptionalString("user"),
            Workdir = read.OptionalString("workdir"),
            Restart = read.OptionalString("restart"),
            DependsOn = read.StringArray("depends_on"),
            Healthcheck = health?.Command ?? read.OptionalString("healthcheck"),
            HealthInterval = health?.Interval ?? 5,
            HealthRetries = health?.Retries ?? 24,
            HealthTimeout = health?.Timeout ?? 3,
            StopTimeout = (int)(read.OptionalInteger("stop_timeout") ?? 10),
        };

        read.RejectUnknown(
            "name", "description", "flake", "attr", "image", "tag", "prebuilt", "ports", "env",
            "volumes", "command", "entrypoint", "user", "workdir", "restart", "depends_on",
            "healthcheck", "stop_timeout", "build");

        return spec;
    }

    private static void Validate(Manifest manifest)
    {
        var systems = manifest.Build.Systems;
        if (systems.Count == 0)
            throw new ManifestException($"{manifest.Path}: build.systems must name at least one system");

        var seenSystems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var system in systems)
        {
            if (!seenSystems.Add(system))
                throw new ManifestException($"{manifest.Path}: build.systems lists '{system}' more than once");
            if (!NixPlatform.IsSupported(system))
                throw new ManifestException(
                    $"{manifest.Path}: build.systems contains unsupported system '{system}'; supported systems: {NixPlatform.SupportedSystemsHint}");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in manifest.Services)
        {
            if (!seen.Add(service.Name))
                throw new ManifestException($"{manifest.Path}: duplicate service '{service.Name}'");

            foreach (var dependency in service.DependsOn)
            {
                if (manifest.Find(dependency) is null)
                    throw new ManifestException($"{manifest.Path}: service '{service.Name}' depends on unknown service '{dependency}'");
                if (dependency == service.Name)
                    throw new ManifestException($"{manifest.Path}: service '{service.Name}' cannot depend on itself");
            }

            foreach (var port in service.Ports)
            {
                if (!IsValidPortSpec(port))
                    throw new ManifestException($"{manifest.Path}: service '{service.Name}' has invalid port mapping '{port}' (expected host:container[/proto])");
            }
        }

        _ = manifest.Ordered(); // surfaces dependency cycles early
    }

    private static bool IsValidPortSpec(string spec)
    {
        var value = spec;
        var slash = value.LastIndexOf('/');
        if (slash >= 0)
        {
            var protocol = value[(slash + 1)..];
            if (protocol is not ("tcp" or "udp" or "sctp"))
                return false;
            value = value[..slash];
        }

        var parts = value.Split(':');
        if (parts.Length is < 1 or > 3)
            return false;

        foreach (var part in parts)
        {
            if (part.Length == 0)
                return false;
            if (!int.TryParse(part, out var port) || port is < 1 or > 65535)
                return false;
        }

        return true;
    }

    private static string SanitizeName(string raw)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in raw.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                builder.Append(c);
            else
                builder.Append('-');
        }
        var value = builder.ToString().Trim('-', '.', '_');
        return value.Length == 0 ? "narwhal" : value;
    }

    private sealed class HealthcheckSpec
    {
        public required string Command { get; init; }
        public int Interval { get; init; }
        public int Retries { get; init; }
        public int Timeout { get; init; }
    }

    /// <summary>Typed accessors that report the manifest path and key on every error.</summary>
    private sealed class Reader(TomlTable table, string filePath, string location = "")
    {
        public string FilePath { get; } = filePath;
        public string Location { get; } = location.Length == 0 ? "manifest" : location;
        private string Prefix => Location == "manifest" ? string.Empty : $"{Location}.";

        public TomlValue? Get(string key) => table.Get(key);

        public string? OptionalString(string key)
        {
            var value = Get(key);
            if (value is null)
                return null;
            return value switch
            {
                TomlString s => s.Value,
                _ => throw new ManifestException($"{FilePath}: '{Prefix}{key}' must be a string, found {value.Kind}"),
            };
        }

        public string RequiredString(string key) =>
            OptionalString(key) ?? throw new ManifestException($"{FilePath}: '{Prefix}{key}' is required");

        public bool? OptionalBool(string key)
        {
            var value = Get(key);
            if (value is null)
                return null;
            return value switch
            {
                TomlBoolean b => b.Value,
                _ => throw new ManifestException($"{FilePath}: '{Prefix}{key}' must be a boolean, found {value.Kind}"),
            };
        }

        public long? OptionalInteger(string key)
        {
            var value = Get(key);
            if (value is null)
                return null;
            return value switch
            {
                TomlInteger i => i.Value,
                _ => throw new ManifestException($"{FilePath}: '{Prefix}{key}' must be an integer, found {value.Kind}"),
            };
        }

        public List<string> StringArray(string key)
        {
            if (Get(key) is not { } value)
                return [];

            if (value is not TomlArray array)
                throw new ManifestException($"{FilePath}: '{Prefix}{key}' must be an array of strings, found {value.Kind}");

            var items = new List<string>(array.Items.Count);
            for (var i = 0; i < array.Items.Count; i++)
            {
                if (array.Items[i] is not TomlString s)
                    throw new ManifestException($"{FilePath}: '{Prefix}{key}[{i}]' must be a string, found {array.Items[i].Kind}");
                items.Add(s.Value);
            }
            return items;
        }

        public Dictionary<string, string> StringMap()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in table.Entries)
            {
                switch (value)
                {
                    case TomlString s:
                        result[key] = s.Value;
                        break;
                    case TomlInteger i:
                        result[key] = i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case TomlBoolean b:
                        result[key] = b.Value ? "true" : "false";
                        break;
                    case TomlFloat f:
                        result[key] = f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    default:
                        throw new ManifestException($"{FilePath}: '{Prefix}{key}' must be a scalar value, found {value.Kind}");
                }
            }
            return result;
        }

        public T? Table<T>(string key, Func<TomlTable, T> read) where T : class
        {
            if (Get(key) is not { } value)
                return null;
            if (value is not TomlTable t)
                throw new ManifestException($"{FilePath}: '{Prefix}{key}' must be a table, found {value.Kind}");
            return read(t);
        }

        public void RejectUnknown(params string[] allowed)
        {
            var known = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (var key in table.Entries.Keys)
            {
                if (!known.Contains(key))
                    throw new ManifestException($"{FilePath}: unknown key '{Prefix}{key}' in service '{Location}'; known keys: {string.Join(", ", allowed)}");
            }
        }
    }
}
