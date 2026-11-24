namespace Narwhal.Cloud;

/// <summary>A layer descriptor from an OCI manifest, without any registry dependency.</summary>
public readonly record struct ArtifactLayer(string MediaType, string Digest, long Size);

/// <summary>The single payload and signature layers selected from an artifact manifest.</summary>
public sealed record SelectedArtifactLayers(ArtifactLayer Payload, ArtifactLayer Signature);

/// <summary>Provider-neutral layer selection for desired and signature media types.</summary>
public static class DesiredArtifactLayout
{
    public const string PayloadMediaType = "application/vnd.narwhal.desired.v1+json";
    public const string SignatureMediaType = "application/vnd.narwhal.signature.v1+json";

    /// <summary>Selects exactly one payload layer and one signature layer.</summary>
    public static SelectedArtifactLayers Select(IReadOnlyList<ArtifactLayer> layers)
    {
        ArtifactLayer? payload = null;
        ArtifactLayer? signature = null;

        foreach (var layer in layers)
        {
            if (string.Equals(layer.MediaType, PayloadMediaType, StringComparison.Ordinal))
            {
                if (payload is not null)
                    throw Invalid("artifact has more than one payload layer");
                payload = layer;
            }
            else if (string.Equals(layer.MediaType, SignatureMediaType, StringComparison.Ordinal))
            {
                if (signature is not null)
                    throw Invalid("artifact has more than one signature layer");
                signature = layer;
            }
        }

        if (payload is null)
            throw Invalid("artifact has no payload layer");
        if (signature is null)
            throw Invalid("artifact has no signature layer");

        return new SelectedArtifactLayers(payload.Value, signature.Value);
    }

    internal static void ValidateBytes(
        SelectedArtifactLayers selection,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature)
    {
        if (!string.Equals(selection.Payload.Digest, CloudEncoding.Sha256Digest(payload), StringComparison.Ordinal) ||
            selection.Payload.Size != payload.Length)
        {
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.DigestMismatch,
                "payload layer descriptor does not match the payload bytes");
        }

        if (!string.Equals(selection.Signature.Digest, CloudEncoding.Sha256Digest(signature), StringComparison.Ordinal) ||
            selection.Signature.Size != signature.Length)
        {
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.DigestMismatch,
                "signature layer descriptor does not match the signature bytes");
        }
    }

    private static CloudArtifactException Invalid(string message) =>
        new(CloudArtifactErrorCodes.LayerInvalid, message);
}
