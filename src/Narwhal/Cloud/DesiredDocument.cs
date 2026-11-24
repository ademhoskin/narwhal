namespace Narwhal.Cloud;

/// <summary>A validated <c>narwhal.desired/v1</c> payload.</summary>
public sealed class DesiredDocument
{
    public const string SchemaName = "narwhal.desired/v1";
    public const int SchemaVersion = 1;

    public required string Schema { get; init; }
    public required int Version { get; init; }
    public required string Project { get; init; }
    public required long Generation { get; init; }
    public required string Created { get; init; }
    public PreviousArtifact? Previous { get; init; }
    public required TargetSpec Target { get; init; }
    public IReadOnlyList<TrustedKey>? Trust { get; init; }
    public required IReadOnlyDictionary<string, ImageSpec> Images { get; init; }
    public required IReadOnlyDictionary<string, ServiceSpec> Services { get; init; }
    public DeploySpec? Deploy { get; init; }
    public AgentSpec? Agent { get; init; }
}

/// <summary>The previous accepted payload for the same target.</summary>
public sealed class PreviousArtifact
{
    public required long Generation { get; init; }
    public required string Digest { get; init; }
    public required string ManifestDigest { get; init; }
}

/// <summary>The VM target the payload applies to.</summary>
public sealed class TargetSpec
{
    public required string Provider { get; init; }
    public required string Region { get; init; }
    public required string Arch { get; init; }
    public required IReadOnlyDictionary<string, string> Selector { get; init; }
    public required IReadOnlyList<IngressRule> Ingress { get; init; }
}

/// <summary>One signed data-plane ingress rule.</summary>
public sealed class IngressRule
{
    public required string Protocol { get; init; }
    public required int Port { get; init; }
    public required IReadOnlyList<string> Source { get; init; }
}

/// <summary>A pinned desired-artifact signing key.</summary>
public sealed class TrustedKey
{
    public required string KeyId { get; init; }
    public required byte[] PublicKey { get; init; }
    public DateTimeOffset? NotAfter { get; init; }
}

/// <summary>One image referenced by a service, always pulled by digest.</summary>
public sealed class ImageSpec
{
    public required string Repository { get; init; }
    public required string Platform { get; init; }
    public required long Size { get; init; }
}

/// <summary>A service container desired on the target.</summary>
public sealed class ServiceSpec
{
    public required string Image { get; init; }
    public IReadOnlyList<string>? Command { get; init; }
    public string? Entrypoint { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public IReadOnlyDictionary<string, SecretRef>? Secrets { get; init; }
    public required IReadOnlyList<string> Ports { get; init; }
    public required IReadOnlyList<string> Volumes { get; init; }
    public HealthSpec? Health { get; init; }
    public required IReadOnlyList<string> DependsOn { get; init; }
    public string? Restart { get; init; }
    public string? User { get; init; }
    public string? Workdir { get; init; }
    public int? StopTimeout { get; init; }
}

/// <summary>A secret reference resolved by the provider adapter.</summary>
public sealed class SecretRef
{
    public required string Source { get; init; }
    public required string Id { get; init; }
    public string? Version { get; init; }
    public string? Key { get; init; }
}

/// <summary>A container health check.</summary>
public sealed class HealthSpec
{
    public required string Command { get; init; }
    public required int Interval { get; init; }
    public required int Timeout { get; init; }
    public required int Retries { get; init; }
    public int? StartPeriod { get; init; }
}

/// <summary>Deployment-wide options.</summary>
public sealed class DeploySpec
{
    public string? Network { get; init; }
    public int? WaitSeconds { get; init; }
}

/// <summary>An optional narwhald upgrade authorization.</summary>
public sealed class AgentSpec
{
    public required string Version { get; init; }
    public required string Repository { get; init; }
    public required string Digest { get; init; }
    public required string BinarySha256 { get; init; }
}
