using System.Text.Json;
using Narwhal.Cli;
using Narwhal.Config;

namespace Narwhal.Nix;

public sealed record BuiltImage(string Attr, string StorePath, string ArtifactPath, string NarHash);

public sealed record FlakeMetadata(string Url, string? NarHash, string? Revision, string? LastModified);

/// <summary>Thin, argument-safe wrapper around the nix CLI.</summary>
public sealed class NixClient(ProcessRunner runner, Reporter reporter)
{
    private static readonly string[] FlakeFlags =
        ["--extra-experimental-features", "nix-command flakes"];

    public string Executable => Environment.GetEnvironmentVariable("NARWHAL_NIX") ?? "nix";

    public async Task<BuiltImage> BuildImageAsync(Manifest manifest, ServiceSpec service,
        CancellationToken cancellationToken, bool rebuild = false)
    {
        var attr = service.AttrPath(manifest);
        var flakeRef = ResolveFlakeRef(manifest, service.FlakeRef(manifest));
        var installable = $"{flakeRef}#{attr}";

        var args = new List<string> { "build", "--no-link", "--json" };
        args.AddRange(FlakeFlags);
        if (manifest.Build.Impure)
            args.Add("--impure");
        if (rebuild)
            args.Add("--rebuild");
        args.AddRange(manifest.Build.ExtraArgs);
        args.Add(installable);

        reporter.Step($"building {service.Name} ({attr})");
        var result = await runner.RunAsync(Executable, args, new ProcessOptions
        {
            WorkingDirectory = manifest.Directory,
            Stream = true,
        }, cancellationToken);

        if (!result.Ok)
        {
            throw new BuildException(
                $"nix build failed for '{service.Name}' ({installable})",
                $"{result.Combined.TrimEnd()}");
        }

        var storePath = ParseBuildOutput(result.StdOut, installable);
        var narHash = await NarHashAsync(manifest, storePath, cancellationToken);
        var artifact = ResolveArtifact(storePath);
        return new BuiltImage(attr, storePath, artifact, narHash);
    }

    public async Task<FlakeMetadata> MetadataAsync(string flakeRef, string workingDirectory,
        CancellationToken cancellationToken)
    {
        var reference = ResolveFlakeRef(workingDirectory, flakeRef);
        var args = new List<string> { "flake", "metadata", "--json" };
        args.AddRange(FlakeFlags);
        args.Add(reference);

        var result = await runner.RunAsync(Executable, args, new ProcessOptions
        {
            WorkingDirectory = workingDirectory,
        }, cancellationToken);

        if (!result.Ok)
        {
            throw new BuildException($"nix flake metadata failed for '{reference}'", result.Combined.TrimEnd());
        }

        using var document = JsonDocument.Parse(result.StdOut);
        var root = document.RootElement;

        // `nix flake metadata --json` reports the source hash either at the top
        // level (clean tree) or under `locked` (dirty tree).
        JsonElement locked = default;
        if (root.TryGetProperty("locked", out var lockedElement) && lockedElement.ValueKind == JsonValueKind.Object)
            locked = lockedElement;

        var url = String(root, "resolvedUrl")
            ?? String(locked, "url")
            ?? reference;

        var narHash = String(root, "narHash") ?? String(locked, "narHash");
        var revision = String(root, "revision") ?? String(locked, "rev") ?? String(locked, "dirtyRev");
        var lastModified = Integer(root, "lastModified") ?? Integer(locked, "lastModified");

        return new FlakeMetadata(url, narHash, revision, lastModified is null ? null : lastModified.ToString());
    }

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Integer(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.TryGetInt64(out var number)
            ? number
            : null;

    public async Task<string> VersionAsync(CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(Executable, ["--version"], new ProcessOptions(), cancellationToken);
        return result.Ok ? result.StdOut.Trim() : "unknown";
    }

    private async Task<string> NarHashAsync(Manifest manifest, string storePath, CancellationToken cancellationToken)
    {
        var args = new List<string> { "path-info", "--json" };
        args.AddRange(FlakeFlags);
        args.Add(storePath);

        var result = await runner.RunAsync(Executable, args, new ProcessOptions
        {
            WorkingDirectory = manifest.Directory,
        }, cancellationToken);

        if (result.Ok)
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var root = document.RootElement;

            // Newer nix returns an array of entries; older versions return an
            // object keyed by store path.
            JsonElement entry = default;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                entry = root[0];
            else if (root.ValueKind == JsonValueKind.Object)
                foreach (var property in root.EnumerateObject())
                {
                    entry = property.Value;
                    break;
                }

            if (String(entry, "narHash") is { } value)
                return value;
        }

        // Older nix versions omit narHash; fall back to hashing the artifact.
        var artifact = ResolveArtifact(storePath);
        await using var stream = File.OpenRead(artifact);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
        return "sha256:" + Convert.ToBase64String(hash);
    }

    private static string ParseBuildOutput(string json, string installable)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var entry = root.ValueKind == JsonValueKind.Array ? root[0] : root;

            if (entry.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Object)
            {
                if (outputs.TryGetProperty("out", out var outPath) && outPath.GetString() is { } path)
                    return path;
                foreach (var property in outputs.EnumerateObject())
                {
                    if (property.Value.GetString() is { } anyPath)
                        return anyPath;
                }
            }

            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { } plain)
                return plain;
        }
        catch (JsonException)
        {
            // fall through to the error below
        }

        throw new BuildException(
            $"could not read the build output for '{installable}'",
            "nix build did not report an output path; ensure the attribute points at a single derivation");
    }

    /// <summary>Flake refs that are local paths are resolved against the manifest directory.</summary>
    private static string ResolveFlakeRef(Manifest manifest, string flakeRef) =>
        ResolveFlakeRef(manifest.Directory, flakeRef);

    private static string ResolveFlakeRef(string workingDirectory, string flakeRef)
    {
        if (flakeRef.Contains(':', StringComparison.Ordinal))
            return flakeRef;
        if (flakeRef is "." or ".." || flakeRef.StartsWith("./") || flakeRef.StartsWith("../") || flakeRef.StartsWith('/'))
            return Path.GetFullPath(Path.Combine(workingDirectory, flakeRef));
        return flakeRef;
    }

    /// <summary>
    /// Docker-archive outputs are usually a single tarball, but layered images can
    /// be directories containing one. Accept both.
    /// </summary>
    public static string ResolveArtifact(string storePath)
    {
        if (File.Exists(storePath))
            return storePath;

        if (!Directory.Exists(storePath))
            throw new BuildException($"nix output '{storePath}' does not exist", "the build produced no artifact");

        string[] preferred = ["tarball", "image.tar.gz", "docker-image.tar.gz", "image.tar", "docker-image.tar"];
        foreach (var name in preferred)
        {
            var candidate = Path.Combine(storePath, name);
            if (File.Exists(candidate))
                return candidate;
        }

        var matches = Directory.GetFiles(storePath)
            .Where(f => f.EndsWith(".tar.gz", StringComparison.Ordinal) || f.EndsWith(".tar", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new BuildException(
                $"no container archive found under '{storePath}'",
                "expected a docker-archive tarball; did the attribute produce an image?"),
            _ => throw new BuildException(
                $"multiple container archives found under '{storePath}'",
                string.Join("\n", matches.Select(Path.GetFileName))),
        };
    }
}

public sealed class BuildException(string message, string? detail = null) : Exception(message)
{
    public string? Detail { get; } = detail;
}
