using System.Buffers;
using System.Text;
using System.Text.Json;
using Narwhal.Cloud;

namespace Narwhal.Reconcile;

/// <summary>A secret value resolved just before apply. It is never written to state, journal, or status.</summary>
public sealed record ResolvedSecret(string Environment, string PodmanName, string VersionId, string Value);

public interface ISecretResolver
{
    Task<ResolvedSecret> ResolveAsync(string environment, SecretRef reference, string podmanName, CancellationToken cancellationToken);
}

/// <summary>Reads secret material from an absolute local file, referenced as <c>source = "file"</c>.</summary>
public sealed class FileSecretResolver : ISecretResolver
{
    public async Task<ResolvedSecret> ResolveAsync(
        string environment,
        SecretRef reference,
        string podmanName,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(reference.Source, "file", StringComparison.Ordinal))
            throw new ReconcileException(
                "secret_source_unsupported",
                $"service secret '{environment}' uses unsupported source '{reference.Source}'",
                ReconcileExitCode.Runtime);

        if (!Path.IsPathRooted(reference.Id))
            throw new ReconcileException(
                "secret_resolution_failed",
                $"service secret '{environment}' must use an absolute file path",
                ReconcileExitCode.Runtime);

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(reference.Id, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ReconcileException(
                "secret_resolution_failed",
                $"cannot read service secret '{environment}' from {reference.Id}: {e.Message}",
                ReconcileExitCode.Runtime);
        }

        var text = reference.Key is null
            ? Decode(bytes, environment)
            : DecodeJsonKey(bytes, reference.Key, environment);

        var digest = CloudEncoding.Sha256Digest(Encoding.UTF8.GetBytes(text));
        return new ResolvedSecret(environment, podmanName, digest, text);
    }

    private static string Decode(byte[] bytes, string environment)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new ReconcileException(
                "secret_resolution_failed",
                $"service secret '{environment}' is not valid UTF-8",
                ReconcileExitCode.Runtime);
        }
    }

    private static string DecodeJsonKey(byte[] bytes, string key, string environment)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(key, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                throw new ReconcileException(
                    "secret_resolution_failed",
                    $"service secret '{environment}' has no string key '{key}'",
                    ReconcileExitCode.Runtime);
            }

            return value.GetString() ?? "";
        }
        catch (JsonException e)
        {
            throw new ReconcileException(
                "secret_resolution_failed",
                $"service secret '{environment}' is not valid JSON: {e.Message}",
                ReconcileExitCode.Runtime);
        }
    }
}

/// <summary>A service with its image, resolved secrets, and canonical config hash.</summary>
public sealed class ServicePlan
{
    public required string Name { get; init; }
    public required ServiceSpec Spec { get; init; }
    public required ImageSpec Image { get; init; }
    public required string ImageReference { get; init; }
    public required string ConfigHash { get; init; }
    public required IReadOnlyList<ResolvedSecret> Secrets { get; init; }
}

public static class ServicePlanner
{
    /// <summary>Builds plans in dependency order and resolves every referenced secret.</summary>
    public static async Task<IReadOnlyList<ServicePlan>> BuildAsync(
        DesiredDocument document,
        ISecretResolver secrets,
        CancellationToken cancellationToken)
    {
        var ordered = Order(document.Services);
        var plans = new List<ServicePlan>(ordered.Count);
        foreach (var name in ordered)
        {
            var service = document.Services[name];
            var image = document.Images[service.Image];
            var reference = $"{image.Repository}@{service.Image}";

            var resolved = new List<ResolvedSecret>();
            if (service.Secrets is not null)
            {
                foreach (var environment in service.Secrets.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    var podmanName = $"{document.Project}-{name}-{environment}";
                    resolved.Add(await secrets.ResolveAsync(environment, service.Secrets[environment], podmanName, cancellationToken));
                }
            }

            plans.Add(new ServicePlan
            {
                Name = name,
                Spec = service,
                Image = image,
                ImageReference = reference,
                ConfigHash = ConfigHash(service, image, resolved),
                Secrets = resolved,
            });
        }

        return plans;
    }

    /// <summary>Topological service order with lexical tie-breaking, dependencies first.</summary>
    public static IReadOnlyList<string> Order(IReadOnlyDictionary<string, ServiceSpec> services)
    {
        var ordered = new List<string>(services.Count);
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 visiting, 2 done

        void Visit(string name)
        {
            if (state.TryGetValue(name, out var current))
            {
                if (current == 2)
                    return;
                throw new ReconcileException(
                    "semantic_invalid",
                    $"dependency cycle detected at service '{name}'",
                    ReconcileExitCode.Verification);
            }

            state[name] = 1;
            foreach (var dependency in services[name].DependsOn.OrderBy(d => d, StringComparer.Ordinal))
            {
                if (services.ContainsKey(dependency))
                    Visit(dependency);
            }
            state[name] = 2;
            ordered.Add(name);
        }

        foreach (var name in services.Keys.OrderBy(k => k, StringComparer.Ordinal))
            Visit(name);

        return ordered;
    }

    /// <summary>Deterministic hash of the service spec plus resolved secret version identifiers.</summary>
    public static string ConfigHash(ServiceSpec service, ImageSpec image, IReadOnlyList<ResolvedSecret> secrets)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("image", image.Repository + "@" + service.Image);
            WriteArray(writer, "command", service.Command);
            WriteString(writer, "entrypoint", service.Entrypoint);
            WriteMap(writer, "env", service.Env);
            WriteSecrets(writer, secrets);
            WriteArray(writer, "ports", service.Ports);
            WriteArray(writer, "volumes", service.Volumes);
            WriteHealth(writer, service.Health);
            WriteArray(writer, "depends_on", service.DependsOn);
            writer.WriteString("restart", service.Restart ?? "unless-stopped");
            WriteString(writer, "user", service.User);
            WriteString(writer, "workdir", service.Workdir);
            if (service.StopTimeout is { } timeout)
                writer.WriteNumber("stop_timeout", timeout);
            writer.WriteEndObject();
        }

        var canonical = CanonicalJson.Canonicalize(buffer.WrittenSpan);
        return CloudEncoding.Sha256Digest(canonical);
    }

    private static void WriteSecrets(Utf8JsonWriter writer, IReadOnlyList<ResolvedSecret> secrets)
    {
        writer.WriteStartObject("secrets");
        foreach (var secret in secrets.OrderBy(s => s.Environment, StringComparer.Ordinal))
        {
            writer.WriteStartObject(secret.Environment);
            writer.WriteString("version_id", secret.VersionId);
            writer.WriteString("podman_name", secret.PodmanName);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteHealth(Utf8JsonWriter writer, HealthSpec? health)
    {
        if (health is null)
        {
            writer.WriteNull("health");
            return;
        }

        writer.WriteStartObject("health");
        writer.WriteString("command", health.Command);
        writer.WriteNumber("interval", health.Interval);
        writer.WriteNumber("timeout", health.Timeout);
        writer.WriteNumber("retries", health.Retries);
        if (health.StartPeriod is { } period)
            writer.WriteNumber("start_period", period);
        writer.WriteEndObject();
    }

    private static void WriteMap(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartObject(name);
        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            writer.WriteString(key, value);
        writer.WriteEndObject();
    }

    private static void WriteArray(Utf8JsonWriter writer, string name, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartArray(name);
        foreach (var value in values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }
}
