using System.Text;
using System.Text.Json;
using Narwhal.Cloud;

namespace Narwhal.Reconcile;

/// <summary>A status artifact whose bytes, signature, and target checks all passed.</summary>
public sealed class VerifiedStatus
{
    public required ReconcileStatusDocument Document { get; init; }
    public required byte[] Payload { get; init; }
    public required byte[] Signature { get; init; }
    public required string ManifestDigest { get; init; }
    public required IReadOnlyList<string> VerifyingKeyIds { get; init; }
}

/// <summary>Fail-closed verification of a signed status artifact.</summary>
public static class StatusArtifactVerifier
{
    public const string DomainSeparator = "narwhal.status/v1\n";

    /// <summary>Verifies layers, digest, host signature, schema, canonical bytes, project, and target.</summary>
    /// <exception cref="CloudArtifactException">Any check failed; the status must not be consumed.</exception>
    public static VerifiedStatus Verify(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signatureLayer,
        IReadOnlyList<ArtifactLayer> layers,
        StatusKeySet statusKeys,
        TargetExpectation target,
        DateTimeOffset now,
        string manifestDigest)
    {
        if (statusKeys.IsEmpty)
            throw new CloudArtifactException(CloudArtifactErrorCodes.SignatureInvalid, "no registered status public key");

        var selection = StatusArtifactLayout.Select(layers);
        StatusArtifactLayout.ValidateBytes(selection, payload, signatureLayer);

        var payloadDigest = CloudEncoding.Sha256Digest(payload);
        var envelope = SignatureEnvelope.Parse(signatureLayer);
        if (!CloudEncoding.FixedTimeEquals(envelope.PayloadDigest, payloadDigest))
        {
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.DigestMismatch,
                "status signature envelope payload digest does not match the payload");
        }

        var message = new byte[Encoding.ASCII.GetByteCount(DomainSeparator) + payload.Length];
        var domainLength = Encoding.ASCII.GetBytes(DomainSeparator, message);
        payload.CopyTo(message.AsSpan(domainLength));

        var verifyingKeyIds = new List<string>();
        var knownKey = false;
        var knownUnexpiredKey = false;
        foreach (var signature in envelope.Signatures)
        {
            if (!statusKeys.TryFind(signature.KeyId, out _))
                continue;
            knownKey = true;
            if (!statusKeys.TryFindUnexpired(signature.KeyId, now, out var key))
                continue;
            knownUnexpiredKey = true;
            if (Ed25519.Verify(key.PublicKey, message, signature.Signature) &&
                !verifyingKeyIds.Contains(signature.KeyId, StringComparer.Ordinal))
            {
                verifyingKeyIds.Add(signature.KeyId);
            }
        }

        if (verifyingKeyIds.Count == 0)
        {
            if (!knownKey)
            {
                throw new CloudArtifactException(
                    CloudArtifactErrorCodes.UnknownKey,
                    "no status signature uses a registered status key");
            }

            if (!knownUnexpiredKey)
            {
                throw new CloudArtifactException(
                    CloudArtifactErrorCodes.KeyExpired,
                    "every matching status key has expired");
            }

            throw new CloudArtifactException(CloudArtifactErrorCodes.SignatureInvalid, "no status signature verified");
        }

        var document = StatusDocumentCodec.Decode(payload);
        if (!verifyingKeyIds.Contains(document.Host.StatusKeyId, StringComparer.Ordinal))
        {
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.SignatureInvalid,
                "host.status_key_id does not name a verifying signature");
        }

        ValidateTarget(document, target);
        return new VerifiedStatus
        {
            Document = document,
            Payload = payload.ToArray(),
            Signature = signatureLayer.ToArray(),
            ManifestDigest = manifestDigest,
            VerifyingKeyIds = verifyingKeyIds,
        };
    }

    private static void ValidateTarget(ReconcileStatusDocument status, TargetExpectation target)
    {
        if (!string.Equals(status.Project, target.Project, StringComparison.Ordinal))
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.ProjectMismatch,
                "status project does not match the configured project");

        if (!string.Equals(status.Target.Provider, target.Provider, StringComparison.Ordinal) ||
            !string.Equals(status.Target.Region, target.Region, StringComparison.Ordinal) ||
            !string.Equals(status.Target.Arch, target.Arch, StringComparison.Ordinal))
        {
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.TargetMismatch,
                "status provider, region, or arch does not match the configured target");
        }

        foreach (var (key, value) in status.Target.Selector)
        {
            if (!target.Selector.TryGetValue(key, out var expected) ||
                !string.Equals(expected, value, StringComparison.Ordinal))
            {
                throw new CloudArtifactException(
                    CloudArtifactErrorCodes.TargetMismatch,
                    "status selector does not match the configured target");
            }
        }
    }
}

/// <summary>Builds the primary and fallback OCI status manifests.</summary>
public static class StatusManifest
{
    public const string ManifestMediaType = "application/vnd.oci.image.manifest.v1+json";
    public const string ArtifactType = "application/vnd.narwhal.status.v1+json";
    public const string EmptyConfigMediaType = "application/vnd.oci.empty.v1+json";
    public const string ImageConfigMediaType = "application/vnd.oci.image.config.v1+json";
    public const string StatusSeqAnnotation = "dev.narwhal.status_seq";
    public const string LatestTag = "latest";

    private static readonly byte[] EmptyConfigBytes = "{}"u8.ToArray();

    /// <summary>The empty OCI config blob referenced by both manifest shapes.</summary>
    public static ReadOnlyMemory<byte> EmptyConfig => EmptyConfigBytes;

    /// <summary>Builds the manifest. The fallback omits <c>artifactType</c> and uses an image config.</summary>
    public static byte[] Build(
        string payloadDigest,
        long payloadSize,
        string signatureDigest,
        long signatureSize,
        long statusSeq,
        bool fallback)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WriteString("mediaType", ManifestMediaType);
            if (!fallback)
                writer.WriteString("artifactType", ArtifactType);
            writer.WriteStartObject("config");
            writer.WriteString("mediaType", fallback ? ImageConfigMediaType : EmptyConfigMediaType);
            writer.WriteString("digest", CloudEncoding.Sha256Digest(EmptyConfigBytes));
            writer.WriteNumber("size", EmptyConfigBytes.Length);
            writer.WriteEndObject();
            writer.WriteStartArray("layers");
            WriteLayer(writer, StatusArtifactLayout.PayloadMediaType, payloadDigest, payloadSize);
            WriteLayer(writer, StatusArtifactLayout.SignatureMediaType, signatureDigest, signatureSize);
            writer.WriteEndArray();
            writer.WriteStartObject("annotations");
            writer.WriteString(StatusSeqAnnotation, statusSeq.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteLayer(Utf8JsonWriter writer, string mediaType, string digest, long size)
    {
        writer.WriteStartObject();
        writer.WriteString("mediaType", mediaType);
        writer.WriteString("digest", digest);
        writer.WriteNumber("size", size);
        writer.WriteEndObject();
    }
}
