using System.Globalization;
using System.Text.Json;
using Narwhal.Cloud;

namespace Narwhal.Reconcile;

/// <summary>One locally retained signed status artifact.</summary>
public sealed class StatusSnapshot
{
    public required long Epoch { get; init; }
    public required long StatusSeq { get; init; }
    public required byte[] Payload { get; init; }
    public required byte[] Signature { get; init; }
    public required byte[] Manifest { get; init; }
    public required string ManifestDigest { get; init; }
    public required string RecordedAt { get; init; }
}

/// <summary>Bounded local status history and publish queue under the state directory.</summary>
public sealed class StatusStore
{
    public const int Limit = 64;
    private const string SnapshotSchema = "narwhal.status-snapshot/v1";

    private readonly string _historyDirectory;
    private readonly string _queueDirectory;

    public StatusStore(string stateDirectory)
    {
        _historyDirectory = Path.Combine(stateDirectory, "status-history");
        _queueDirectory = Path.Combine(stateDirectory, "status-queue");
    }

    public string HistoryDirectory => _historyDirectory;

    public string QueueDirectory => _queueDirectory;

    public void SaveHistory(StatusSnapshot snapshot) => WriteBounded(_historyDirectory, snapshot);

    public void Enqueue(StatusSnapshot snapshot) => WriteBounded(_queueDirectory, snapshot);

    public void ClearQueue()
    {
        if (!Directory.Exists(_queueDirectory))
            return;
        foreach (var file in Directory.EnumerateFiles(_queueDirectory, "*.json"))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ReconcileException(
                    "status_io_failed",
                    $"cannot clear {file}: {e.Message}",
                    ReconcileExitCode.Config);
            }
        }
    }

    public int QueueCount => Count(_queueDirectory);

    public int HistoryCount => Count(_historyDirectory);

    /// <summary>Reads the newest queued snapshot, or null when the queue is empty.</summary>
    public StatusSnapshot? NewestQueued()
    {
        var newest = NewestFile(_queueDirectory);
        return newest is null ? null : TryRead(newest);
    }

    /// <summary>Reads the newest retained history snapshot, or null when history is empty.</summary>
    public StatusSnapshot? NewestHistory()
    {
        var newest = NewestFile(_historyDirectory);
        return newest is null ? null : TryRead(newest);
    }

    private static int Count(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Count() : 0;

    private static void WriteBounded(string directory, StatusSnapshot snapshot)
    {
        var name = string.Format(
            CultureInfo.InvariantCulture,
            "{0:D19}-{1:D19}.json",
            snapshot.Epoch,
            snapshot.StatusSeq);
        ReconcileStore.WriteAtomic(Path.Combine(directory, name), Encode(snapshot));
        Trim(directory);
    }

    private static void Trim(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*.json")
            .OrderByDescending(file => Path.GetFileName(file) ?? "", StringComparer.Ordinal)
            .ToList();
        foreach (var file in files.Skip(Limit))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string? NewestFile(string directory)
    {
        if (!Directory.Exists(directory))
            return null;
        return Directory.EnumerateFiles(directory, "*.json")
            .OrderByDescending(file => Path.GetFileName(file) ?? "", StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static byte[] Encode(StatusSnapshot snapshot)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", SnapshotSchema);
            writer.WriteNumber("epoch", snapshot.Epoch);
            writer.WriteNumber("status_seq", snapshot.StatusSeq);
            writer.WriteString("manifest_digest", snapshot.ManifestDigest);
            writer.WriteString("recorded_at", snapshot.RecordedAt);
            writer.WriteBase64String("payload", snapshot.Payload);
            writer.WriteBase64String("signature", snapshot.Signature);
            writer.WriteBase64String("manifest", snapshot.Manifest);
            writer.WriteEndObject();
        }

        return CanonicalJson.Canonicalize(buffer.ToArray());
    }

    private static StatusSnapshot? TryRead(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("schema", out var schema) ||
                !string.Equals(schema.GetString(), SnapshotSchema, StringComparison.Ordinal))
            {
                return null;
            }

            return new StatusSnapshot
            {
                Epoch = root.GetProperty("epoch").GetInt64(),
                StatusSeq = root.GetProperty("status_seq").GetInt64(),
                ManifestDigest = root.GetProperty("manifest_digest").GetString() ?? "",
                RecordedAt = root.GetProperty("recorded_at").GetString() ?? "",
                Payload = root.GetProperty("payload").GetBytesFromBase64(),
                Signature = root.GetProperty("signature").GetBytesFromBase64(),
                Manifest = root.GetProperty("manifest").GetBytesFromBase64(),
            };
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return null;
        }
    }
}
