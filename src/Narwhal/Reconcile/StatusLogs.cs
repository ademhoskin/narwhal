using System.Text;

namespace Narwhal.Reconcile;

/// <summary>Bounds and redacts the status log tail before it is signed.</summary>
public static class StatusLogs
{
    public const int MaxLines = 200;
    public const int MaxBytes = 32 * 1024;
    public const string Replacement = "[redacted]";

    /// <summary>
    /// Replaces resolved secret values and configured patterns, then keeps the newest lines
    /// within the line and byte limits.
    /// </summary>
    public static List<StatusLogLine> BoundAndRedact(
        IEnumerable<StatusLogLine> lines,
        IReadOnlyCollection<string>? resolvedSecrets = null,
        IReadOnlyCollection<string>? patterns = null)
    {
        var secretValues = (resolvedSecrets ?? [])
            .Where(value => !string.IsNullOrEmpty(value))
            .OrderByDescending(value => value.Length)
            .ToList();
        var patternValues = (patterns ?? [])
            .Where(value => !string.IsNullOrEmpty(value))
            .ToList();

        var kept = new List<StatusLogLine>();
        var bytes = 0;
        foreach (var line in lines.Reverse())
        {
            if (kept.Count >= MaxLines)
                break;

            var text = Redact(line.Line, secretValues, patternValues);
            var size = Encoding.UTF8.GetByteCount(text);
            if (size > MaxBytes)
                continue;
            if (bytes + size > MaxBytes)
                break;

            kept.Add(new StatusLogLine { Service = line.Service, At = line.At, Line = text });
            bytes += size;
        }

        kept.Reverse();
        return kept;
    }

    private static string Redact(string line, IReadOnlyList<string> secrets, IReadOnlyList<string> patterns)
    {
        var result = line;
        foreach (var secret in secrets)
            result = result.Replace(secret, Replacement, StringComparison.Ordinal);
        foreach (var pattern in patterns)
            result = result.Replace(pattern, Replacement, StringComparison.Ordinal);
        return result;
    }

    /// <summary>Redacts resolved secret values and configured patterns from arbitrary text.</summary>
    public static string RedactText(
        string text,
        IReadOnlyCollection<string>? resolvedSecrets = null,
        IReadOnlyCollection<string>? patterns = null)
    {
        var secretValues = (resolvedSecrets ?? [])
            .Where(value => !string.IsNullOrEmpty(value))
            .OrderByDescending(value => value.Length)
            .ToList();
        var patternValues = (patterns ?? []).Where(value => !string.IsNullOrEmpty(value)).ToList();
        return Redact(text, secretValues, patternValues);
    }
}
