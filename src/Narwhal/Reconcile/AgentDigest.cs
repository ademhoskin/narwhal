using System.Text.RegularExpressions;

namespace Narwhal.Reconcile;

/// <summary>
/// Canonical agent-digest handling. Desired payloads carry a bare SHA-256 hex value for
/// <c>agent.binary_sha256</c>; state and signed status carry <c>sha256:&lt;hex&gt;</c>.
/// </summary>
public static class AgentDigest
{
    public const string Prefix = "sha256:";

    private static readonly Regex NullableDigest = new(
        "^sha256:[0-9a-f]{64}$",
        RegexOptions.CultureInvariant);

    private static readonly Regex BareSha256 = new(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant);

    /// <summary>Normalizes a bare legacy value or a canonical digest, and rejects anything else.</summary>
    public static string Normalize(string value)
    {
        if (NullableDigest.IsMatch(value))
            return value;
        if (BareSha256.IsMatch(value))
            return Prefix + value;
        throw new AgentConfigException($"invalid agent digest '{value}'");
    }

    public static string? TryNormalize(string? value) =>
        value is null ? null : Normalize(value);

    public static string FromBinarySha256(string binarySha256) =>
        BareSha256.IsMatch(binarySha256)
            ? Prefix + binarySha256
            : throw new AgentConfigException($"invalid agent binary SHA-256 '{binarySha256}'");
}
