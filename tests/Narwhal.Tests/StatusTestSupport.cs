using Narwhal.Cloud;
using Narwhal.Reconcile;

namespace Narwhal.Tests;

internal sealed class TestStatusArtifact
{
    public required ReconcileStatusDocument Document { get; init; }
    public required byte[] Payload { get; init; }
    public required byte[] Signature { get; init; }
    public required byte[] Manifest { get; init; }
    public required string ManifestDigest { get; init; }
}

internal static class StatusTestData
{
    public const string Repository = "shop/state/aws-us-east-1-amd64-1a2b3c4d/status";
    public const string HostId = "i-0a1b2c3d4e5f67890";
    public const string BootId = "4c1f9a7e-2b3d-4c5e-9f00-112233445566";

    public static ReconcileStatusDocument Document(
        long statusSeq = 184,
        long epoch = 3,
        string phase = ReconcilePhases.Idle,
        long highestGeneration = 42,
        long? blockedGeneration = null,
        string? statusKeyId = null,
        bool withObserved = true,
        bool withError = false)
    {
        statusKeyId ??= CloudArtifactTestData.KeyId1;
        var status = new ReconcileStatusDocument
        {
            Project = ReconcileTestData.Project,
            Target = new StatusTarget
            {
                Provider = "aws",
                Region = "us-east-1",
                Arch = "amd64",
                Selector = new Dictionary<string, string>(StringComparer.Ordinal) { ["stack"] = "prod" },
                Ingress =
                [
                    new StatusIngress
                    {
                        Protocol = "tcp",
                        Port = 8080,
                        Source = ["203.0.113.0/24"],
                    },
                ],
            },
            StatusSeq = statusSeq,
            Epoch = epoch,
            Updated = "2026-11-24T18:32:10Z",
            Host = new StatusHost
            {
                Id = HostId,
                BootId = BootId,
                NarwhaldVersion = "0.1.0",
                StartedAt = "2026-11-24T17:00:00Z",
                StatusKeyId = statusKeyId,
            },
            HighestGeneration = highestGeneration,
            BlockedGeneration = blockedGeneration,
            Phase = phase,
            Observed = withObserved
                ? new StatusObserved
                {
                    Generation = highestGeneration,
                    ManifestDigest = Digest('a'),
                    PayloadDigest = Digest('b'),
                    KeyId = CloudArtifactTestData.KeyId1,
                }
                : null,
            Active = new StatusActive
            {
                Generation = highestGeneration,
                PayloadDigest = Digest('b'),
                State = phase == ReconcilePhases.Degraded ? "degraded" : "healthy",
            },
            Attempts = new StatusAttempts { Generation = highestGeneration, Count = 1, LastAt = "2026-11-24T18:31:12Z" },
            Services = new Dictionary<string, StatusService>(StringComparer.Ordinal)
            {
                ["api"] = new()
                {
                    Container = "narwhal-shop-api",
                    ImageDigest = Digest('c'),
                    State = "running",
                    Health = "healthy",
                    Restarts = 0,
                    StartedAt = "2026-11-24T18:31:02Z",
                },
            },
            LastError = withError
                ? new LastErrorInfo
                {
                    Code = "health_timeout",
                    Message = "api did not become healthy",
                    Service = "api",
                    At = "2026-11-24T18:35:00Z",
                }
                : null,
            LogsTail =
            [
                new StatusLogLine { Service = "api", At = "2026-11-24T18:31:05Z", Line = "listening on :8080" },
            ],
        };
        return status;
    }

    public static TestStatusArtifact Artifact(byte[]? seed = null, ReconcileStatusDocument? document = null)
    {
        document ??= Document();
        var payload = StatusDocumentCodec.Encode(document);
        var signature = StatusSigningKey.FromSeed(seed ?? CloudArtifactTestData.Seed1).Sign(payload);
        var manifest = StatusManifest.Build(
            CloudEncoding.Sha256Digest(payload),
            payload.Length,
            CloudEncoding.Sha256Digest(signature),
            signature.Length,
            document.StatusSeq,
            fallback: false);
        return new TestStatusArtifact
        {
            Document = document,
            Payload = payload,
            Signature = signature,
            Manifest = manifest,
            ManifestDigest = CloudEncoding.Sha256Digest(manifest),
        };
    }

    public static StatusKeySet Keys(params byte[][] seeds)
    {
        if (seeds.Length == 0)
            seeds = [CloudArtifactTestData.Seed1];
        return StatusKeySet.FromKeys(seeds.Select(seed =>
        {
            var publicKey = Ed25519.PublicKeyFromSeed(seed);
            return new TrustedKey { KeyId = KeyId.Derive(publicKey), PublicKey = publicKey };
        }));
    }

    public static IReadOnlyList<ArtifactLayer> Layers(byte[] manifest, string reference = "latest") =>
        OciManifestParser.Parse(manifest, Repository, reference);

    public static string Digest(char value) => "sha256:" + new string(value, 64);
}

/// <summary>In-memory status registry with push capture and failure injection.</summary>
internal sealed class FakeStatusRegistry : IStatusRegistry
{
    private readonly Dictionary<string, RegistryManifest> _manifests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    public bool Unavailable { get; set; }
    public bool RejectArtifactType { get; set; }
    public Exception? ManifestFailure { get; set; }
    public int ManifestAttempts { get; private set; }
    public List<(string Repository, string Reference, byte[] Manifest, string MediaType)> PushedManifests { get; } = [];
    public List<(string Repository, string Digest, long Size)> PushedBlobs { get; } = [];
    public List<string> Events { get; } = [];

    public void AddStatus(string repository, string reference, TestStatusArtifact artifact)
    {
        var layers = StatusTestData.Layers(artifact.Manifest, reference);
        var digest = CloudEncoding.Sha256Digest(artifact.Manifest);
        _manifests[Key(repository, reference)] = new RegistryManifest(digest, artifact.Manifest, layers, StatusArtifactLayout.Select(layers));
        _blobs[Key(repository, CloudEncoding.Sha256Digest(artifact.Payload))] = artifact.Payload;
        _blobs[Key(repository, CloudEncoding.Sha256Digest(artifact.Signature))] = artifact.Signature;
    }

    public Task<RegistryManifest> GetStatusManifestAsync(string repository, string reference, CancellationToken cancellationToken)
    {
        if (Unavailable)
            throw new RegistryException("simulated registry outage");
        if (!_manifests.TryGetValue(Key(repository, reference), out var manifest))
            throw new RegistryException($"manifest not found: {repository}:{reference}", statusCode: 404);
        return Task.FromResult(manifest);
    }

    public Task<byte[]> GetBlobAsync(string repository, string digest, CancellationToken cancellationToken)
    {
        if (Unavailable)
            throw new RegistryException("simulated registry outage");
        if (!_blobs.TryGetValue(Key(repository, digest), out var bytes))
            throw new RegistryException($"blob not found: {repository}@{digest}", statusCode: 404);
        return Task.FromResult(bytes);
    }

    public Task PushBlobAsync(string repository, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (Unavailable)
            throw new RegistryException("simulated registry outage");
        var digest = CloudEncoding.Sha256Digest(bytes.Span);
        _blobs[Key(repository, digest)] = bytes.ToArray();
        PushedBlobs.Add((repository, digest, bytes.Length));
        Events.Add("blob:" + digest);
        return Task.CompletedTask;
    }

    public Task<RegistryPushResult> PushManifestAsync(
        string repository,
        string reference,
        byte[] manifest,
        string mediaType,
        CancellationToken cancellationToken)
    {
        ManifestAttempts++;
        if (Unavailable)
            throw new RegistryException("simulated registry outage");
        if (ManifestFailure is not null)
            throw ManifestFailure;
        if (RejectArtifactType &&
            System.Text.Encoding.UTF8.GetString(manifest).Contains("\"artifactType\"", StringComparison.Ordinal))
        {
            throw new RegistryManifestRejectedException(415, "artifactType is not supported");
        }

        var layers = OciManifestParser.Parse(manifest, repository, reference);
        StatusArtifactLayout.Select(layers);
        using (var document = System.Text.Json.JsonDocument.Parse(manifest))
        {
            ValidateDescriptor(repository, document.RootElement.GetProperty("config"));
            foreach (var layer in document.RootElement.GetProperty("layers").EnumerateArray())
                ValidateDescriptor(repository, layer);
        }

        var digest = CloudEncoding.Sha256Digest(manifest);
        _manifests[Key(repository, reference)] = new RegistryManifest(digest, manifest, layers, StatusArtifactLayout.Select(layers));
        PushedManifests.Add((repository, reference, manifest, mediaType));
        Events.Add("manifest:" + digest);
        return Task.FromResult(new RegistryPushResult(digest, reference));
    }

    private void ValidateDescriptor(string repository, System.Text.Json.JsonElement descriptor)
    {
        var digest = descriptor.GetProperty("digest").GetString() ?? "";
        var size = descriptor.GetProperty("size").GetInt64();
        if (!_blobs.TryGetValue(Key(repository, digest), out var blob) || blob.LongLength != size)
            throw new RegistryException($"manifest descriptor is missing or has the wrong size: {digest}", statusCode: 400);
    }

    private static string Key(string repository, string reference) => repository + "|" + reference;
}

internal sealed class FakeStatusChannel : IStatusChannel
{
    public VerifiedStatus? Remote { get; set; }
    public bool PublishFails { get; set; }
    public bool ThrowOnPublish { get; set; }
    public List<ReconcileStatusDocument> Published { get; } = [];
    public int ReadCalls { get; private set; }

    public Task<VerifiedStatus?> TryReadLatestAsync(CancellationToken cancellationToken)
    {
        ReadCalls++;
        return Task.FromResult(Remote);
    }

    public Task<VerifiedStatus> FetchLatestAsync(CancellationToken cancellationToken) =>
        Remote is null
            ? throw new RegistryException("no status", statusCode: 404)
            : Task.FromResult(Remote);

    public Task<StatusPublishResult> PublishAsync(ReconcileStatusDocument status, CancellationToken cancellationToken)
    {
        if (ThrowOnPublish)
            throw new RegistryException("simulated publish outage");
        if (PublishFails)
            return Task.FromResult(new StatusPublishResult(false, true, "queued"));
        Published.Add(StatusDocumentCodec.Decode(StatusDocumentCodec.Encode(status)));
        return Task.FromResult(new StatusPublishResult(true, false, null));
    }
}
