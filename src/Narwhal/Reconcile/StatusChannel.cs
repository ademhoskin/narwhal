using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Narwhal.Cli;
using Narwhal.Cloud;

namespace Narwhal.Reconcile;

/// <summary>The OCI operations a status channel needs, split out for hermetic tests.</summary>
public interface IStatusRegistry
{
    Task<RegistryManifest> GetStatusManifestAsync(string repository, string reference, CancellationToken cancellationToken);
    Task<byte[]> GetBlobAsync(string repository, string digest, CancellationToken cancellationToken);
    Task PushBlobAsync(string repository, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    Task<RegistryPushResult> PushManifestAsync(
        string repository,
        string reference,
        byte[] manifest,
        string mediaType,
        CancellationToken cancellationToken);
}

/// <summary>Reads and publishes the signed status artifact.</summary>
public interface IStatusChannel
{
    /// <summary>Reads and verifies the latest status, returning null when it is absent or unreadable.</summary>
    Task<VerifiedStatus?> TryReadLatestAsync(CancellationToken cancellationToken);

    /// <summary>Reads and verifies the latest status; registry and verification errors are propagated.</summary>
    Task<VerifiedStatus> FetchLatestAsync(CancellationToken cancellationToken);

    Task<StatusPublishResult> PublishAsync(ReconcileStatusDocument status, CancellationToken cancellationToken);
}

public sealed record StatusPublishResult(bool Published, bool Queued, string? Warning);

/// <summary>Signed status publishing over the state registry.</summary>
public sealed class StatusArtifactChannel : IStatusChannel
{
    private readonly string _repository;
    private readonly IStatusRegistry _registry;
    private readonly StatusKeySet _statusKeys;
    private readonly TargetExpectation _target;
    private readonly StatusSigningKey? _signingKey;
    private readonly StatusStore _store;
    private readonly Reporter _reporter;
    private readonly TimeProvider _time;
    private readonly IReadOnlyCollection<string>? _redactedValues;

    public StatusArtifactChannel(
        string repository,
        IStatusRegistry registry,
        StatusKeySet statusKeys,
        TargetExpectation target,
        StatusSigningKey? signingKey,
        StatusStore store,
        Reporter reporter,
        TimeProvider? time = null,
        IReadOnlyCollection<string>? redactedValues = null)
    {
        _repository = repository;
        _registry = registry;
        _statusKeys = statusKeys;
        _target = target;
        _signingKey = signingKey;
        _store = store;
        _reporter = reporter;
        _time = time ?? TimeProvider.System;
        _redactedValues = redactedValues;
    }

    public string Repository => _repository;

    public async Task<VerifiedStatus?> TryReadLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await FetchLatestAsync(cancellationToken);
        }
        catch (RegistryException e)
        {
            if (e.StatusCode == 404)
                return null;
            _reporter.Warn($"cannot read the latest status: {e.Message}");
            return null;
        }
        catch (CloudArtifactException e)
        {
            _reporter.Warn($"ignoring the latest status: {e.Message}");
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidOperationException ||
                                  (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _reporter.Warn($"cannot read the latest status: {e.Message}");
            return null;
        }
    }

    public async Task<VerifiedStatus> FetchLatestAsync(CancellationToken cancellationToken)
    {
        if (_statusKeys.IsEmpty)
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.SignatureInvalid,
                "no registered status public key for the target");

        var manifest = await _registry.GetStatusManifestAsync(_repository, StatusManifest.LatestTag, cancellationToken);
        var payload = await _registry.GetBlobAsync(_repository, manifest.Selection.Payload.Digest, cancellationToken);
        var signature = await _registry.GetBlobAsync(_repository, manifest.Selection.Signature.Digest, cancellationToken);
        return StatusArtifactVerifier.Verify(
            payload,
            signature,
            manifest.Layers,
            _statusKeys,
            _target,
            _time.GetUtcNow(),
            manifest.Digest);
    }

    public async Task<StatusPublishResult> PublishAsync(ReconcileStatusDocument status, CancellationToken cancellationToken)
    {
        if (_signingKey is null)
            throw new AgentConfigException("status signing key is not configured");

        if (!string.Equals(status.Host.StatusKeyId, _signingKey.KeyId, StringComparison.Ordinal))
            throw new AgentConfigException("status host.status_key_id does not match the signing seed");

        status.LogsTail = StatusLogs.BoundAndRedact(status.LogsTail, _redactedValues);
        var payload = StatusDocumentCodec.Encode(status);
        var signature = _signingKey.Sign(payload);
        var payloadDigest = CloudEncoding.Sha256Digest(payload);
        var signatureDigest = CloudEncoding.Sha256Digest(signature);

        var primary = StatusManifest.Build(
            payloadDigest,
            payload.Length,
            signatureDigest,
            signature.Length,
            status.StatusSeq,
            fallback: false);

        var snapshot = new StatusSnapshot
        {
            Epoch = status.Epoch,
            StatusSeq = status.StatusSeq,
            Payload = payload,
            Signature = signature,
            Manifest = primary,
            ManifestDigest = CloudEncoding.Sha256Digest(primary),
            RecordedAt = Timestamp(_time.GetUtcNow()),
        };
        _store.SaveHistory(snapshot);

        var queued = _store.NewestQueued();
        if (queued is not null)
        {
            var comparison = CompareOrder(queued.Epoch, queued.StatusSeq, snapshot.Epoch, snapshot.StatusSeq);
            if (comparison > 0)
            {
                return new StatusPublishResult(
                    Published: false,
                    Queued: false,
                    Warning: $"queued status {queued.StatusSeq} is newer than status {snapshot.StatusSeq}; deferring publication");
            }

            if (comparison < 0)
            {
                if (await RepublishQueuedAsync(queued, cancellationToken))
                    _store.ClearQueue();
            }
        }

        try
        {
            RegistryPushResult result;
            try
            {
                result = await PushSnapshotAsync(snapshot, cancellationToken);
            }
            catch (RegistryManifestRejectedException)
            {
                snapshot = WithFallback(snapshot);
                _store.SaveHistory(snapshot);
                result = await PushSnapshotAsync(snapshot, cancellationToken);
            }

            _store.ClearQueue();
            _reporter.Detail($"published status {status.StatusSeq} as {result.Digest}");
            return new StatusPublishResult(Published: true, Queued: false, Warning: null);
        }
        catch (Exception e) when (e is RegistryException or IOException or UnauthorizedAccessException or
                                  RegistryManifestRejectedException or HttpRequestException or JsonException ||
                                  (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _store.Enqueue(snapshot);
            return new StatusPublishResult(
                Published: false,
                Queued: true,
                Warning: $"status publish failed; queued {status.StatusSeq}: {e.Message}");
        }
    }

    private async Task<bool> RepublishQueuedAsync(StatusSnapshot queued, CancellationToken cancellationToken)
    {
        try
        {
            await PushSnapshotAsync(queued, cancellationToken);
            _reporter.Detail($"published queued status {queued.StatusSeq}");
            return true;
        }
        catch (RegistryManifestRejectedException)
        {
            try
            {
                await PushSnapshotAsync(WithFallback(queued), cancellationToken);
                _reporter.Detail($"published queued status {queued.StatusSeq} with the fallback manifest");
                return true;
            }
            catch (Exception e) when (e is RegistryException or IOException or UnauthorizedAccessException or
                                      RegistryManifestRejectedException or HttpRequestException or JsonException ||
                                      (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _reporter.Warn($"cannot publish queued status {queued.StatusSeq}: {e.Message}");
                return false;
            }
        }
        catch (Exception e) when (e is RegistryException or IOException or UnauthorizedAccessException or
                                  RegistryManifestRejectedException or HttpRequestException or JsonException ||
                                  (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _reporter.Warn($"cannot publish queued status {queued.StatusSeq}: {e.Message}");
            return false;
        }
    }

    private static int CompareOrder(long leftEpoch, long leftSeq, long rightEpoch, long rightSeq)
    {
        var epoch = leftEpoch.CompareTo(rightEpoch);
        return epoch != 0 ? epoch : leftSeq.CompareTo(rightSeq);
    }

    private async Task<RegistryPushResult> PushSnapshotAsync(StatusSnapshot snapshot, CancellationToken cancellationToken)
    {
        await _registry.PushBlobAsync(_repository, snapshot.Payload, cancellationToken);
        await _registry.PushBlobAsync(_repository, snapshot.Signature, cancellationToken);
        await _registry.PushBlobAsync(_repository, StatusManifest.EmptyConfig, cancellationToken);
        return await _registry.PushManifestAsync(
            _repository,
            StatusManifest.LatestTag,
            snapshot.Manifest,
            StatusManifest.ManifestMediaType,
            cancellationToken);
    }

    private static StatusSnapshot WithFallback(StatusSnapshot snapshot)
    {
        var payloadDigest = CloudEncoding.Sha256Digest(snapshot.Payload);
        var signatureDigest = CloudEncoding.Sha256Digest(snapshot.Signature);
        var fallback = StatusManifest.Build(
            payloadDigest,
            snapshot.Payload.Length,
            signatureDigest,
            snapshot.Signature.Length,
            snapshot.StatusSeq,
            fallback: true);
        return new StatusSnapshot
        {
            Epoch = snapshot.Epoch,
            StatusSeq = snapshot.StatusSeq,
            Payload = snapshot.Payload,
            Signature = snapshot.Signature,
            Manifest = fallback,
            ManifestDigest = CloudEncoding.Sha256Digest(fallback),
            RecordedAt = snapshot.RecordedAt,
        };
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>Decides whether a cycle's status is worth publishing.</summary>
public static class StatusPublicationPolicy
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(5);

    public static bool ShouldPublish(ReconcileState state, ReconcileStatusDocument status, DateTimeOffset now)
    {
        var fingerprint = Fingerprint(status);
        if (state.StatusPublishPending)
            return true;
        if (!string.Equals(state.LastStatusFingerprint, fingerprint, StringComparison.Ordinal))
            return true;
        if (!CloudEncoding.TryParseUtcTimestamp(state.LastStatusPublishedAt ?? "", out var last))
            return true;
        return now - last >= Heartbeat;
    }

    public static string Fingerprint(ReconcileStatusDocument status)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("phase", status.Phase);
            writer.WriteNumber("highest_generation", status.HighestGeneration);
            if (status.BlockedGeneration is { } blocked)
                writer.WriteNumber("blocked_generation", blocked);
            else
                writer.WriteNull("blocked_generation");
            WriteNullable(writer, "boot_id", status.Host.BootId);
            WriteNullable(writer, "blocked_agent_digest", status.Host.BlockedAgentDigest);
            WriteNullable(writer, "agent_rollback_version", status.Host.AgentRollbackVersion);
            if (status.Observed is { } observed)
            {
                writer.WriteStartObject("observed");
                writer.WriteNumber("generation", observed.Generation);
                writer.WriteString("manifest_digest", observed.ManifestDigest);
                writer.WriteString("payload_digest", observed.PayloadDigest);
                WriteNullable(writer, "key_id", observed.KeyId);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("observed");
            }

            if (status.Active is { } active)
            {
                writer.WriteStartObject("active");
                writer.WriteNumber("generation", active.Generation);
                writer.WriteString("payload_digest", active.PayloadDigest);
                writer.WriteString("state", active.State);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("active");
            }

            if (status.LastError is { } error)
            {
                writer.WriteStartObject("last_error");
                writer.WriteString("code", error.Code);
                WriteNullable(writer, "service", error.Service);
                writer.WriteString("message", error.Message);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("last_error");
            }

            writer.WriteStartObject("services");
            foreach (var (name, service) in status.Services.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(name);
                writer.WriteString("container", service.Container);
                writer.WriteString("image_digest", service.ImageDigest);
                writer.WriteString("state", service.State);
                WriteNullable(writer, "health", service.Health);
                writer.WriteNumber("restarts", service.Restarts);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Canonicalize(buffer.ToArray())));
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }
}
