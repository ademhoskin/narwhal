using System.Text.Json;
using Narwhal.Cli;
using Narwhal.Config;
using Narwhal.Runtime;

namespace Narwhal.Podman;

/// <summary>Wrapper around podman, always invoked with explicit argument lists.</summary>
public sealed class PodmanClient(ProcessRunner runner, Reporter reporter) : IContainerClient
{
    public static string Executable => Environment.GetEnvironmentVariable("NARWHAL_PODMAN") ?? "podman";

    public async Task EnsureNetworkAsync(string project, string network, CancellationToken cancellationToken)
    {
        var exists = await runner.RunAsync(Executable, ["network", "exists", network],
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
        var exists = await runner.RunAsync(Executable, ["volume", "exists", name],
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
            ?? throw new PodmanException($"image '{reference}' was loaded but has no inspectable id");
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
        var result = await runner.RunAsync(Executable, ["image", "exists", reference],
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
        var result = await runner.RunAsync(Executable, ["container", "exists", name],
            new ProcessOptions(), cancellationToken);
        return result.Ok;
    }

    public async Task RemoveContainerAsync(string name, CancellationToken cancellationToken)
    {
        if (!await ContainerExistsAsync(name, cancellationToken))
            return;
        await runner.RunAsync(Executable, ["rm", "--force", "--time", "5", name],
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
            args.AddRange(["--health-retries", service.HealthRetries.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
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
            throw new PodmanException(
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
        using var document = JsonDocument.Parse(result.StdOut.Length == 0 ? "[]" : result.StdOut);
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var name = entry.TryGetProperty("Names", out var names) && names.ValueKind == JsonValueKind.Array
                ? names.EnumerateArray().Select(n => n.GetString() ?? "").FirstOrDefault("")
                : entry.TryGetProperty("Name", out var single) ? single.GetString() ?? "" : "";

            var status = String(entry, "Status");
            containers.Add(new ContainerInfo(
                Name: name,
                State: String(entry, "State"),
                Status: status,
                Image: String(entry, "Image"),
                Ports: FormatPorts(entry),
                Health: HealthFromStatus(status),
                Id: String(entry, "Id")));
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
            args.AddRange(["--tail", count.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (timestamps)
            args.Add("--timestamps");
        args.AddRange(containers);

        await runner.RunAsync(Executable, args, new ProcessOptions { Raw = true }, cancellationToken);
    }

    /// <summary>Push a specific local image id to a destination reference.</summary>
    public async Task PushByIdAsync(string imageId, string destination, CancellationToken cancellationToken)
    {
        reporter.Step($"pushing {destination}");
        await runner.RunAsync(Executable, ["push", imageId, $"docker://{destination}"],
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
        await runner.RunAsync(Executable, ["manifest", "create", "--amend", reference],
            new ProcessOptions { Check = true }, cancellationToken);

        if (imageIds.Count > 0)
        {
            var args = new List<string> { "manifest", "add", reference };
            args.AddRange(imageIds);
            await runner.RunAsync(Executable, args,
                new ProcessOptions { Check = true }, cancellationToken);
        }
    }

    /// <summary>Push every platform in a manifest list and remove the local list on success.</summary>
    public async Task PushManifestAsync(string index, string destination, CancellationToken cancellationToken)
    {
        reporter.Step($"pushing multi-platform manifest {destination}");
        await runner.RunAsync(Executable,
            ["manifest", "push", "--all", "--rm", index, $"docker://{destination}"],
            new ProcessOptions { Check = true, Stream = true }, cancellationToken);
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

    private static string String(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Array => string.Join(",", value.EnumerateArray().Select(v => v.ToString())),
                _ => value.ToString(),
            }
            : "";

    private static string FormatPorts(JsonElement entry)
    {
        if (!entry.TryGetProperty("Ports", out var ports) || ports.ValueKind != JsonValueKind.Array)
            return "";

        var parts = new List<string>();
        foreach (var port in ports.EnumerateArray())
        {
            if (port.ValueKind == JsonValueKind.String)
            {
                parts.Add(port.GetString() ?? "");
                continue;
            }

            var hostIp = String(port, "host_ip");
            var hostPort = String(port, "host_port");
            var containerPort = String(port, "container_port");
            var protocol = String(port, "protocol");
            var prefix = hostIp.Length > 0 ? $"{hostIp}:" : "";
            var mapping = hostPort.Length > 0 ? $"{prefix}{hostPort}->{containerPort}" : containerPort;
            parts.Add(protocol.Length > 0 ? $"{mapping}/{protocol}" : mapping);
        }

        return string.Join(", ", parts.Where(p => p.Length > 0));
    }

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

public sealed class PodmanException(string message, Exception? inner = null) : ContainerException(message, inner);
