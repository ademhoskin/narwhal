namespace Narwhal.Cloud;

/// <summary>The effective trust set, initialized once from boot pins and replaced by signed rotations.</summary>
public sealed class TrustSet
{
    private readonly Dictionary<string, TrustedKey> _byKeyId;

    private TrustSet(IReadOnlyList<TrustedKey> keys)
    {
        Keys = keys;
        _byKeyId = new Dictionary<string, TrustedKey>(StringComparer.Ordinal);
        foreach (var key in keys)
            _byKeyId.Add(key.KeyId, key);
    }

    public IReadOnlyList<TrustedKey> Keys { get; }

    /// <summary>Parses a non-empty JSON array of trusted keys and derives every key ID.</summary>
    /// <exception cref="CloudArtifactException">The set is malformed, empty, duplicated, or has a mismatched key ID.</exception>
    public static TrustSet Parse(ReadOnlySpan<byte> utf8)
    {
        JsonValue root;
        try
        {
            root = JsonAstParser.Parse(utf8);
        }
        catch (CloudArtifactException)
        {
            throw Invalid("trust set is not strict JSON");
        }

        var array = root as JsonArray ?? throw Invalid("trust set must be a JSON array");
        if (array.Items.Count == 0)
            throw Invalid("trust set must contain at least one key");

        var keys = new List<TrustedKey>(array.Items.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < array.Items.Count; i++)
        {
            var key = TrustedKeyParser.Parse(
                array.Items[i],
                $"trust[{i}]",
                CloudArtifactErrorCodes.TrustSetInvalid);
            if (!seen.Add(key.KeyId))
                throw Invalid("trust set contains a duplicate key ID");
            keys.Add(key);
        }

        return new TrustSet(keys);
    }

    /// <summary>Builds and validates a trust set from already-decoded keys.</summary>
    public static TrustSet FromKeys(IEnumerable<TrustedKey> keys)
    {
        var list = keys.ToList();
        if (list.Count == 0)
            throw Invalid("trust set must contain at least one key");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in list)
        {
            if (key.PublicKey.Length != 32)
                throw Invalid("trusted public key must be 32 bytes");
            var derived = KeyId.Derive(key.PublicKey);
            if (!CloudEncoding.FixedTimeEquals(derived, key.KeyId))
                throw Invalid("trusted key ID does not match its public key");
            if (!seen.Add(key.KeyId))
                throw Invalid("trust set contains a duplicate key ID");
        }

        return new TrustSet(list);
    }

    /// <summary>True when the key ID exists and its not_after has not passed at the supplied time.</summary>
    public bool TryFindUnexpired(string keyId, DateTimeOffset now, out TrustedKey key)
    {
        if (_byKeyId.TryGetValue(keyId, out var found) && (found.NotAfter is null || now < found.NotAfter.Value))
        {
            key = found;
            return true;
        }

        key = null!;
        return false;
    }

    /// <summary>Finds a key by ID without considering expiry.</summary>
    public bool TryFind(string keyId, out TrustedKey key) => _byKeyId.TryGetValue(keyId, out key!);

    private static CloudArtifactException Invalid(string message) =>
        new(CloudArtifactErrorCodes.TrustSetInvalid, message);
}

internal static class TrustedKeyParser
{
    public static TrustedKey Parse(JsonValue value, string path, string code)
    {
        var obj = JsonShape.Object(value, path, code);
        JsonShape.Members(obj, path, code, "key_id", "public_key", "not_after");

        var keyId = JsonShape.String(JsonShape.Required(obj, path, "key_id", code), $"{path}.key_id", code);
        if (!CloudPatterns.KeyId().IsMatch(keyId))
            throw JsonShape.Error(code, $"{path}.key_id", "expected 16 lowercase hex characters");

        var publicKeyText = JsonShape.String(JsonShape.Required(obj, path, "public_key", code), $"{path}.public_key", code);
        if (!CloudEncoding.TryDecodeBase64(publicKeyText, out var publicKey) || publicKey.Length != 32)
            throw JsonShape.Error(code, $"{path}.public_key", "expected canonical base64 for a 32-byte key");

        var derived = KeyId.Derive(publicKey);
        if (!CloudEncoding.FixedTimeEquals(derived, keyId))
            throw JsonShape.Error(code, $"{path}.key_id", "key ID does not match the public key");

        DateTimeOffset? notAfter = null;
        if (JsonShape.Optional(obj, "not_after", out var notAfterValue))
        {
            var text = JsonShape.String(notAfterValue, $"{path}.not_after", code);
            if (!CloudEncoding.TryParseUtcTimestamp(text, out var timestamp))
                throw JsonShape.Error(code, $"{path}.not_after", "expected an RFC 3339 UTC timestamp");
            notAfter = timestamp;
        }

        return new TrustedKey { KeyId = keyId, PublicKey = publicKey, NotAfter = notAfter };
    }
}
