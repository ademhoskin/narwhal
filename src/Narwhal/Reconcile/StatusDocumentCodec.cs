using System.Text;
using System.Text.Json;
using Narwhal.Cloud;

namespace Narwhal.Reconcile;

/// <summary>Canonical <c>narwhal.status/v1</c> encoding and strict validation.</summary>
public static class StatusDocumentCodec
{
    public const string SchemaName = ReconcileSchema.Status;
    public const int SchemaVersion = 1;

    private static readonly string[] Phases =
    [
        ReconcilePhases.Idle, ReconcilePhases.Pulling, ReconcilePhases.Applying,
        ReconcilePhases.Verifying, ReconcilePhases.RollingBack, ReconcilePhases.Degraded,
        ReconcilePhases.Failed, ReconcilePhases.ReplayRejected, ReconcilePhases.AuthFailed,
        ReconcilePhases.RegistryUnavailable,
    ];

    private static readonly string[] ErrorCodes =
    [
        "agent_upgrade_failed", "agent_upgrade_blocked", "json_invalid", "non_canonical",
        "schema_invalid", "semantic_invalid", "signature_invalid", "unknown_key", "key_expired",
        "trust_set_invalid", "target_mismatch", "project_mismatch", "generation_replay",
        "generation_conflict", "generation_blocked", "layer_invalid", "context_invalid",
        "digest_mismatch", "image_pull_failed", "image_digest_mismatch", "apply_failed",
        "health_failed", "health_timeout", "health_unhealthy", "health_exited", "rollback_failed",
        "registry_unavailable", "auth_failed", "cache_mismatch", "previous_unavailable",
        "bind_mount_missing", "state_io_failed", "status_io_failed", "secret_source_unsupported",
        "secret_resolution_failed",
    ];

    /// <summary>Encodes the document as canonical UTF-8 JSON, including explicit null fields.</summary>
    public static byte[] Encode(ReconcileStatusDocument status)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", SchemaName);
            writer.WriteNumber("version", SchemaVersion);
            writer.WriteString("project", status.Project);
            WriteTarget(writer, status.Target);
            writer.WriteNumber("status_seq", status.StatusSeq);
            writer.WriteNumber("epoch", status.Epoch);
            writer.WriteString("updated", status.Updated);
            WriteHost(writer, status.Host);
            writer.WriteNumber("highest_generation", status.HighestGeneration);
            WriteNullableNumber(writer, "blocked_generation", status.BlockedGeneration);
            writer.WriteString("phase", status.Phase);
            WriteObserved(writer, status.Observed);
            WriteActive(writer, status.Active);
            WriteAttempts(writer, status.Attempts);
            WriteServices(writer, status.Services);
            WriteLastError(writer, status.LastError);
            WriteLogsTail(writer, status.LogsTail);
            writer.WriteEndObject();
        }

        return CanonicalJson.Canonicalize(buffer.ToArray());
    }

    /// <summary>Parses canonical status bytes, rejecting unknown fields, malformed values, and non-canonical bytes.</summary>
    /// <exception cref="CloudArtifactException">The document is not a valid canonical status document.</exception>
    public static ReconcileStatusDocument Decode(ReadOnlySpan<byte> utf8)
    {
        JsonDocument document;
        JsonElement root;
        try
        {
            document = StrictJson.Parse(utf8);
            root = document.RootElement;
        }
        catch (CloudArtifactException)
        {
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.JsonInvalid,
                "status payload is not strict JSON");
        }

        using var ownedDocument = document;

        if (!CanonicalJsonWriter.Write(root).AsSpan().SequenceEqual(utf8))
            throw new CloudArtifactException(CloudArtifactErrorCodes.NonCanonical, "status payload bytes are not canonical");

        var obj = JsonShape.Object(root, "status");
        JsonShape.Members(
            obj,
            "status",
            CloudArtifactErrorCodes.SchemaInvalid,
            "schema", "version", "project", "target", "status_seq", "epoch", "updated", "host",
            "highest_generation", "blocked_generation", "phase", "observed", "active", "attempts",
            "services", "last_error", "logs_tail");

        var schema = JsonShape.String(JsonShape.Required(obj, "status", "schema"), "status.schema");
        if (!string.Equals(schema, SchemaName, StringComparison.Ordinal))
            throw Invalid("status.schema", "unknown schema discriminator");

        var version = JsonShape.Integer(JsonShape.Required(obj, "status", "version"), "status.version", 0, long.MaxValue);
        if (version != SchemaVersion)
            throw Invalid("status.version", "unknown schema version");

        var project = JsonShape.String(JsonShape.Required(obj, "status", "project"), "status.project");
        if (!CloudPatterns.ProjectName().IsMatch(project))
            throw Invalid("status.project", "invalid project name");

        var status = new ReconcileStatusDocument
        {
            Project = project,
            Target = ParseTarget(JsonShape.Required(obj, "status", "target"), "status.target"),
            StatusSeq = JsonShape.Integer(JsonShape.Required(obj, "status", "status_seq"), "status.status_seq", 0, MaxSafeInteger),
            Epoch = JsonShape.Integer(JsonShape.Required(obj, "status", "epoch"), "status.epoch", 0, MaxSafeInteger),
            Updated = Timestamp(JsonShape.Required(obj, "status", "updated"), "status.updated"),
            Host = ParseHost(JsonShape.Required(obj, "status", "host"), "status.host"),
            HighestGeneration = JsonShape.Integer(
                JsonShape.Required(obj, "status", "highest_generation"),
                "status.highest_generation",
                0,
                MaxSafeInteger),
        };

        if (JsonShape.Optional(obj, "blocked_generation", out var blocked) && !JsonShape.IsNull(blocked))
        {
            status.BlockedGeneration = JsonShape.Integer(blocked, "status.blocked_generation", 1, MaxSafeInteger);
            if (status.BlockedGeneration > status.HighestGeneration)
                throw Invalid("status.blocked_generation", "must not exceed highest_generation");
        }

        status.Phase = JsonShape.String(JsonShape.Required(obj, "status", "phase"), "status.phase");
        if (!Phases.Contains(status.Phase, StringComparer.Ordinal))
            throw Invalid("status.phase", "unknown phase");

        if (JsonShape.Optional(obj, "observed", out var observed) && !JsonShape.IsNull(observed))
            status.Observed = ParseObserved(observed, "status.observed");
        if (JsonShape.Optional(obj, "active", out var active) && !JsonShape.IsNull(active))
            status.Active = ParseActive(active, "status.active");
        if (JsonShape.Optional(obj, "attempts", out var attempts) && !JsonShape.IsNull(attempts))
            status.Attempts = ParseAttempts(attempts, "status.attempts");

        status.Services = ParseServices(JsonShape.Required(obj, "status", "services"), "status.services");

        if (JsonShape.Optional(obj, "last_error", out var lastError) && !JsonShape.IsNull(lastError))
            status.LastError = ParseLastError(lastError, "status.last_error");

        status.LogsTail = ParseLogsTail(JsonShape.Required(obj, "status", "logs_tail"), "status.logs_tail");
        return status;
    }

    private const long MaxSafeInteger = 9007199254740991;

    private static void WriteTarget(Utf8JsonWriter writer, StatusTarget target)
    {
        writer.WriteStartObject("target");
        writer.WriteString("provider", target.Provider);
        writer.WriteString("region", target.Region);
        writer.WriteString("arch", target.Arch);
        writer.WriteStartObject("selector");
        foreach (var (key, value) in target.Selector)
            writer.WriteString(key, value);
        writer.WriteEndObject();
        writer.WriteStartArray("ingress");
        foreach (var rule in target.Ingress)
        {
            writer.WriteStartObject();
            writer.WriteString("protocol", rule.Protocol);
            writer.WriteNumber("port", rule.Port);
            writer.WriteStartArray("source");
            foreach (var source in rule.Source)
                writer.WriteStringValue(source);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteHost(Utf8JsonWriter writer, StatusHost host)
    {
        writer.WriteStartObject("host");
        WriteNullableString(writer, "id", host.Id);
        WriteNullableString(writer, "boot_id", host.BootId);
        writer.WriteString("narwhald_version", host.NarwhaldVersion);
        WriteNullableString(writer, "agent_rollback_version", host.AgentRollbackVersion);
        WriteNullableString(writer, "started_at", host.StartedAt);
        WriteNullableString(writer, "status_key_id", host.StatusKeyId);
        WriteNullableString(writer, "blocked_agent_digest", host.BlockedAgentDigest);
        writer.WriteEndObject();
    }

    private static void WriteObserved(Utf8JsonWriter writer, StatusObserved? observed)
    {
        if (observed is null)
        {
            writer.WriteNull("observed");
            return;
        }

        writer.WriteStartObject("observed");
        writer.WriteNumber("generation", observed.Generation);
        writer.WriteString("manifest_digest", observed.ManifestDigest);
        writer.WriteString("payload_digest", observed.PayloadDigest);
        WriteNullableString(writer, "key_id", observed.KeyId);
        writer.WriteEndObject();
    }

    private static void WriteActive(Utf8JsonWriter writer, StatusActive? active)
    {
        if (active is null)
        {
            writer.WriteNull("active");
            return;
        }

        writer.WriteStartObject("active");
        writer.WriteNumber("generation", active.Generation);
        writer.WriteString("payload_digest", active.PayloadDigest);
        writer.WriteString("state", active.State);
        writer.WriteEndObject();
    }

    private static void WriteAttempts(Utf8JsonWriter writer, StatusAttempts? attempts)
    {
        if (attempts is null)
        {
            writer.WriteNull("attempts");
            return;
        }

        writer.WriteStartObject("attempts");
        writer.WriteNumber("generation", attempts.Generation);
        writer.WriteNumber("count", attempts.Count);
        writer.WriteString("last_at", attempts.LastAt);
        writer.WriteEndObject();
    }

    private static void WriteServices(Utf8JsonWriter writer, Dictionary<string, StatusService> services)
    {
        writer.WriteStartObject("services");
        foreach (var (name, service) in services)
        {
            writer.WriteStartObject(name);
            writer.WriteString("container", service.Container);
            writer.WriteString("image_digest", service.ImageDigest);
            writer.WriteString("state", service.State);
            WriteNullableString(writer, "health", service.Health);
            writer.WriteNumber("restarts", service.Restarts);
            WriteNullableString(writer, "started_at", service.StartedAt);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteLastError(Utf8JsonWriter writer, LastErrorInfo? error)
    {
        if (error is null)
        {
            writer.WriteNull("last_error");
            return;
        }

        if (!ErrorCodes.Contains(error.Code, StringComparer.Ordinal))
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.SchemaInvalid,
                $"status.last_error.code is unknown: {error.Code}");

        writer.WriteStartObject("last_error");
        writer.WriteString("code", error.Code);
        writer.WriteString("message", error.Message);
        WriteNullableString(writer, "service", error.Service);
        WriteNullableString(writer, "at", error.At);
        writer.WriteEndObject();
    }

    private static void WriteLogsTail(Utf8JsonWriter writer, List<StatusLogLine> lines)
    {
        writer.WriteStartArray("logs_tail");
        foreach (var line in lines)
        {
            writer.WriteStartObject();
            writer.WriteString("service", line.Service);
            writer.WriteString("at", line.At);
            writer.WriteString("line", line.Line);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteNumber(name, value.Value);
    }

    private static StatusTarget ParseTarget(JsonElement value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(obj, path, CloudArtifactErrorCodes.SchemaInvalid, "provider", "region", "arch", "selector", "ingress");

        var provider = JsonShape.String(JsonShape.Required(obj, path, "provider"), $"{path}.provider");
        if (provider is not ("aws" or "digitalocean" or "gcp"))
            throw Invalid($"{path}.provider", "unknown provider");
        var region = JsonShape.NonEmptyString(JsonShape.Required(obj, path, "region"), $"{path}.region");
        var arch = JsonShape.String(JsonShape.Required(obj, path, "arch"), $"{path}.arch");
        if (arch is not ("amd64" or "arm64"))
            throw Invalid($"{path}.arch", "unknown architecture");

        var selectorObject = JsonShape.Object(JsonShape.Required(obj, path, "selector"), $"{path}.selector");
        var selector = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in selectorObject.EnumerateObject())
            selector.Add(member.Name, JsonShape.NonEmptyString(member.Value, $"{path}.selector.{member.Name}"));

        var ingressArray = JsonShape.Array(JsonShape.Required(obj, path, "ingress"), $"{path}.ingress");
        var ingress = new List<StatusIngress>(ingressArray.GetArrayLength());
        for (var i = 0; i < ingressArray.GetArrayLength(); i++)
            ingress.Add(ParseIngress(ingressArray[i], $"{path}.ingress[{i}]"));

        return new StatusTarget
        {
            Provider = provider,
            Region = region,
            Arch = arch,
            Selector = selector,
            Ingress = ingress,
        };
    }

    private static StatusIngress ParseIngress(JsonElement value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(obj, path, CloudArtifactErrorCodes.SchemaInvalid, "protocol", "port", "source");

        var protocol = JsonShape.String(JsonShape.Required(obj, path, "protocol"), $"{path}.protocol");
        if (protocol is not ("tcp" or "udp"))
            throw Invalid($"{path}.protocol", "unknown protocol");
        var port = JsonShape.Integer(JsonShape.Required(obj, path, "port"), $"{path}.port", 1, 65535);
        var sourceArray = JsonShape.Array(JsonShape.Required(obj, path, "source"), $"{path}.source");
        var sources = new List<string>(sourceArray.GetArrayLength());
        foreach (var item in sourceArray.EnumerateArray())
            sources.Add(JsonShape.NonEmptyString(item, $"{path}.source"));

        return new StatusIngress { Protocol = protocol, Port = (int)port, Source = sources };
    }

    private static StatusHost ParseHost(JsonElement value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(
            obj,
            path,
            CloudArtifactErrorCodes.SchemaInvalid,
            "id", "boot_id", "narwhald_version", "agent_rollback_version", "started_at",
            "status_key_id", "blocked_agent_digest");

        var keyId = JsonShape.String(JsonShape.Required(obj, path, "status_key_id"), $"{path}.status_key_id");
        if (!CloudPatterns.KeyId().IsMatch(keyId))
            throw Invalid($"{path}.status_key_id", "expected 16 lowercase hex characters");

        return new StatusHost
        {
            Id = NullableString(obj, path, "id"),
            BootId = NullableString(obj, path, "boot_id"),
            NarwhaldVersion = JsonShape.NonEmptyString(JsonShape.Required(obj, path, "narwhald_version"), $"{path}.narwhald_version"),
            AgentRollbackVersion = NullableString(obj, path, "agent_rollback_version"),
            StartedAt = OptionalTimestamp(obj, path, "started_at"),
            StatusKeyId = keyId,
            BlockedAgentDigest = OptionalDigest(obj, path, "blocked_agent_digest"),
        };
    }

    private static StatusObserved ParseObserved(JsonElement value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(obj, path, CloudArtifactErrorCodes.SchemaInvalid, "generation", "manifest_digest", "payload_digest", "key_id");
        var keyId = NullableString(obj, path, "key_id");
        if (keyId is not null && !CloudPatterns.KeyId().IsMatch(keyId))
            throw Invalid($"{path}.key_id", "expected 16 lowercase hex characters");
        return new StatusObserved
        {
            Generation = JsonShape.Integer(JsonShape.Required(obj, path, "generation"), $"{path}.generation", 1, MaxSafeInteger),
            ManifestDigest = RequiredDigest(obj, path, "manifest_digest"),
            PayloadDigest = RequiredDigest(obj, path, "payload_digest"),
            KeyId = keyId,
        };
    }

    private static StatusActive ParseActive(JsonElement value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(obj, path, CloudArtifactErrorCodes.SchemaInvalid, "generation", "payload_digest", "state");
        var state = JsonShape.String(JsonShape.Required(obj, path, "state"), $"{path}.state");
        if (state is not ("healthy" or "degraded" or "failed"))
            throw Invalid($"{path}.state", "unknown active state");
        return new StatusActive
        {
            Generation = JsonShape.Integer(JsonShape.Required(obj, path, "generation"), $"{path}.generation", 1, MaxSafeInteger),
            PayloadDigest = RequiredDigest(obj, path, "payload_digest"),
            State = state,
        };
    }

    private static StatusAttempts ParseAttempts(JsonElement value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(obj, path, CloudArtifactErrorCodes.SchemaInvalid, "generation", "count", "last_at");
        return new StatusAttempts
        {
            Generation = JsonShape.Integer(JsonShape.Required(obj, path, "generation"), $"{path}.generation", 1, MaxSafeInteger),
            Count = (int)JsonShape.Integer(JsonShape.Required(obj, path, "count"), $"{path}.count", 0, int.MaxValue),
            LastAt = Timestamp(JsonShape.Required(obj, path, "last_at"), $"{path}.last_at"),
        };
    }

    private static Dictionary<string, StatusService> ParseServices(JsonElement value, string path)
    {
        var obj = JsonShape.Object(value, path);
        var services = new Dictionary<string, StatusService>(StringComparer.Ordinal);
        foreach (var member in obj.EnumerateObject())
        {
            var servicePath = $"{path}.{member.Name}";
            var entry = JsonShape.Object(member.Value, servicePath);
            JsonShape.Members(entry, servicePath, CloudArtifactErrorCodes.SchemaInvalid, "container", "image_digest", "state", "health", "restarts", "started_at");
            services.Add(member.Name, new StatusService
            {
                Container = JsonShape.NonEmptyString(JsonShape.Required(entry, servicePath, "container"), $"{servicePath}.container"),
                ImageDigest = RequiredDigest(entry, servicePath, "image_digest"),
                State = JsonShape.NonEmptyString(JsonShape.Required(entry, servicePath, "state"), $"{servicePath}.state"),
                Health = NullableString(entry, servicePath, "health"),
                Restarts = (int)JsonShape.Integer(JsonShape.Required(entry, servicePath, "restarts"), $"{servicePath}.restarts", 0, int.MaxValue),
                StartedAt = OptionalTimestamp(entry, servicePath, "started_at"),
            });
        }

        return services;
    }

    private static LastErrorInfo ParseLastError(JsonElement value, string path)
    {
        var obj = JsonShape.Object(value, path);
        JsonShape.Members(obj, path, CloudArtifactErrorCodes.SchemaInvalid, "code", "message", "service", "at");
        var code = JsonShape.String(JsonShape.Required(obj, path, "code"), $"{path}.code");
        if (!ErrorCodes.Contains(code, StringComparer.Ordinal))
            throw Invalid($"{path}.code", "unknown error code");
        return new LastErrorInfo
        {
            Code = code,
            Message = JsonShape.NonEmptyString(JsonShape.Required(obj, path, "message"), $"{path}.message"),
            Service = NullableString(obj, path, "service"),
            At = OptionalTimestamp(obj, path, "at"),
        };
    }

    private static List<StatusLogLine> ParseLogsTail(JsonElement value, string path)
    {
        var array = JsonShape.Array(value, path);
        if (array.GetArrayLength() > 200)
            throw Invalid(path, "expected at most 200 lines");
        var lines = new List<StatusLogLine>(array.GetArrayLength());
        var bytes = 0;
        for (var i = 0; i < array.GetArrayLength(); i++)
        {
            var itemPath = $"{path}[{i}]";
            var obj = JsonShape.Object(array[i], itemPath);
            JsonShape.Members(obj, itemPath, CloudArtifactErrorCodes.SchemaInvalid, "service", "at", "line");
            var line = JsonShape.String(JsonShape.Required(obj, itemPath, "line"), $"{itemPath}.line");
            bytes += Encoding.UTF8.GetByteCount(line);
            lines.Add(new StatusLogLine
            {
                Service = JsonShape.NonEmptyString(JsonShape.Required(obj, itemPath, "service"), $"{itemPath}.service"),
                At = Timestamp(JsonShape.Required(obj, itemPath, "at"), $"{itemPath}.at"),
                Line = line,
            });
        }

        if (bytes > 32 * 1024)
            throw Invalid(path, "expected at most 32 KiB of log text");
        return lines;
    }

    private static string RequiredDigest(JsonElement obj, string path, string name)
    {
        var value = JsonShape.String(JsonShape.Required(obj, path, name), $"{path}.{name}");
        if (!CloudPatterns.Digest().IsMatch(value))
            throw Invalid($"{path}.{name}", "expected a sha256 digest");
        return value;
    }

    private static string? OptionalDigest(JsonElement obj, string path, string name)
    {
        var value = NullableString(obj, path, name);
        if (value is not null && !CloudPatterns.Digest().IsMatch(value))
            throw Invalid($"{path}.{name}", "expected a sha256 digest");
        return value;
    }

    private static string? NullableString(JsonElement obj, string path, string name)
    {
        if (!JsonShape.Optional(obj, name, out var value) || JsonShape.IsNull(value))
            return null;
        return JsonShape.String(value, $"{path}.{name}");
    }

    private static string? OptionalTimestamp(JsonElement obj, string path, string name)
    {
        if (!JsonShape.Optional(obj, name, out var value) || JsonShape.IsNull(value))
            return null;
        return Timestamp(value, $"{path}.{name}");
    }

    private static string Timestamp(JsonElement value, string path)
    {
        var text = JsonShape.String(value, path);
        if (!CloudEncoding.TryParseUtcTimestamp(text, out _))
            throw Invalid(path, "expected an RFC 3339 UTC timestamp");
        return text;
    }

    private static CloudArtifactException Invalid(string path, string reason) =>
        JsonShape.Error(CloudArtifactErrorCodes.SchemaInvalid, path, reason);
}
