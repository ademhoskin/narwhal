using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Narwhal.Cloud;

namespace Narwhal.Reconcile;

/// <summary>The per-host Ed25519 status signing seed stored at <c>/etc/narwhal/status-key</c>.</summary>
public sealed class StatusSigningKey
{
    public const string DomainSeparator = "narwhal.status/v1\n";

    private readonly byte[] _seed;

    private StatusSigningKey(byte[] seed)
    {
        _seed = seed;
        PublicKey = Ed25519.PublicKeyFromSeed(seed);
        KeyId = Cloud.KeyId.Derive(PublicKey);
    }

    internal static StatusSigningKey FromSeed(byte[] seed) => new(seed);

    public byte[] PublicKey { get; }

    public string KeyId { get; }

    /// <summary>Reads a 32-byte seed as raw bytes, hex, or canonical base64 text.</summary>
    /// <exception cref="AgentConfigException">The file is missing, unreadable, mispermissioned, or not a seed.</exception>
    public static StatusSigningKey Load(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode;
            try
            {
                mode = File.GetUnixFileMode(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new AgentConfigException($"cannot check permissions on status key {path}: {e.Message}");
            }

            const UnixFileMode shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & shared) != 0)
                throw new AgentConfigException($"status key {path} must be mode 0600");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AgentConfigException($"cannot read status key {path}: {e.Message}");
        }

        if (!TryDecodeSeed(bytes, out var seed))
            throw new AgentConfigException($"status key {path} does not hold a 32-byte Ed25519 seed");
        return new StatusSigningKey(seed);
    }

    /// <summary>Parses raw, hex, or canonical base64 seed material.</summary>
    public static bool TryDecodeSeed(ReadOnlySpan<byte> bytes, out byte[] seed)
    {
        seed = [];
        if (bytes.Length == 32)
        {
            seed = bytes.ToArray();
            return true;
        }

        var text = Encoding.UTF8.GetString(bytes).Trim();
        if (text.Length == 64 && CloudPatterns.Hex64().IsMatch(text))
        {
            seed = Convert.FromHexString(text);
            return true;
        }

        if (CloudEncoding.TryDecodeBase64(text, out var decoded) && decoded.Length == 32)
        {
            seed = decoded;
            return true;
        }

        return false;
    }

    /// <summary>Builds the canonical signature layer for the canonical status bytes.</summary>
    public byte[] Sign(ReadOnlySpan<byte> payload)
    {
        var message = new byte[DomainSeparator.Length + payload.Length];
        Encoding.ASCII.GetBytes(DomainSeparator, message);
        payload.CopyTo(message.AsSpan(DomainSeparator.Length));
        var signature = Ed25519.Sign(_seed, message);
        return StatusSignatureEnvelope.Write(CloudEncoding.Sha256Digest(payload), KeyId, signature);
    }
}

/// <summary>Writes the <c>narwhal.signature/v1</c> envelope used by status artifacts.</summary>
public static class StatusSignatureEnvelope
{
    public static byte[] Write(string payloadDigest, string keyId, ReadOnlySpan<byte> signature)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", SignatureEnvelope.SchemaName);
            writer.WriteString("payload_digest", payloadDigest);
            writer.WriteStartArray("signatures");
            writer.WriteStartObject();
            writer.WriteString("alg", SignatureEnvelope.Ed25519Algorithm);
            writer.WriteString("key_id", keyId);
            writer.WriteString("sig", CloudEncoding.ToBase64Url(signature));
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return CanonicalJson.Canonicalize(buffer.ToArray());
    }
}

/// <summary>Derives <c>target_id</c> and the status repository for an agent config.</summary>
public static class StatusRepository
{
    public static string TargetId(TargetConfig target)
    {
        var selectorJson = SelectorJson(target.Selector);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selectorJson)))[..8];
        return $"{target.Provider}-{target.Region}-{target.Arch}-{hash}"
            .ToLowerInvariant()
            .Replace('_', '-');
    }

    public static string Derive(string project, TargetConfig target) =>
        $"{project}/state/{TargetId(target)}/status";

    private static string SelectorJson(IReadOnlyDictionary<string, string> selector)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in selector)
                writer.WriteString(key, value);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(CanonicalJson.Canonicalize(buffer.ToArray()));
    }
}
