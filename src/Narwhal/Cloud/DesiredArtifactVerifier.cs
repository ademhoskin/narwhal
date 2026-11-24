using System.Text;

namespace Narwhal.Cloud;

/// <summary>Inputs required to accept a desired-state artifact.</summary>
public sealed class ArtifactVerificationContext
{
    public required TrustSet TrustSet { get; init; }
    public required TargetExpectation Target { get; init; }
    public required DateTimeOffset Now { get; init; }
    public required GenerationState Generation { get; init; }

    /// <summary>The artifact manifest layers. Exactly one payload and one signature layer are required.</summary>
    public required IReadOnlyList<ArtifactLayer> Layers { get; init; }
}

/// <summary>The verified payload and the checks it passed.</summary>
public sealed class VerifiedDesiredArtifact
{
    public required DesiredDocument Document { get; init; }
    public required string PayloadDigest { get; init; }
    public required GenerationDecision Generation { get; init; }
    public required IReadOnlyList<string> VerifyingKeyIds { get; init; }

    /// <summary>
    /// The previous payload declared by the signed document. This layer only checks that
    /// <c>generation &gt; previous.generation</c>. Before applying or rolling back, the registry
    /// layer must resolve <c>g&lt;previous.generation&gt;</c>, verify that artifact against the
    /// effective trust set, and require its payload digest and manifest digest to match this
    /// reference. No registry I/O is performed here.
    /// </summary>
    public required PreviousArtifact? Previous { get; init; }

    /// <summary>The payload's replacement trust set, or null when the payload does not rotate trust.</summary>
    public TrustSet? NextTrustSet { get; init; }
}

/// <summary>Fail-closed verification of a signed desired-state artifact.</summary>
public static class DesiredArtifactVerifier
{
    public const string DomainSeparator = "narwhal.desired/v1\n";

    /// <summary>Verifies layers, digest, signature, payload schema, semantics, target, project, and generation.</summary>
    /// <exception cref="CloudArtifactException">Any check failed; nothing from the artifact may be applied.</exception>
    public static VerifiedDesiredArtifact Verify(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signatureLayer,
        ArtifactVerificationContext context)
    {
        if (context.Layers is null)
        {
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.LayerInvalid,
                "manifest layer descriptors are required");
        }

        if (context.Generation is null)
        {
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.ContextInvalid,
                "persisted generation state is required");
        }

        var selection = DesiredArtifactLayout.Select(context.Layers);
        DesiredArtifactLayout.ValidateBytes(selection, payload, signatureLayer);

        var payloadDigest = CloudEncoding.Sha256Digest(payload);

        var envelope = SignatureEnvelope.Parse(signatureLayer);
        if (!CloudEncoding.FixedTimeEquals(envelope.PayloadDigest, payloadDigest))
        {
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.DigestMismatch,
                "signature envelope payload digest does not match the payload");
        }

        var message = new byte[Encoding.ASCII.GetByteCount(DomainSeparator) + payload.Length];
        var domainLength = Encoding.ASCII.GetBytes(DomainSeparator, message);
        payload.CopyTo(message.AsSpan(domainLength));

        var verifyingKeyIds = new List<string>();
        foreach (var signature in envelope.Signatures)
        {
            if (!context.TrustSet.TryFindUnexpired(signature.KeyId, context.Now, out var key))
                continue;
            if (Ed25519.Verify(key.PublicKey, message, signature.Signature))
            {
                if (!verifyingKeyIds.Contains(signature.KeyId, StringComparer.Ordinal))
                    verifyingKeyIds.Add(signature.KeyId);
            }
        }

        if (verifyingKeyIds.Count == 0)
            throw new CloudArtifactException(CloudArtifactErrorCodes.SignatureInvalid, "no signature verified");

        JsonValue root;
        try
        {
            root = JsonAstParser.Parse(payload);
        }
        catch (CloudArtifactException exception)
        {
            throw new CloudArtifactException(exception.Code, "payload is not strict JSON");
        }

        var canonical = CanonicalJsonWriter.Write(root);
        if (!canonical.AsSpan().SequenceEqual(payload))
            throw new CloudArtifactException(CloudArtifactErrorCodes.NonCanonical, "payload bytes are not canonical");

        var document = DesiredSchema.Validate(root);
        DesiredSemantics.Validate(document);

        TrustSet? nextTrust = null;
        if (document.Trust is not null)
        {
            nextTrust = TrustSet.FromKeys(document.Trust);
            foreach (var keyId in verifyingKeyIds)
            {
                if (!nextTrust.TryFind(keyId, out _))
                {
                    throw new CloudArtifactException(
                        CloudArtifactErrorCodes.SemanticInvalid,
                        "replacement trust set does not contain every key that verified the artifact");
                }
            }
        }

        TargetValidation.Validate(document, context.Target);

        var decision = GenerationCheck.Evaluate(document.Generation, payloadDigest, context.Generation);
        switch (decision)
        {
            case GenerationDecision.Replay:
                throw new CloudArtifactException(
                    CloudArtifactErrorCodes.GenerationReplay,
                    "generation is lower than the highest accepted generation");
            case GenerationDecision.Conflict:
                throw new CloudArtifactException(
                    CloudArtifactErrorCodes.GenerationConflict,
                    "a different payload already used this generation");
            case GenerationDecision.Blocked:
                throw new CloudArtifactException(
                    CloudArtifactErrorCodes.GenerationBlocked,
                    "generation is blocked after a failed health gate");
        }

        return new VerifiedDesiredArtifact
        {
            Document = document,
            PayloadDigest = payloadDigest,
            Generation = decision,
            VerifyingKeyIds = verifyingKeyIds,
            Previous = document.Previous,
            NextTrustSet = nextTrust,
        };
    }
}
