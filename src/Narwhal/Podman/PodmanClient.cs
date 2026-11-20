using System.Text.Json;
using Narwhal.Cli;
using Narwhal.Config;

namespace Narwhal.Podman;

public sealed record ContainerInfo(
    string Name,
    string State,
    string Status,
    string Image,
    string Ports,
    string Health,
    string Id);

/// <summary>Wrapper around podman, always invoked with explicit argument lists.</summary>
public sealed class PodmanClient(ProcessRunner runner, Reporter reporter)
{
    public const string ProjectLabel = "io.narwhal.project";
    public const string ServiceLabel = "io.narwhal.service";
    public const string ManifestLabel = "io.narwhal.manifest";
    public const string ImageIdLabel = "io.narwhal.image-id";

    public string Executable => Environment.GetEnvironmentVariable("NARWHAL_PODMAN") ?? "podman";

    public static string ContainerName(string project, string service) => $"{project}-{service}";

    public static string DefaultNetwork(string project) => $"narwhal-{project}";

    public async Task EnsureNetworkAsync(string project, string network, CancellationToken cancellationToken)
    {
        var exists = await runner.RunAsync(Executable, ["network", "exists", network],
            new ProcessOptions(), cancellationToken);
        if (exists.Ok)
            return;

        reporter.Detail($"creating network {network}");
        await runner.RunAsync(Executable,
            ["network", "create", "--label", $"{ProjectLabel}={project}", network],
            new ProcessOptions { Check = true }, cancellationToken);
    }

    public async Task EnsureVolumeAsync(string project, string name, CancellationToken cancellationToken)
    {
        var exists = await runner.RunAsync(Executable, ["volume", "exists", name],
            new ProcessOptions(), cancellationToken);
        if (exists.Ok)
            return;

        await runner.RunAsync(Executable,
            ["volume", "create", "--label", $"{ProjectLabel}={project}", name],
            new ProcessOptions { Check = true }, cancellationToken);
    }

    /// <summary>Load a docker-archive tarball and return the resulting image id.</summary>
    public async Task<string> LoadImageAsync(string artifactPath, string reference, CancellationToken cancellationToken)
    {
        await runner.RunAsync(Executable, ["load", "-i", artifactPath],
            new ProcessOptions { Check = true, Stream = true }, cancellationToken);

        var id = await ImageIdAsync(reference, cancellationToken);
        if (id is null)
        {
            // Nix sets the repository name inside the archive; if the manifest's
            // reference differs, tag it explicitly so later commands can use it.
            await TagAsync(artifactPath, reference, cancellationToken);
            id = await ImageIdAsync(reference, cancellationToken)
                ?? throw new PodmanException($"image '{reference}' was loaded but has no inspectable id");
        }

        return id;
    }

    private async Task TagAsync(string artifactPath, string reference, CancellationToken cancellationToken)
    {
        // `podman load` prints the loaded name; use the image id from the tarball.
        var idResult = await runner.RunAsync(Executable, ["load", "-i", artifactPath],
            new ProcessOptions(), cancellationToken);
        var loaded = idResult.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Contains("Loaded image", StringComparison.OrdinalIgnoreCase));

        if (loaded is null)
            return;

        var name = loaded[(loaded.LastIndexOf(' ') + 1)..].Trim();
        var withoutDigest = name.Split('@')[0];
        if (withoutDigest.Length > 0 && withoutDigest != reference)
            await runner.RunAsync(Executable, ["tag", withoutDigest, reference],
                new ProcessOptions { Check = true }, cancellationToken);
    }

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
            "--label", $"{ProjectLabel}={manifest.Name}",
            "--label", $"{ServiceLabel}={service.Name}",
            "--label", $"{ManifestLabel}={manifest.ContentHash}",
            "--label", $"{ImageIdLabel}={imageId}",
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
            ["ps", "--all", "--filter", $"label={ProjectLabel}={project}", "--format", "json"],
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

    public async Task PushAsync(string reference, CancellationToken cancellationToken)
    {
        reporter.Step($"pushing {reference}");
        await runner.RunAsync(Executable, ["push", reference],
            new ProcessOptions { Check = true, Stream = true }, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ProjectVolumesAsync(string project, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(Executable,
            ["volume", "ls", "--filter", $"label={ProjectLabel}={project}", "--format", "{{.Name}}"],
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

public sealed class PodmanException(string message) : Exception(message);
