using System.Text.Json;
using System.Text.Json.Serialization;
using Narwhal.Config;

namespace Narwhal;

public sealed class LockFile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Narwhal { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public SortedDictionary<string, LockedFlake> Flakes { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, LockedService> Services { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public string Path { get; set; } = "";

    public static LockFile Load(string path)
    {
        if (!File.Exists(path))
            return new LockFile { Path = path };

        var text = File.ReadAllText(path);
        LockFile? lockFile;
        try
        {
            lockFile = JsonSerializer.Deserialize(text, LockJsonContext.Pretty.LockFile);
        }
        catch (JsonException e)
        {
            throw new ManifestException($"cannot parse {path}: {e.Message}");
        }

        if (lockFile is null)
            throw new ManifestException($"cannot parse {path}: empty document");
        if (lockFile.Version != CurrentVersion)
            throw new ManifestException($"{path}: lock file version {lockFile.Version} is not supported by this narwhal (expected {CurrentVersion}); delete it and rebuild");

        lockFile.Path = path;
        return lockFile;
    }

    public void Save()
    {
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
    public string Attr { get; set; } = "";
    public string Flake { get; set; } = "";
    public string Image { get; set; } = "";
    public string Tag { get; set; } = "latest";

    /// <summary>Nix store path of the docker-archive tarball.</summary>
    public string StorePath { get; set; } = "";

    /// <summary>Nix content hash of the image tarball; identical inputs give identical hashes.</summary>
    public string NarHash { get; set; } = "";

    /// <summary>Podman image id after loading, e.g. sha256:abc...</summary>
    public string ImageId { get; set; } = "";
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(LockFile))]
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
