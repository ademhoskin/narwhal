namespace Narwhal.Cloud;

/// <summary>Layer media types for the signed status artifact.</summary>
public static class StatusArtifactLayout
{
    public const string PayloadMediaType = "application/vnd.narwhal.status.v1+json";
    public const string SignatureMediaType = "application/vnd.narwhal.status-signature.v1+json";

    /// <summary>Selects exactly one status payload layer and one status signature layer.</summary>
    public static SelectedArtifactLayers Select(IReadOnlyList<ArtifactLayer> layers)
    {
        ArtifactLayer? payload = null;
        ArtifactLayer? signature = null;

        foreach (var layer in layers)
        {
            if (string.Equals(layer.MediaType, PayloadMediaType, StringComparison.Ordinal))
            {
                if (payload is not null)
                    throw Invalid("status artifact has more than one payload layer");
                payload = layer;
            }
            else if (string.Equals(layer.MediaType, SignatureMediaType, StringComparison.Ordinal))
            {
                if (signature is not null)
                    throw Invalid("status artifact has more than one signature layer");
                signature = layer;
            }
        }

        if (payload is null)
            throw Invalid("status artifact has no payload layer");
        if (signature is null)
            throw Invalid("status artifact has no signature layer");

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
                "status payload layer descriptor does not match the payload bytes");
        }

        if (!string.Equals(selection.Signature.Digest, CloudEncoding.Sha256Digest(signature), StringComparison.Ordinal) ||
            selection.Signature.Size != signature.Length)
        {
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.DigestMismatch,
                "status signature layer descriptor does not match the signature bytes");
        }
    }

    private static CloudArtifactException Invalid(string message) =>
        new(CloudArtifactErrorCodes.LayerInvalid, message);
}
