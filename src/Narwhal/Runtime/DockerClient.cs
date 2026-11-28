using System.Globalization;
using System.Text.Json;
using Narwhal.Cli;
using Narwhal.Config;

namespace Narwhal.Runtime;

/// <summary>Runtime-agnostic container failure, reported as exit code 3.</summary>
public class ContainerException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Wrapper around the docker CLI, always invoked with explicit argument lists.</summary>
public sealed class DockerClient(ProcessRunner runner, Reporter reporter, string? executable = null) : IContainerClient
{
    private readonly string? _executable = executable;
    private readonly Dictionary<string, IReadOnlyList<string>> _manifestSources = new(StringComparer.Ordinal);

    public string Executable => _executable ?? Environment.GetEnvironmentVariable("NARWHAL_DOCKER") ?? "docker";

    public async Task EnsureNetworkAsync(string project, string network, CancellationToken cancellationToken)
    {
        var exists = await runner.RunAsync(Executable, ["network", "inspect", network],
            new ProcessOptions(), cancellationToken);
        if (exists.Ok)
            return;

        reporter.Detail($"creating network {network}");
        await runner.RunAsync(Executable,
            ["network", "create", "--label", $"{ContainerNaming.ProjectLabel}={project}", network],
            new ProcessOptions { Check = true }, cancellationToken);
    }

    public async Task EnsureVolumeAsync(string project, string name, CancellationToken cancellationToken)
    {
        var exists = await runner.RunAsync(Executable, ["volume", "inspect", name],
            new ProcessOptions(), cancellationToken);
        if (exists.Ok)
            return;

        await runner.RunAsync(Executable,
            ["volume", "create", "--label", $"{ContainerNaming.ProjectLabel}={project}", name],
            new ProcessOptions { Check = true }, cancellationToken);
    }

    /// <summary>Load a docker-archive tarball and return the resulting image id.</summary>
    public async Task<string> LoadImageAsync(string artifactPath, string reference, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(Executable, ["load", "-i", artifactPath],
            new ProcessOptions { Check = true, Stream = true }, cancellationToken);

        var (loadedName, loadedId) = ParseLoadOutput(result.StdOut);
        var source = loadedName ?? loadedId;
        if (source is not null && NormalizeReference(source) != reference)
            await runner.RunAsync(Executable, ["tag", source, reference],
                new ProcessOptions { Check = true }, cancellationToken);

        return await ImageIdAsync(reference, cancellationToken)
            ?? throw new ContainerException($"image '{reference}' was loaded but has no inspectable id");
    }

    private static (string? Name, string? Id) ParseLoadOutput(string stdout)
    {
        string? name = null;
        string? id = null;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Loaded image ID:", StringComparison.OrdinalIgnoreCase))
                id = line["Loaded image ID:".Length..].Trim();
            else if (line.StartsWith("Loaded image:", StringComparison.OrdinalIgnoreCase))
                name = line["Loaded image:".Length..].Trim();
        }

        return (name, id);
    }

    private static string NormalizeReference(string reference) =>
        reference.Split('@')[0].Replace("docker.io/", "", StringComparison.Ordinal);

    public async Task<string?> ImageIdAsync(string reference, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(Executable,
            ["image", "inspect", "--format", "{{.Id}}", reference],
            new ProcessOptions(), cancellationToken);
        return result.Ok && result.StdOut.Trim().Length > 0 ? result.StdOut.Trim() : null;
    }

    public async Task<bool> ImageExistsAsync(string reference, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(Executable, ["image", "inspect", reference],
            new ProcessOptions(), cancellationToken);
        return result.Ok;
    }

    public async Task PullAsync(string reference, CancellationToken cancellationToken)
    {
        reporter.Step($"pulling {reference}");
        await runner.RunAsync(Executable, ["pull", reference],
            new ProcessOptions { Check = true, Stream = true }, cancellationToken);
    }

    public async Task<bool> ContainerExistsAsync(string name, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(Executable, ["container", "inspect", name],
            new ProcessOptions(), cancellationToken);
        return result.Ok;
    }

    public async Task RemoveContainerAsync(string name, CancellationToken cancellationToken)
    {
        if (!await ContainerExistsAsync(name, cancellationToken))
            return;
        await runner.RunAsync(Executable, ["rm", "--force", name],
            new ProcessOptions(), cancellationToken);
    }

    public async Task RunAsync(Manifest manifest, ServiceSpec service, string network, string containerName,
        string imageReference, string imageId, CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "run", "--detach",
            "--name", containerName,
            "--network", network,
            "--restart", service.RestartPolicy(manifest),
            "--label", $"{ContainerNaming.ProjectLabel}={manifest.Name}",
            "--label", $"{ContainerNaming.ServiceLabel}={service.Name}",
            "--label", $"{ContainerNaming.ManifestLabel}={manifest.ContentHash}",
            "--label", $"{ContainerNaming.ImageIdLabel}={imageId}",
        };

        foreach (var port in service.Ports)
            args.AddRange(["--publish", port]);

        foreach (var (key, value) in service.Env.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            args.AddRange(["--env", $"{key}={value}"]);

        foreach (var volume in service.Volumes)
            args.AddRange(["--volume", volume]);

        if (service.User is not null)
            args.AddRange(["--user", service.User]);
        if (service.Workdir is not null)
            args.AddRange(["--workdir", service.Workdir]);
        if (service.Entrypoint is not null)
            args.AddRange(["--entrypoint", service.Entrypoint]);

        if (service.Healthcheck is not null)
        {
            args.AddRange(["--health-cmd", service.Healthcheck]);
            args.AddRange(["--health-interval", $"{service.HealthInterval}s"]);
            args.AddRange(["--health-retries", service.HealthRetries.ToString(CultureInfo.InvariantCulture)]);
            args.AddRange(["--health-timeout", $"{service.HealthTimeout}s"]);
            args.AddRange(["--health-start-period", "2s"]);
        }

        args.Add(imageReference);
        args.AddRange(service.Command);

        reporter.Detail($"starting {containerName} from {imageReference}");
        var result = await runner.RunAsync(Executable, args, new ProcessOptions
        {
            Stream = true,
        }, cancellationToken);

        if (!result.Ok)
        {
            throw new ContainerException(
                $"failed to start '{service.Name}':\n{result.Combined.TrimEnd()}");
        }
    }

    public async Task<string?> HealthAsync(string containerName, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(Executable,
            ["inspect", "--format", "{{if .State.Health}}{{.State.Health.Status}}{{end}}", containerName],
            new ProcessOptions(), cancellationToken);
        if (!result.Ok)
            return null;
        var value = result.StdOut.Trim();
        return value.Length == 0 ? null : value;
    }

    public async Task<string?> StateAsync(string containerName, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(Executable,
            ["inspect", "--format", "{{.State.Status}}", containerName],
            new ProcessOptions(), cancellationToken);
        return result.Ok ? result.StdOut.Trim() : null;
    }

    public async Task<IReadOnlyList<ContainerInfo>> ListAsync(string project, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(Executable,
            ["ps", "--all", "--filter", $"label={ContainerNaming.ProjectLabel}={project}", "--format", "json"],
            new ProcessOptions { Check = true }, cancellationToken);

        var containers = new List<ContainerInfo>();
        foreach (var line in result.StdOut.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            var entry = document.RootElement;
            var status = String(entry, "Status");
            var health = String(entry, "HealthStatus");
            if (health == "none")
                health = "";
            if (health.Length == 0)
                health = HealthFromStatus(status);

            containers.Add(new ContainerInfo(
                Name: String(entry, "Names"),
                State: String(entry, "State"),
                Status: status,
                Image: String(entry, "Image"),
                Ports: String(entry, "Ports"),
                Health: health,
                Id: String(entry, "ID")));
        }

        return containers;
    }

    public async Task LogsAsync(IReadOnlyList<string> containers, bool follow, int? tail, bool timestamps,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "logs" };
        if (follow)
            args.Add("--follow");
        if (tail is { } count)
            args.AddRange(["--tail", count.ToString(CultureInfo.InvariantCulture)]);
        if (timestamps)
            args.Add("--timestamps");

        // docker logs takes one container, so follow each stream concurrently.
        await Task.WhenAll(containers.Select(name =>
            runner.RunAsync(Executable, [.. args, name], new ProcessOptions { Raw = true }, cancellationToken)));
    }

    /// <summary>Push a specific local image id to a destination reference.</summary>
    public async Task PushByIdAsync(string imageId, string destination, CancellationToken cancellationToken)
    {
        reporter.Step($"pushing {destination}");
        await runner.RunAsync(Executable, ["tag", imageId, destination],
            new ProcessOptions { Check = true }, cancellationToken);
        await runner.RunAsync(Executable, ["push", destination],
            new ProcessOptions { Check = true, Stream = true }, cancellationToken);
    }

    /// <summary>Tag a local image id, replacing any existing owner of the tag.</summary>
    public async Task TagAsync(string imageId, string reference, CancellationToken cancellationToken)
    {
        await runner.RunAsync(Executable, ["tag", imageId, reference],
            new ProcessOptions { Check = true }, cancellationToken);
    }

    /// <summary>Create or amend a local manifest list and add each image id to it.</summary>
    public async Task CreateManifestAsync(string reference, IReadOnlyList<string> imageIds,
        CancellationToken cancellationToken)
    {
        reporter.Step($"creating multi-platform manifest {reference}");
        _manifestSources[reference] = imageIds;

        if (imageIds.Count == 0)
            return;

        var args = new List<string> { "manifest", "create", "--amend", reference };
        args.AddRange(imageIds);
        await runner.RunAsync(Executable, args,
            new ProcessOptions { Check = true }, cancellationToken);
    }

    /// <summary>Push every platform in a manifest list and remove the local list on success.</summary>
    public async Task PushManifestAsync(string index, string destination, CancellationToken cancellationToken)
    {
        reporter.Step($"pushing multi-platform manifest {destination}");
        if (!_manifestSources.TryGetValue(index, out var imageIds) || imageIds.Count == 0)
            throw new ContainerException($"no local manifest list '{index}' was created by this process");

        var insecure = IsLocalRegistry(destination);

        // docker manifest push cannot rename, so rebuild the list at the push destination.
        var create = new List<string> { "manifest", "create", "--amend" };
        if (insecure)
            create.Add("--insecure");
        create.Add(destination);
        create.AddRange(imageIds);
        await runner.RunAsync(Executable, create,
            new ProcessOptions { Check = true }, cancellationToken);

        var push = new List<string> { "manifest", "push", "--purge" };
        if (insecure)
            push.Add("--insecure");
        push.Add(destination);
        await runner.RunAsync(Executable, push,
            new ProcessOptions { Check = true, Stream = true }, cancellationToken);

        await runner.RunAsync(Executable, ["manifest", "rm", index],
            new ProcessOptions(), cancellationToken);
        _manifestSources.Remove(index);
    }

    /// <summary>Remove a local manifest list; a missing list is not an error.</summary>
    public async Task RemoveManifestAsync(string reference, CancellationToken cancellationToken)
    {
        await runner.RunAsync(Executable, ["manifest", "rm", reference],
            new ProcessOptions(), cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ProjectVolumesAsync(string project, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(Executable,
            ["volume", "ls", "--filter", $"label={ContainerNaming.ProjectLabel}={project}", "--format", "{{.Name}}"],
            new ProcessOptions { Check = true }, cancellationToken);
        return result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task RemoveVolumeAsync(string name, CancellationToken cancellationToken)
    {
        await runner.RunAsync(Executable, ["volume", "rm", "--force", name],
            new ProcessOptions(), cancellationToken);
    }

    private static bool IsLocalRegistry(string reference)
    {
        var host = reference.Split('/', 2)[0];
        return host is "localhost" or "127.0.0.1" or "[::1]"
            || host.StartsWith("localhost:", StringComparison.Ordinal)
            || host.StartsWith("127.0.0.1:", StringComparison.Ordinal)
            || host.StartsWith("[::1]:", StringComparison.Ordinal);
    }

    private static string String(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Array => string.Join(",", value.EnumerateArray().Select(v => v.ToString())),
                _ => value.ToString(),
            }
            : "";

    private static string HealthFromStatus(string status)
    {
        if (status.Contains("(healthy)", StringComparison.OrdinalIgnoreCase))
            return "healthy";
        if (status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase))
            return "unhealthy";
        if (status.Contains("(starting)", StringComparison.OrdinalIgnoreCase))
            return "starting";
        return "";
    }
}
