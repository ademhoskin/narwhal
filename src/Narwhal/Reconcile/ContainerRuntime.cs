using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Narwhal.Cli;
using Narwhal.Podman;

namespace Narwhal.Reconcile;

public sealed record MountObservation(string Type, string Source, string Destination, bool ReadOnly);

/// <summary>Observed podman container state used by the diff. Podman, not a local model, is authoritative.</summary>
public sealed class ContainerObservation
{
    public required string Name { get; init; }
    public required IReadOnlyDictionary<string, string> Labels { get; init; }
    public required string State { get; init; }
    public required string ImageReference { get; init; }
    public required string RestartPolicy { get; init; }
    public required IReadOnlyList<string> Environment { get; init; }
    public required IReadOnlyList<string> Ports { get; init; }
    public required IReadOnlyList<MountObservation> Mounts { get; init; }
    public string? Health { get; init; }

    public string Project => Label(ReconcileLabels.Project) ?? "";
    public string Service => Label(ReconcileLabels.Service) ?? "";

    public string? Label(string name) => Labels.TryGetValue(name, out var value) ? value : null;
}

public static class ReconcileLabels
{
    public const string Project = "io.narwhal.project";
    public const string Service = "io.narwhal.service";
    public const string Generation = "io.narwhal.generation";
    public const string ConfigHash = "io.narwhal.config-hash";
    public const string Image = "io.narwhal.image";
}

public static class ReconcileNames
{
    public static string Container(string project, string service) => $"narwhal-{project}-{service}";
}

public interface IContainerRuntime
{
    Task EnsureNetworkAsync(string project, string network, CancellationToken cancellationToken);
    Task EnsureVolumeAsync(string project, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContainerObservation>> ListAsync(string project, CancellationToken cancellationToken);
    Task<bool> ImageMatchesDigestAsync(string imageReference, string repository, string digest, CancellationToken cancellationToken);
    Task PullAsync(string reference, CancellationToken cancellationToken);
    Task CreateAsync(string project, ServicePlan plan, string containerName, string network, long generation, CancellationToken cancellationToken);
    Task StartAsync(string containerName, CancellationToken cancellationToken);
    Task StopAsync(string containerName, CancellationToken cancellationToken);
    Task RemoveAsync(string containerName, CancellationToken cancellationToken);
    Task EnsureSecretAsync(string name, string value, CancellationToken cancellationToken);
    Task<string?> StateAsync(string containerName, CancellationToken cancellationToken);
    Task<string?> HealthAsync(string containerName, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> LogsTailAsync(string containerName, int lines, CancellationToken cancellationToken);
}

/// <summary>Podman implementation of the reconcile runtime.</summary>
public sealed class PodmanRuntime(
    ProcessRunner runner,
    Reporter reporter,
    string? executable = null,
    ImagePullAuth? imageAuth = null,
    string? authFileDirectory = null) : IContainerRuntime
{
    private readonly string? _executable = executable;
    private readonly ImagePullAuth? _imageAuth = imageAuth;
    private readonly string? _authFileDirectory = authFileDirectory;

    public string Executable => _executable ?? Environment.GetEnvironmentVariable("NARWHAL_PODMAN") ?? "podman";

    public async Task EnsureNetworkAsync(string project, string network, CancellationToken cancellationToken)
    {
        var exists = await runner.RunAsync(Executable, ["network", "exists", network], new ProcessOptions(), cancellationToken);
        if (exists.Ok)
            return;
        await runner.RunAsync(
            Executable,
            ["network", "create", "--label", $"{ReconcileLabels.Project}={project}", network],
            new ProcessOptions { Check = true },
            cancellationToken);
    }

    public async Task EnsureVolumeAsync(string project, string name, CancellationToken cancellationToken)
    {
        var exists = await runner.RunAsync(Executable, ["volume", "exists", name], new ProcessOptions(), cancellationToken);
        if (exists.Ok)
            return;
        await runner.RunAsync(
            Executable,
            ["volume", "create", "--label", $"{ReconcileLabels.Project}={project}", name],
            new ProcessOptions { Check = true },
            cancellationToken);
    }

    public async Task<IReadOnlyList<ContainerObservation>> ListAsync(string project, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            Executable,
            ["ps", "--all", "--filter", $"label={ReconcileLabels.Project}={project}", "--format", "json"],
            new ProcessOptions { Check = true },
            cancellationToken);

        var names = new List<string>();
        using (var document = JsonDocument.Parse(result.StdOut.Length == 0 ? "[]" : result.StdOut))
        {
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var name = entry.TryGetProperty("Names", out var namesElement) && namesElement.ValueKind == JsonValueKind.Array
                    ? namesElement.EnumerateArray().Select(n => n.GetString() ?? "").FirstOrDefault("")
                    : entry.TryGetProperty("Name", out var single) ? single.GetString() ?? "" : "";
                if (name.Length > 0)
                    names.Add(name);
            }
        }

        var observations = new List<ContainerObservation>(names.Count);
        foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
            observations.Add(await InspectAsync(name, cancellationToken));
        return observations;
    }

    private async Task<ContainerObservation> InspectAsync(string name, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            Executable,
            ["container", "inspect", name],
            new ProcessOptions { Check = true },
            cancellationToken);
        using var document = JsonDocument.Parse(result.StdOut);
        var root = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement[0]
            : document.RootElement;

        var config = Property(root, "Config");
        var state = Property(root, "State");
        var hostConfig = Property(root, "HostConfig");

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config.TryGetProperty("Labels", out var labelsElement) && labelsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in labelsElement.EnumerateObject())
                labels[label.Name] = label.Value.GetString() ?? "";
        }

        var environment = new List<string>();
        if (config.TryGetProperty("Env", out var envElement) && envElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in envElement.EnumerateArray())
            {
                if (item.GetString() is { } value)
                    environment.Add(value);
            }
        }

        var ports = new List<string>();
        if (hostConfig.TryGetProperty("PortBindings", out var bindings) && bindings.ValueKind == JsonValueKind.Object)
        {
            foreach (var binding in bindings.EnumerateObject())
            {
                var slash = binding.Name.LastIndexOf('/');
                var containerPort = slash >= 0 ? binding.Name[..slash] : binding.Name;
                var protocol = slash >= 0 ? binding.Name[(slash + 1)..] : "tcp";
                if (binding.Value.ValueKind != JsonValueKind.Array || binding.Value.GetArrayLength() == 0)
                {
                    ports.Add(PortKey(containerPort, "*", protocol));
                    continue;
                }

                foreach (var host in binding.Value.EnumerateArray())
                {
                    var hostPort = host.TryGetProperty("HostPort", out var value) ? value.GetString() ?? "*" : "*";
                    ports.Add(PortKey(containerPort, hostPort.Length == 0 ? "*" : hostPort, protocol));
                }
            }
        }

        var mounts = new List<MountObservation>();
        if (root.TryGetProperty("Mounts", out var mountsElement) && mountsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var mount in mountsElement.EnumerateArray())
            {
                var type = String(mount, "Type");
                var source = String(mount, "Name") is { Length: > 0 } volume ? volume : String(mount, "Source");
                var destination = String(mount, "Destination");
                var readOnly = mount.TryGetProperty("RW", out var rw) && !rw.GetBoolean();
                mounts.Add(new MountObservation(type, source, destination, readOnly));
            }
        }

        var health = string.Empty;
        if (state.TryGetProperty("Health", out var healthElement) && healthElement.ValueKind == JsonValueKind.Object)
            health = String(healthElement, "Status");

        var rawName = String(root, "Name");
        return new ContainerObservation
        {
            Name = rawName.StartsWith('/') ? rawName[1..] : rawName,
            Labels = labels,
            State = String(state, "Status"),
            ImageReference = String(config, "Image"),
            RestartPolicy = hostConfig.TryGetProperty("RestartPolicy", out var restart) ? String(restart, "Name") : "",
            Environment = environment,
            Ports = ports,
            Mounts = mounts,
            Health = health.Length == 0 ? null : health,
        };
    }

    public async Task<bool> ImageMatchesDigestAsync(
        string imageReference,
        string repository,
        string digest,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            Executable,
            ["image", "inspect", "--format", "{{json .}}", imageReference],
            new ProcessOptions(),
            cancellationToken);
        if (!result.Ok)
            return false;

        using var document = JsonDocument.Parse(result.StdOut);
        var root = document.RootElement;
        var expected = $"{repository}@{digest}";
        if (root.TryGetProperty("RepoDigests", out var digests) && digests.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in digests.EnumerateArray())
            {
                if (string.Equals(entry.GetString(), expected, StringComparison.Ordinal))
                    return true;
            }
        }

        return root.TryGetProperty("Digest", out var local) &&
               string.Equals(local.GetString(), digest, StringComparison.Ordinal);
    }

    public async Task PullAsync(string reference, CancellationToken cancellationToken)
    {
        reporter.Step($"pulling {reference}");
        string? authFile = null;
        try
        {
            authFile = await CreateAuthFileAsync(cancellationToken);
            var args = new List<string> { "pull" };
            if (authFile is not null)
                args.AddRange(["--authfile", authFile]);
            args.Add(reference);

            var result = await runner.RunAsync(
                Executable,
                args,
                new ProcessOptions { Stream = true },
                cancellationToken);
            if (!result.Ok)
                throw new PodmanException($"failed to pull {reference}:\n{result.Combined.TrimEnd()}");
        }
        finally
        {
            if (authFile is not null)
                TryDelete(authFile);
        }
    }

    private async Task<string> CreateAuthFileAsync(CancellationToken cancellationToken)
    {
        var directory = _authFileDirectory ?? DefaultAuthFileDirectory();
        SweepStaleAuthFiles(directory);
        var credential = _imageAuth is null
            ? null
            : await RegistryCredentialsResolver.ResolveAsync(
                _imageAuth.Auth,
                "https://" + _imageAuth.Registry,
                cancellationToken);

        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var path = Path.Combine(directory, $"auth-{Environment.ProcessId}-{Guid.NewGuid():n}.json");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(path, options))
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteStartObject("auths");
                    if (_imageAuth is not null && credential is not null)
                    {
                        writer.WriteStartObject(_imageAuth.Registry);
                        if (!string.IsNullOrEmpty(_imageAuth.Auth.Token) && _imageAuth.Auth.Username is null)
                            writer.WriteString("identitytoken", _imageAuth.Auth.Token);
                        else
                            writer.WriteString("auth", RegistryCredentialsResolver.BasicToken(credential.Username, credential.Secret));
                        writer.WriteEndObject();
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                stream.Flush(flushToDisk: true);
            }

            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private static readonly Regex AuthFileName = new(
        "^auth-(?<pid>[0-9]+)-[0-9a-f]+\\.json$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static void SweepStaleAuthFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var path in Directory.EnumerateFiles(directory, "auth-*.json"))
        {
            var match = AuthFileName.Match(Path.GetFileName(path));
            if (!match.Success ||
                !int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
                pid == Environment.ProcessId ||
                IsProcessAlive(pid, path))
                continue;

            TryDelete(path);
        }
    }

    private static bool IsProcessAlive(int pid, string path) =>
        OperatingSystem.IsLinux()
            ? Directory.Exists("/proc/" + pid.ToString(CultureInfo.InvariantCulture))
            : File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddHours(-1);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string DefaultAuthFileDirectory()
    {
        var uid = Unix.GetEffectiveUserId();
        var runtime = "/run/user/" + uid.ToString(CultureInfo.InvariantCulture);
        return Directory.Exists(runtime)
            ? Path.Combine(runtime, "narwhal")
            : Path.Combine(Path.GetTempPath(), "narwhal-" + uid.ToString(CultureInfo.InvariantCulture));
    }

    public async Task CreateAsync(
        string project,
        ServicePlan plan,
        string containerName,
        string network,
        long generation,
        CancellationToken cancellationToken)
    {
        var service = plan.Spec;
        var args = new List<string>
        {
            "run",
            "--detach",
            "--pull=never",
            "--name", containerName,
            "--network", network,
            "--restart", service.Restart ?? "unless-stopped",
            "--label", $"{ReconcileLabels.Project}={project}",
            "--label", $"{ReconcileLabels.Service}={plan.Name}",
            "--label", $"{ReconcileLabels.Generation}={generation.ToString(CultureInfo.InvariantCulture)}",
            "--label", $"{ReconcileLabels.ConfigHash}={plan.ConfigHash}",
            "--label", $"{ReconcileLabels.Image}={plan.Spec.Image}",
        };

        if (service.StopTimeout is { } stopTimeout)
            args.AddRange(["--stop-timeout", stopTimeout.ToString(CultureInfo.InvariantCulture)]);

        foreach (var port in service.Ports)
            args.AddRange(["--publish", port]);

        if (service.Env is not null)
        {
            foreach (var (key, value) in service.Env.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                args.AddRange(["--env", $"{key}={value}"]);
        }

        foreach (var secret in plan.Secrets)
            args.AddRange(["--secret", $"{secret.PodmanName},type=env,target={secret.Environment}"]);

        foreach (var volume in service.Volumes)
            args.AddRange(["--volume", volume]);

        if (service.User is not null)
            args.AddRange(["--user", service.User]);
        if (service.Workdir is not null)
            args.AddRange(["--workdir", service.Workdir]);
        if (service.Entrypoint is not null)
            args.AddRange(["--entrypoint", service.Entrypoint]);

        if (service.Health is { } health)
        {
            args.AddRange(["--health-cmd", health.Command]);
            args.AddRange(["--health-interval", health.Interval.ToString(CultureInfo.InvariantCulture) + "s"]);
            args.AddRange(["--health-timeout", health.Timeout.ToString(CultureInfo.InvariantCulture) + "s"]);
            args.AddRange(["--health-retries", health.Retries.ToString(CultureInfo.InvariantCulture)]);
            if (health.StartPeriod is { } startPeriod)
                args.AddRange(["--health-start-period", startPeriod.ToString(CultureInfo.InvariantCulture) + "s"]);
        }

        args.Add(plan.ImageReference);
        if (service.Command is not null)
            args.AddRange(service.Command);

        reporter.Detail($"starting {containerName} from {plan.ImageReference}");
        var result = await runner.RunAsync(Executable, args, new ProcessOptions { Stream = true }, cancellationToken);
        if (!result.Ok)
            throw new PodmanException($"failed to start '{plan.Name}':\n{result.Combined.TrimEnd()}");
    }

    public async Task EnsureSecretAsync(string name, string value, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            Executable,
            ["secret", "create", "--replace", name, "-"],
            new ProcessOptions { Input = value },
            cancellationToken);
        if (!result.Ok)
            throw new PodmanException($"failed to store podman secret '{name}'");
    }

    public async Task<IReadOnlyList<string>> LogsTailAsync(
        string containerName,
        int lines,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            Executable,
            ["logs", "--tail", lines.ToString(CultureInfo.InvariantCulture), containerName],
            new ProcessOptions(),
            cancellationToken);
        if (!result.Ok)
            throw new PodmanException($"failed to read logs for '{containerName}':\n{result.Combined.TrimEnd()}");

        return result.Combined
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    public async Task StartAsync(string containerName, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(Executable, ["start", containerName], new ProcessOptions(), cancellationToken);
        if (!result.Ok)
            throw new PodmanException($"failed to start '{containerName}':\n{result.Combined.TrimEnd()}");
    }

    public async Task StopAsync(string containerName, CancellationToken cancellationToken)
    {
        await runner.RunAsync(Executable, ["stop", "--time", "10", containerName], new ProcessOptions(), cancellationToken);
    }

    public async Task RemoveAsync(string containerName, CancellationToken cancellationToken)
    {
        await runner.RunAsync(Executable, ["rm", "--force", "--time", "5", containerName], new ProcessOptions(), cancellationToken);
    }

    public async Task<string?> StateAsync(string containerName, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            Executable,
            ["inspect", "--format", "{{.State.Status}}", containerName],
            new ProcessOptions(),
            cancellationToken);
        return result.Ok ? result.StdOut.Trim() : null;
    }

    public async Task<string?> HealthAsync(string containerName, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            Executable,
            ["inspect", "--format", "{{if .State.Health}}{{.State.Health.Status}}{{end}}", containerName],
            new ProcessOptions(),
            cancellationToken);
        if (!result.Ok)
            return null;
        var value = result.StdOut.Trim();
        return value.Length == 0 ? null : value;
    }

    internal static string PortKey(string containerPort, string hostPort, string protocol) =>
        $"{containerPort}/{protocol}:{hostPort}";

    private static JsonElement Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static string String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

/// <summary>Credentials used for podman image pulls, separate from state-registry credentials.</summary>
public sealed record ImagePullAuth(string Registry, RegistryAuthConfig Auth);

internal static class Unix
{
    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern uint geteuid();

    public static uint GetEffectiveUserId() => geteuid();
}
