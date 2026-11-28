using Narwhal.Cli;
using Narwhal.Config;
using Narwhal.Nix;
using Narwhal.Podman;
using Narwhal.Runtime;

namespace Narwhal.Commands;

/// <summary>Everything a command needs: parsed manifest, lock, and tool clients.</summary>
public sealed class CommandContext
{
    private CommandContext()
    {
    }

    public required Manifest Manifest { get; init; }
    public required LockFile Lock { get; init; }
    public required Reporter Reporter { get; init; }
    public required ProcessRunner Runner { get; init; }
    public required NixClient Nix { get; init; }
    public required IContainerClient Podman { get; init; }

    public string Network => Manifest.Deploy.Network ?? ContainerNaming.DefaultNetwork(Manifest.Name);

    public static string HostSystem => NixPlatform.ContainerSystem;

    public static CommandContext Create(CliOptions options)
    {
        var directory = options.Directory ?? Environment.CurrentDirectory;
        if (!Directory.Exists(directory))
            throw new UsageException($"directory not found: {directory}");
        directory = Path.GetFullPath(directory);

        var manifestPath = options.File is not null
            ? Path.GetFullPath(Path.Combine(directory, options.File))
            : ManifestLoader.Discover(directory)
              ?? throw new UsageException($"no {ManifestLoader.FileName} found in {directory} or any parent directory\nrun 'narwhal init' to create one");

        var manifest = ManifestLoader.Load(manifestPath);
        manifest.LockPath = options.LockPath is not null
            ? Path.GetFullPath(Path.Combine(directory, options.LockPath))
            : Path.Combine(manifest.Directory, ManifestLoader.LockFileName);

        var reporter = new Reporter(options.Json, options.Verbose, options.NoColor ? false : null);
        var runner = new ProcessRunner(reporter, options.Verbose);

        return new CommandContext
        {
            Manifest = manifest,
            Lock = LockFile.Load(manifest.LockPath),
            Reporter = reporter,
            Runner = runner,
            Nix = new NixClient(runner, reporter),
            Podman = new PodmanClient(runner, reporter),
        };
    }

    /// <summary>Selected services in manifest order; all services when no names are given.</summary>
    public IReadOnlyList<ServiceSpec> Select(IEnumerable<string> names, bool includePrebuilt = true)
    {
        var requested = names.Where(n => n.Length > 0).ToList();
        CommandLine.EnsureServicesExist(Manifest, requested);

        var selected = requested.Count == 0
            ? Manifest.Services
            : Manifest.Services.Where(s => requested.Contains(s.Name, StringComparer.Ordinal)).ToList();

        return includePrebuilt ? selected : selected.Where(s => !s.Prebuilt).ToList();
    }

    public string ContainerName(ServiceSpec service) => ContainerNaming.ContainerName(Manifest.Name, service.Name);

    /// <summary>Configured systems, or the subset named by repeated <c>--system</c> flags.</summary>
    public IReadOnlyList<string> SelectSystems(CliOptions options)
    {
        var requested = options.ListValue("system");
        if (requested.Count == 0)
            return Manifest.Build.Systems;

        var selected = new List<string>();
        foreach (var system in requested)
        {
            if (!Manifest.Build.Systems.Contains(system, StringComparer.Ordinal))
                throw new UsageException(
                    $"system '{system}' is not configured in {Path.GetFileName(Manifest.Path)}; configured systems: {string.Join(", ", Manifest.Build.Systems)}");
            if (!selected.Contains(system, StringComparer.Ordinal))
                selected.Add(system);
        }

        return selected;
    }
}

public sealed class ReproducibilityException(string message) : Exception(message);
