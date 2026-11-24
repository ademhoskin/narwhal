using System.Text.Json;
using System.Text.Json.Serialization;

namespace Narwhal.Reconcile;

public static class ReconcileSchema
{
    public const string State = "narwhal.state/v1";
    public const string Journal = "narwhal.journal/v1";
    public const string Status = "narwhal.status/v1";
}

public static class ReconcilePhases
{
    public const string Idle = "idle";
    public const string Pulling = "pulling";
    public const string Applying = "applying";
    public const string Verifying = "verifying";
    public const string Degraded = "degraded";
    public const string Failed = "failed";
    public const string RollingBack = "rolling_back";
    public const string ReplayRejected = "replay_rejected";
    public const string RegistryUnavailable = "registry_unavailable";
    public const string AuthFailed = "auth_failed";
}

public static class JournalPhases
{
    public const string Applying = "applying";
    public const string RollingBack = "rolling_back";
    public const string Committed = "committed";
}

public sealed class ReconcileState
{
    public string Schema { get; set; } = ReconcileSchema.State;
    public long HighestGeneration { get; set; }
    public string? HighestPayloadDigest { get; set; }
    public string? ActivePayloadDigest { get; set; }
    public string? ActiveManifestDigest { get; set; }
    public long? ActiveGeneration { get; set; }
    public long? BlockedGeneration { get; set; }
    public string? BlockedAgentDigest { get; set; }
    public string? AgentRollbackVersion { get; set; }
    public string? LastPayloadDigest { get; set; }
    public string? LastManifestDigest { get; set; }
    public string? LastRejectedManifestDigest { get; set; }
    public string? LastRejectedCode { get; set; }
    public string Phase { get; set; } = ReconcilePhases.Idle;
    /// <summary>True when the last rollback could not restore the active generation.</summary>
    public bool RollbackFailed { get; set; }
    public LastErrorInfo? LastError { get; set; }
    public long StatusEpoch { get; set; }
    public long StatusSeq { get; set; }
    public string? LastStatusPublishedAt { get; set; }
    public string? LastStatusFingerprint { get; set; }
    public bool StatusPublishPending { get; set; }
    public bool StatusStateLost { get; set; }
    public long LastKnownStatusEpoch { get; set; }
    public long LastKnownStatusSeq { get; set; }
    public string? LastHostId { get; set; }
    public bool ArtifactCacheMissing { get; set; }
}

public sealed class LastErrorInfo
{
    public required string Code { get; set; }
    public string? Service { get; set; }
    public required string Message { get; set; }
    public string? At { get; set; }
}

public sealed class ReconcileJournal
{
    public string Schema { get; set; } = ReconcileSchema.Journal;
    public long Generation { get; set; }
    public string PayloadDigest { get; set; } = "";
    public string ManifestDigest { get; set; } = "";
    public string Phase { get; set; } = JournalPhases.Applying;
    public long HighestGeneration { get; set; }
    public string? HighestPayloadDigest { get; set; }
    public long? BlockedGeneration { get; set; }
    public long? ActiveGeneration { get; set; }
    public string? ActivePayloadDigest { get; set; }
    public string? ActiveManifestDigest { get; set; }
    public List<JournalAction> Actions { get; set; } = [];
    public LastErrorInfo? LastError { get; set; }
    public string StartedAt { get; set; } = "";
}

public sealed class JournalAction
{
    public required string Service { get; set; }
    public required string Action { get; set; }

    /// <summary>The action began mutating podman and must be rolled back on failure.</summary>
    public bool Touched { get; set; }

    /// <summary>The action completed its health gate.</summary>
    public bool Completed { get; set; }
}

public sealed class ReconcileStatusDocument
{
    public string Schema { get; set; } = ReconcileSchema.Status;
    public int Version { get; set; } = 1;
    public string Project { get; set; } = "";
    public StatusTarget Target { get; set; } = new();
    public long StatusSeq { get; set; }
    public long Epoch { get; set; }
    public string Updated { get; set; } = "";
    public StatusHost Host { get; set; } = new();
    public long HighestGeneration { get; set; }
    public long? BlockedGeneration { get; set; }
    public string Phase { get; set; } = ReconcilePhases.Idle;
    public StatusObserved? Observed { get; set; }
    public StatusActive? Active { get; set; }
    public StatusAttempts? Attempts { get; set; }
    public Dictionary<string, StatusService> Services { get; set; } = new(StringComparer.Ordinal);
    public LastErrorInfo? LastError { get; set; }
    public List<StatusLogLine> LogsTail { get; set; } = [];

    [JsonIgnore]
    public long? ActiveGeneration => Active?.Generation;
}

public sealed class StatusTarget
{
    public string Provider { get; set; } = "";
    public string Region { get; set; } = "";
    public string Arch { get; set; } = "";
    public Dictionary<string, string> Selector { get; set; } = new(StringComparer.Ordinal);
    public List<StatusIngress> Ingress { get; set; } = [];
}

public sealed class StatusIngress
{
    public required string Protocol { get; set; }
    public int Port { get; set; }
    public List<string> Source { get; set; } = [];
}

public sealed class StatusHost
{
    public string? Id { get; set; }
    public string? BootId { get; set; }
    public string NarwhaldVersion { get; set; } = App.Version;
    public string? AgentRollbackVersion { get; set; }
    public string? StartedAt { get; set; }
    public string? StatusKeyId { get; set; }
    public string? BlockedAgentDigest { get; set; }
}

public sealed class StatusObserved
{
    public long Generation { get; set; }
    public required string ManifestDigest { get; set; }
    public required string PayloadDigest { get; set; }
    public string? KeyId { get; set; }
}

public sealed class StatusActive
{
    public long Generation { get; set; }
    public required string PayloadDigest { get; set; }
    public string State { get; set; } = "healthy";
}

public sealed class StatusAttempts
{
    public long Generation { get; set; }
    public int Count { get; set; }
    public string LastAt { get; set; } = "";
}

public sealed class StatusService
{
    public required string Container { get; set; }
    public required string ImageDigest { get; set; }
    public string State { get; set; } = "running";
    public string? Health { get; set; }
    public int Restarts { get; set; }
    public string? StartedAt { get; set; }
}

public sealed class StatusLogLine
{
    public required string Service { get; set; }
    public required string At { get; set; }
    public required string Line { get; set; }
}

public enum ReconcileExitCode
{
    Success = 0,
    Config = 1,
    Runtime = 3,
    Verification = 5,
}

/// <summary>A fail-closed reconcile error carrying its CLI exit code and status phase.</summary>
public sealed class ReconcileException(string code, string message, ReconcileExitCode exitCode, string phase = ReconcilePhases.Failed)
    : Exception(message)
{
    public string Code { get; } = code;
    public ReconcileExitCode ExitCode { get; } = exitCode;
    public string Phase { get; } = phase;
    public string? Service { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ReconcileState))]
[JsonSerializable(typeof(ReconcileJournal))]
[JsonSerializable(typeof(ReconcileStatusDocument))]
internal sealed partial class ReconcileJsonContext : JsonSerializerContext
{
    public static ReconcileJsonContext Compact { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
