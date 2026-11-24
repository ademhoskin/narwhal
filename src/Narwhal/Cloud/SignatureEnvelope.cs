namespace Narwhal.Cloud;

/// <summary>The signature layer payload: a detached Ed25519 multi-signature envelope.</summary>
public sealed class SignatureEnvelope
{
    public const string SchemaName = "narwhal.signature/v1";
    public const string Ed25519Algorithm = "ed25519";

    public required string PayloadDigest { get; init; }
    public required IReadOnlyList<EnvelopeSignature> Signatures { get; init; }

    /// <summary>Parses the envelope strictly, rejecting unknown fields and malformed signatures.</summary>
    public static SignatureEnvelope Parse(ReadOnlySpan<byte> utf8)
    {
        JsonValue root;
        try
        {
            root = JsonAstParser.Parse(utf8);
        }
        catch (CloudArtifactException)
        {
            throw Invalid("signature envelope is not strict JSON");
        }

        const string code = CloudArtifactErrorCodes.SignatureInvalid;
        var obj = JsonShape.Object(root, "signature", code);
        JsonShape.Members(obj, "signature", code, "schema", "payload_digest", "signatures");

        var schema = JsonShape.String(JsonShape.Required(obj, "signature", "schema", code), "signature.schema", code);
        if (!string.Equals(schema, SchemaName, StringComparison.Ordinal))
            throw Invalid("unknown signature schema");

        var payloadDigest = JsonShape.String(
            JsonShape.Required(obj, "signature", "payload_digest", code),
            "signature.payload_digest",
            code);
        if (!CloudPatterns.Digest().IsMatch(payloadDigest))
            throw Invalid("signature payload_digest is not a sha256 digest");

        var signatures = JsonShape.Array(
            JsonShape.Required(obj, "signature", "signatures", code),
            "signature.signatures",
            code);
        if (signatures.Items.Count == 0)
            throw Invalid("signature envelope has no signatures");

        var parsed = new List<EnvelopeSignature>(signatures.Items.Count);
        for (var i = 0; i < signatures.Items.Count; i++)
        {
            var path = $"signature.signatures[{i}]";
            var entry = JsonShape.Object(signatures.Items[i], path, code);
            JsonShape.Members(entry, path, code, "alg", "key_id", "sig");

            var algorithm = JsonShape.String(JsonShape.Required(entry, path, "alg", code), $"{path}.alg", code);
            if (!string.Equals(algorithm, Ed25519Algorithm, StringComparison.Ordinal))
                throw Invalid($"{path}.alg: unsupported algorithm");

            var keyId = JsonShape.String(JsonShape.Required(entry, path, "key_id", code), $"{path}.key_id", code);
            if (!CloudPatterns.KeyId().IsMatch(keyId))
                throw Invalid($"{path}.key_id: expected 16 lowercase hex characters");

            var signatureText = JsonShape.String(JsonShape.Required(entry, path, "sig", code), $"{path}.sig", code);
            if (!CloudEncoding.TryDecodeBase64Url(signatureText, out var signature) || signature.Length != 64)
                throw Invalid($"{path}.sig: expected canonical base64url for a 64-byte signature");

            parsed.Add(new EnvelopeSignature
            {
                Algorithm = algorithm,
                KeyId = keyId,
                Signature = signature,
            });
        }

        return new SignatureEnvelope { PayloadDigest = payloadDigest, Signatures = parsed };
    }

    private static CloudArtifactException Invalid(string message) =>
        new(CloudArtifactErrorCodes.SignatureInvalid, message);
}

/// <summary>One detached Ed25519 signature in the envelope.</summary>
public sealed class EnvelopeSignature
{
    public required string Algorithm { get; init; }
    public required string KeyId { get; init; }
    public required byte[] Signature { get; init; }
}
