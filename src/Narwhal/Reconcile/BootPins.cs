using System.Text;
using Narwhal.Cloud;

namespace Narwhal.Reconcile;

/// <summary>Validates the immutable boot-pin directory described in design section 2.5.</summary>
public static class BootPins
{
    public const string Prefix = "narwhal-ed25519-v1 ";

    public static void Validate(string directory)
    {
        if (!Directory.Exists(directory))
            throw new AgentConfigException($"boot pin directory not found: {directory}");

        var files = Directory.GetFiles(directory, "*.pub", SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
            throw new AgentConfigException($"boot pin directory is empty: {directory}");

        foreach (var file in files)
            ValidateFile(file);
    }

    private static void ValidateFile(string path)
    {
        var keyId = Path.GetFileNameWithoutExtension(path);
        if (!CloudPatterns.KeyId().IsMatch(keyId))
            throw new AgentConfigException($"{path}: boot pin file name must be a 16-character key ID");

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AgentConfigException($"cannot read boot pin {path}: {e.Message}");
        }

        if (lines.Length is < 1 or > 2)
            throw new AgentConfigException($"{path}: expected a key line and an optional not_after line");

        var keyLine = lines[0];
        if (!keyLine.StartsWith(Prefix, StringComparison.Ordinal))
            throw new AgentConfigException($"{path}: expected '{Prefix.Trim()}'");
        var encoded = keyLine[Prefix.Length..].Trim();
        if (!CloudEncoding.TryDecodeBase64(encoded, out var publicKey) || publicKey.Length != 32)
            throw new AgentConfigException($"{path}: expected canonical base64 for a 32-byte Ed25519 key");

        var derived = KeyId.Derive(publicKey);
        if (!string.Equals(derived, keyId, StringComparison.Ordinal))
            throw new AgentConfigException($"{path}: key ID does not match the public key");

        if (lines.Length == 2)
        {
            var notAfterLine = lines[1];
            if (!notAfterLine.StartsWith("not_after=", StringComparison.Ordinal))
                throw new AgentConfigException($"{path}: expected 'not_after=<timestamp>'");
            if (!CloudEncoding.TryParseUtcTimestamp(notAfterLine["not_after=".Length..], out _))
                throw new AgentConfigException($"{path}: invalid not_after timestamp");
        }
    }
}
