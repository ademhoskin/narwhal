namespace Narwhal.Cloud;

/// <summary>Registered per-host status public keys used to verify <c>narwhal.status/v1</c>.</summary>
public sealed class StatusKeySet
{
    private readonly Dictionary<string, TrustedKey> _byKeyId;

    private StatusKeySet(IReadOnlyList<TrustedKey> keys)
    {
        Keys = keys;
        _byKeyId = new Dictionary<string, TrustedKey>(StringComparer.Ordinal);
        foreach (var key in keys)
            _byKeyId.Add(key.KeyId, key);
    }

    public IReadOnlyList<TrustedKey> Keys { get; }

    public bool IsEmpty => Keys.Count == 0;

    /// <summary>Builds a set from decoded keys and derives every key ID.</summary>
    /// <exception cref="CloudArtifactException">A key is malformed, duplicated, or its ID does not match.</exception>
    public static StatusKeySet FromKeys(IEnumerable<TrustedKey> keys)
    {
        var list = keys.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in list)
        {
            if (key.PublicKey.Length != 32)
                throw Invalid("status public key must be 32 bytes");
            var derived = KeyId.Derive(key.PublicKey);
            if (!CloudEncoding.FixedTimeEquals(derived, key.KeyId))
                throw Invalid("status key ID does not match its public key");
            if (!seen.Add(key.KeyId))
                throw Invalid("status key set contains a duplicate key ID");
        }

        return new StatusKeySet(list);
    }

    /// <summary>Builds a set from base64 public keys, deriving each key ID.</summary>
    public static StatusKeySet FromBase64(IEnumerable<string> publicKeys, string path = "status_public_keys")
    {
        var keys = new List<TrustedKey>();
        foreach (var encoded in publicKeys)
        {
            if (!CloudEncoding.TryDecodeBase64(encoded, out var publicKey) || publicKey.Length != 32)
                throw Invalid($"{path}: expected canonical base64 for a 32-byte status public key");
            keys.Add(new TrustedKey { KeyId = KeyId.Derive(publicKey), PublicKey = publicKey });
        }

        return FromKeys(keys);
    }

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

    public bool TryFind(string keyId, out TrustedKey key) => _byKeyId.TryGetValue(keyId, out key!);

    private static CloudArtifactException Invalid(string message) =>
        new(CloudArtifactErrorCodes.TrustSetInvalid, message);
}
