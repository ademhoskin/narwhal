using System.Text.Json;
using Narwhal.Cli;
using Narwhal.Config;
using Narwhal.Podman;

namespace Narwhal.Commands;

/// <summary>up, down, ps and logs: the podman-facing commands.</summary>
public static class RunCommands
{
    public static async Task<int> UpAsync(CommandContext ctx, CliOptions options, CancellationToken cancellationToken)
    {
        var manifest = ctx.Manifest;
        var reporter = ctx.Reporter;
        var selected = ctx.Select(options.Positionals);

        if (!manifest.Build.Systems.Contains(ctx.HostSystem, StringComparer.Ordinal))
            throw new UsageException(
                $"the host system '{ctx.HostSystem}' is not in build.systems; configured systems: {string.Join(", ", manifest.Build.Systems)}\n" +
                $"add '{ctx.HostSystem}' to build.systems to run services on this machine");

        // Starting a service without its dependencies is almost never what anyone wants.
        var selectedNames = new HashSet<string>(selected.Select(s => s.Name), StringComparer.Ordinal);
        foreach (var service in selected)
        {
            foreach (var dependency in service.DependsOn)
                selectedNames.Add(dependency);
        }

        var services = manifest.Ordered().Where(s => selectedNames.Contains(s.Name)).ToList();

        var noBuild = options.Flag("no-build");
        var rebuild = options.Flag("build");
        if (noBuild && rebuild)
            throw new UsageException("--build and --no-build cannot be combined");

        var waitSeconds = options.IntValue("wait", 0, 3600) ?? manifest.Deploy.WaitSeconds;
        var imageIds = new Dictionary<string, string>(StringComparer.Ordinal);

        if (noBuild)
        {
            foreach (var service in services.Where(s => !s.Prebuilt))
            {
                var reference = service.Reference(manifest);
                if (!await ctx.Podman.ImageExistsAsync(reference, cancellationToken))
                    throw new PodmanException($"image '{reference}' is not loaded; run 'narwhal build' first");
                imageIds[service.Name] = await ctx.Podman.ImageIdAsync(reference, cancellationToken) ?? reference;
            }
        }
        else
        {
            imageIds = await BuildCommands.EnsureImagesAsync(ctx, services, rebuild, cancellationToken);
        }

        reporter.Step($"bringing up {manifest.Name} ({services.Count} service(s))");
        await ctx.Podman.EnsureNetworkAsync(manifest.Name, ctx.Network, cancellationToken);

        foreach (var volume in services.SelectMany(s => s.Volumes).Distinct(StringComparer.Ordinal))
        {
            if (TryNamedVolume(volume, out var name))
                await ctx.Podman.EnsureVolumeAsync(manifest.Name, name, cancellationToken);
        }

        foreach (var service in services)
        {
            await WaitForDependenciesAsync(ctx, service, waitSeconds, cancellationToken);

            var container = ctx.ContainerName(service);
            await ctx.Podman.RemoveContainerAsync(container, cancellationToken);

            var imageId = imageIds.TryGetValue(service.Name, out var id) ? id : "";
            await ctx.Podman.RunAsync(manifest, service, ctx.Network, container,
                service.Reference(manifest), imageId, cancellationToken);

            if (service.Healthcheck is not null)
                await WaitForHealthAsync(ctx, service, cancellationToken);
            else
                reporter.Ok($"{service.Name} started as {container}");
        }

        return await PsAsync(ctx, cancellationToken);
    }

    public static async Task<int> DownAsync(CommandContext ctx, CliOptions options, CancellationToken cancellationToken)
    {
        var reporter = ctx.Reporter;
        var containers = await ctx.Podman.ListAsync(ctx.Manifest.Name, cancellationToken);
        foreach (var container in containers)
            await ctx.Podman.RemoveContainerAsync(container.Name, cancellationToken);

        var removedVolumes = 0;
        if (options.Flag("volumes"))
        {
            foreach (var volume in await ctx.Podman.ProjectVolumesAsync(ctx.Manifest.Name, cancellationToken))
            {
                await ctx.Podman.RemoveVolumeAsync(volume, cancellationToken);
                removedVolumes++;
            }
        }

        if (reporter.Json)
        {
            await BuildCommands.WriteJsonAsync(reporter, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("project", ctx.Manifest.Name);
                writer.WriteNumber("containers_removed", containers.Count);
                writer.WriteNumber("volumes_removed", removedVolumes);
                writer.WriteEndObject();
            });
        }
        else if (containers.Count == 0)
        {
            reporter.Ok("nothing to stop");
        }
        else
        {
            reporter.Ok($"stopped {containers.Count} container(s)" +
                (removedVolumes > 0 ? $" and removed {removedVolumes} volume(s)" : ""));
        }

        return 0;
    }

    public static async Task<int> PsAsync(CommandContext ctx, CancellationToken cancellationToken)
    {
        var reporter = ctx.Reporter;
        var containers = await ctx.Podman.ListAsync(ctx.Manifest.Name, cancellationToken);
        var byName = containers.ToDictionary(c => c.Name, StringComparer.Ordinal);

        var rows = new List<IReadOnlyList<string>>();
        foreach (var service in ctx.Manifest.Ordered())
        {
            var container = ctx.ContainerName(service);
            if (byName.TryGetValue(container, out var info))
            {
                rows.Add([service.Name, info.State, info.Health.Length > 0 ? info.Health : "-",
                    info.Ports.Length > 0 ? info.Ports : "-", info.Image]);
            }
            else
            {
                rows.Add([service.Name, "not created", "-", "-", "-"]);
            }
        }

        if (reporter.Json)
        {
            await BuildCommands.WriteJsonAsync(reporter, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("project", ctx.Manifest.Name);
                writer.WriteString("network", ctx.Network);
                writer.WriteStartArray("services");
                foreach (var service in ctx.Manifest.Ordered())
                {
                    byName.TryGetValue(ctx.ContainerName(service), out var info);
                    writer.WriteStartObject();
                    writer.WriteString("name", service.Name);
                    writer.WriteString("container", ctx.ContainerName(service));
                    writer.WriteString("state", info?.State ?? "not created");
                    writer.WriteString("health", info?.Health ?? "");
                    writer.WriteString("ports", info?.Ports ?? "");
                    writer.WriteString("image_id", info?.Id ?? "");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
            return 0;
        }

        reporter.Table(["service", "state", "health", "ports", "image"], rows);
        return 0;
    }

    public static async Task<int> LogsAsync(CommandContext ctx, CliOptions options, CancellationToken cancellationToken)
    {
        var services = ctx.Select(options.Positionals);
        var containers = new List<string>();
        foreach (var service in services)
        {
            var container = ctx.ContainerName(service);
            if (!await ctx.Podman.ContainerExistsAsync(container, cancellationToken))
                throw new PodmanException($"'{service.Name}' has no container; run 'narwhal up' first");
            containers.Add(container);
        }

        await ctx.Podman.LogsAsync(containers, options.Flag("follow"),
            options.IntValue("tail"), options.Flag("timestamps"), cancellationToken);
        return 0;
    }

    private static async Task WaitForDependenciesAsync(CommandContext ctx, ServiceSpec service, int waitSeconds,
        CancellationToken cancellationToken)
    {
        foreach (var dependency in service.DependsOn)
        {
            var dependencySpec = ctx.Manifest.Find(dependency)!;
            var container = ctx.ContainerName(dependencySpec);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(waitSeconds);

            while (true)
            {
                var health = await ctx.Podman.HealthAsync(container, cancellationToken);
                var state = await ctx.Podman.StateAsync(container, cancellationToken);

                if (health == "healthy" || (health is null && state == "running"))
                    break;
                if (health == "unhealthy" || state is "exited" or "dead")
                    throw new PodmanException($"dependency '{dependency}' is {health ?? state}; not starting '{service.Name}'");
                if (DateTimeOffset.UtcNow > deadline)
                    throw new PodmanException($"timed out waiting for dependency '{dependency}' to become ready");

                await Task.Delay(500, cancellationToken);
            }
        }
    }

    private static async Task WaitForHealthAsync(CommandContext ctx, ServiceSpec service, CancellationToken cancellationToken)
    {
        var container = ctx.ContainerName(service);
        var timeout = TimeSpan.FromSeconds(Math.Max(service.HealthRetries * service.HealthInterval, 5));
        var deadline = DateTimeOffset.UtcNow + timeout;
        var reported = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var health = await ctx.Podman.HealthAsync(container, cancellationToken);
            if (health == "healthy")
            {
                ctx.Reporter.Ok($"{service.Name} healthy as {container}");
                return;
            }

            if (health == "unhealthy")
                throw new PodmanException($"'{service.Name}' failed its healthcheck; see 'narwhal logs {service.Name}'");

            if (!reported)
            {
                ctx.Reporter.Info($"waiting for {service.Name} to become healthy");
                reported = true;
            }

            await Task.Delay(1000, cancellationToken);
        }

        throw new PodmanException($"timed out after {timeout.TotalSeconds:0}s waiting for '{service.Name}' to become healthy");
    }

    /// <summary>volume specs look like name:/path or /path, or name:/path:opts.</summary>
    private static bool TryNamedVolume(string spec, out string name)
    {
        name = "";
        var parts = spec.Split(':');
        if (parts.Length < 2)
            return false;

        var source = parts[0];
        if (source.Length == 0 || source.StartsWith('/') || source.StartsWith('.') || source.StartsWith('~'))
            return false;

        name = source;
        return true;
    }

    private static string ShortId(string id)
    {
        var value = id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id;
        return value.Length <= 12 ? value : value[..12];
    }
}
