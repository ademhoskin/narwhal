using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Narwhal.Cloud;

internal static class CloudEncoding
{
    public static bool TryDecodeBase64(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Length == 0 || value.Length % 4 != 0 || !CloudPatterns.Base64().IsMatch(value))
            return false;

        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return false;
        }

        return string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal);
    }

    public static bool TryDecodeBase64Url(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Length == 0 || value.Length % 4 == 1 || !CloudPatterns.Base64Url().IsMatch(value))
            return false;

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return false;
        }

        return string.Equals(ToBase64Url(bytes), value, StringComparison.Ordinal);
    }

    public static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryParseUtcTimestamp(string value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!CloudPatterns.Timestamp().IsMatch(value))
            return false;

        return DateTimeOffset.TryParseExact(
            value,
            ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    public static string Sha256Digest(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

/// <summary>Derives the 16-character key ID from an Ed25519 public key.</summary>
public static class KeyId
{
    public static string Derive(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != 32)
            throw new ArgumentException("an Ed25519 public key is 32 bytes", nameof(publicKey));

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(publicKey, hash);
        return Convert.ToHexStringLower(hash[..8]);
    }
}
