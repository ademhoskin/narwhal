using System.Text.Json;
using System.Text.Json.Serialization;
using Narwhal.Config;

namespace Narwhal;

public sealed class LockFile
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public string Narwhal { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public SortedDictionary<string, LockedFlake> Flakes { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, LockedService> Services { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public string Path { get; set; } = "";

    /// <summary>True when the file was loaded as version 1 and should be rewritten.</summary>
    [JsonIgnore]
    public bool NeedsMigration { get; set; }

    public static LockFile Load(string path)
    {
        if (!File.Exists(path))
            return new LockFile { Path = path };

        var text = File.ReadAllText(path);
        var version = ReadVersion(path, text);

        if (version == 1)
        {
            var legacy = Deserialize<LegacyLockFile>(path, text);
            if (legacy is null)
                throw new ManifestException($"cannot parse {path}: empty document");

            return Migrate(legacy, path);
        }

        if (version != CurrentVersion)
            throw new ManifestException(
                $"{path}: lock file version {version} is not supported by this narwhal (expected {CurrentVersion}); delete it and rebuild");

        var lockFile = Deserialize<LockFile>(path, text);
        if (lockFile is null)
            throw new ManifestException($"cannot parse {path}: empty document");

        lockFile.Path = path;
        return lockFile;
    }

    public void Save()
    {
        Version = CurrentVersion;
        NeedsMigration = false;
        var json = JsonSerializer.Serialize(this, LockJsonContext.Pretty.LockFile);
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Write atomically so a killed build never leaves a half-written lock.
        var temp = Path + ".tmp";
        File.WriteAllText(temp, json + "\n");
        File.Move(temp, Path, overwrite: true);
    }

    public LockedService? Service(string name) =>
        Services.TryGetValue(name, out var service) ? service : null;

    public LockedPlatform? Platform(string serviceName, string system) =>
        Service(serviceName)?.Platforms.TryGetValue(system, out var platform) == true ? platform : null;

    /// <summary>True when every non-prebuilt service is locked for every configured system.</summary>
    public bool Covers(Manifest manifest) =>
        manifest.Services
            .Where(service => !service.Prebuilt)
            .All(service =>
            {
                var entry = Service(service.Name);
                return entry is not null && manifest.Build.Systems.All(system =>
                {
                    var platform = entry.Platforms.TryGetValue(system, out var value) ? value : null;
                    return platform is not null
                        && platform.Attr == service.AttrPath(manifest, system)
                        && platform.NarHash.Length > 0
                        && platform.Platform == NixPlatform.ToOciPlatform(system)
                        && platform.ImageId.Length > 0;
                });
            });

    private static int ReadVersion(string path, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("version", out var version)
                || !version.TryGetInt32(out var value))
                throw new ManifestException($"cannot parse {path}: missing lock file version");
            return value;
        }
        catch (JsonException e)
        {
            throw new ManifestException($"cannot parse {path}: {e.Message}");
        }
    }

    private static T? Deserialize<T>(string path, string text)
    {
        try
        {
            return typeof(T) == typeof(LockFile)
                ? (T?)(object?)JsonSerializer.Deserialize(text, LockJsonContext.Pretty.LockFile)
                : (T?)(object?)JsonSerializer.Deserialize(text, LockJsonContext.Pretty.LegacyLockFile);
        }
        catch (JsonException e)
        {
            throw new ManifestException($"cannot parse {path}: {e.Message}");
        }
    }

    private static LockFile Migrate(LegacyLockFile legacy, string path)
    {
        var lockFile = new LockFile
        {
            Version = CurrentVersion,
            Narwhal = legacy.Narwhal,
            ManifestSha256 = legacy.ManifestSha256,
            Flakes = legacy.Flakes,
            Path = path,
            NeedsMigration = true,
        };

        foreach (var (name, service) in legacy.Services)
        {
            var system = SystemFromAttr(service.Attr);
            var migrated = new LockedService
            {
                Flake = service.Flake,
                Image = service.Image,
                Tag = service.Tag,
            };
            migrated.Platforms[system] = new LockedPlatform
            {
                Platform = NixPlatform.ToOciPlatform(system),
                Attr = service.Attr,
                StorePath = service.StorePath,
                NarHash = service.NarHash,
                ImageId = service.ImageId,
            };
            lockFile.Services[name] = migrated;
        }

        return lockFile;
    }

    /// <summary>The <c>&lt;arch&gt;-linux</c> token in a version 1 attr, or the host system.</summary>
    private static string SystemFromAttr(string attr)
    {
        foreach (var segment in attr.Split('.'))
        {
            if (segment.EndsWith("-linux", StringComparison.Ordinal) && NixPlatform.IsSupported(segment))
                return segment;
        }
        return NixPlatform.ContainerSystem;
    }
}

public sealed class LockedFlake
{
    public string Url { get; set; } = "";
    public string? NarHash { get; set; }
    public string? Revision { get; set; }
    public string? LastModified { get; set; }
}

public sealed class LockedService
{
    public string Flake { get; set; } = "";
    public string Image { get; set; } = "";
    public string Tag { get; set; } = "latest";

    /// <summary>Locked entries keyed by Nix system, in deterministic order.</summary>
    public SortedDictionary<string, LockedPlatform> Platforms { get; set; } = new(StringComparer.Ordinal);
}

public sealed class LockedPlatform
{
    /// <summary>OCI platform string, e.g. linux/arm64.</summary>
    public string Platform { get; set; } = "";

    public string Attr { get; set; } = "";

    /// <summary>Nix store path of the docker-archive tarball.</summary>
    public string StorePath { get; set; } = "";

    /// <summary>Nix content hash of the image tarball; identical inputs give identical hashes.</summary>
    public string NarHash { get; set; } = "";

    /// <summary>Image id read from the archive, e.g. sha256:abc...</summary>
    public string ImageId { get; set; } = "";
}

/// <summary>Version 1 lock shape, read only so existing locks migrate in place.</summary>
internal sealed class LegacyLockFile
{
    public int Version { get; set; }
    public string Narwhal { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public SortedDictionary<string, LockedFlake> Flakes { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, LegacyLockedService> Services { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class LegacyLockedService
{
    public string Attr { get; set; } = "";
    public string Flake { get; set; } = "";
    public string Image { get; set; } = "";
    public string Tag { get; set; } = "latest";
    public string StorePath { get; set; } = "";
    public string NarHash { get; set; } = "";
    public string ImageId { get; set; } = "";
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(LockFile))]
[JsonSerializable(typeof(LegacyLockFile))]
internal sealed partial class LockJsonContext : JsonSerializerContext
{
    /// <summary>Readable output: no \u002B-style escaping in URLs and hashes.</summary>
    public static LockJsonContext Pretty { get; } = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
