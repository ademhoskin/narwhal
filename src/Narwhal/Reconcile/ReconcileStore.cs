using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Narwhal.Cloud;

namespace Narwhal.Reconcile;

/// <summary>Content-addressed copy of the last verified artifact, used when the registry is unavailable.</summary>
public sealed record CachedArtifact(byte[] Payload, byte[] Signature, string ManifestDigest, byte[] Manifest);

/// <summary>Local state, journal, artifact cache, trust set, and reconcile lock.</summary>
public sealed class ReconcileStore
{
    private readonly string _stateDirectory;

    public ReconcileStore(string stateDirectory)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
        StatePath = Path.Combine(_stateDirectory, "state.json");
        JournalPath = Path.Combine(_stateDirectory, "journal.json");
        LockPath = Path.Combine(_stateDirectory, "lock");
        ArtifactDirectory = Path.Combine(_stateDirectory, "artifacts");
        TrustPath = Path.Combine(_stateDirectory, "trust.json");
    }

    public string StatePath { get; }
    public string JournalPath { get; }
    public string LockPath { get; }
    public string ArtifactDirectory { get; }
    public string TrustPath { get; }

    public ReconcileState LoadState()
    {
        if (!File.Exists(StatePath))
            return new ReconcileState();

        ReconcileState? state;
        try
        {
            state = JsonSerializer.Deserialize(File.ReadAllBytes(StatePath), ReconcileJsonContext.Compact.ReconcileState);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new AgentConfigException($"cannot read {StatePath}: {e.Message}");
        }

        if (state is null)
            throw new AgentConfigException($"cannot read {StatePath}: empty document");
        if (!string.Equals(state.Schema, ReconcileSchema.State, StringComparison.Ordinal))
            throw new AgentConfigException($"cannot read {StatePath}: unsupported schema '{state.Schema}'");
        if (state.HighestGeneration < 0 || state.StatusSeq < 0 || state.StatusEpoch < 0)
            throw new AgentConfigException($"cannot read {StatePath}: invalid generation counters");

        NormalizeAgentDigests(state);
        return state;
    }

    /// <summary>
    /// Loads state for a reconcile cycle. A missing or corrupt file is state loss, not a fatal error:
    /// the caller restores ordering from signed status and bumps the epoch.
    /// </summary>
    public ReconcileState LoadStateForRecovery(out bool lost, out string? warning)
    {
        lost = false;
        warning = null;
        if (!File.Exists(StatePath))
        {
            lost = true;
            return new ReconcileState();
        }

        try
        {
            var state = JsonSerializer.Deserialize(
                File.ReadAllBytes(StatePath),
                ReconcileJsonContext.Compact.ReconcileState);
            if (state is null ||
                !string.Equals(state.Schema, ReconcileSchema.State, StringComparison.Ordinal) ||
                state.HighestGeneration < 0 ||
                state.StatusSeq < 0 ||
                state.StatusEpoch < 0)
            {
                lost = true;
                warning = $"ignoring invalid state {StatePath}; status epoch will advance";
                return new ReconcileState();
            }

            NormalizeAgentDigests(state);
            return state;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or AgentConfigException)
        {
            lost = true;
            warning = $"ignoring unreadable state {StatePath}: {e.Message}; status epoch will advance";
            return new ReconcileState();
        }
    }

    private static void NormalizeAgentDigests(ReconcileState state)
    {
        state.BlockedAgentDigest = AgentDigest.TryNormalize(state.BlockedAgentDigest);
        state.LastAgentRevertDigest = AgentDigest.TryNormalize(state.LastAgentRevertDigest);
    }

    public void SaveState(ReconcileState state)
    {
        NormalizeAgentDigests(state);
        WriteAtomic(StatePath, JsonSerializer.SerializeToUtf8Bytes(state, ReconcileJsonContext.Compact.ReconcileState));
    }

    public ReconcileJournal? LoadJournal(out string? warning)
    {
        warning = null;
        if (!File.Exists(JournalPath))
            return null;

        try
        {
            var journal = JsonSerializer.Deserialize(
                File.ReadAllBytes(JournalPath),
                ReconcileJsonContext.Compact.ReconcileJournal);
            if (journal is null || !string.Equals(journal.Schema, ReconcileSchema.Journal, StringComparison.Ordinal))
            {
                warning = $"ignoring invalid journal {JournalPath}";
                return null;
            }

            return journal;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            warning = $"ignoring unreadable journal {JournalPath}: {e.Message}";
            return null;
        }
    }

    public void SaveJournal(ReconcileJournal journal) =>
        WriteAtomic(JournalPath, JsonSerializer.SerializeToUtf8Bytes(journal, ReconcileJsonContext.Compact.ReconcileJournal));

    public void ClearJournal()
    {
        try
        {
            File.Delete(JournalPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ReconcileException(
                "state_io_failed",
                $"cannot clear {JournalPath}: {e.Message}",
                ReconcileExitCode.Config);
        }
    }

    public bool TryLoadArtifact(string payloadDigest, out CachedArtifact artifact)
    {
        artifact = null!;
        var payloadPath = Path.Combine(ArtifactDirectory, payloadDigest + ".json");
        var signaturePath = Path.Combine(ArtifactDirectory, payloadDigest + ".sig.json");
        var manifestPath = Path.Combine(ArtifactDirectory, payloadDigest + ".manifest.json");
        if (!File.Exists(payloadPath) || !File.Exists(signaturePath) || !File.Exists(manifestPath))
            return false;

        var manifestDigest = TryReadManifestDigest(manifestPath, out var manifestBytes);
        if (manifestDigest is null)
            return false;

        artifact = new CachedArtifact(
            File.ReadAllBytes(payloadPath),
            File.ReadAllBytes(signaturePath),
            manifestDigest,
            manifestBytes!);
        return true;
    }

    public void SaveArtifact(string payloadDigest, byte[] payload, byte[] signature, string manifestDigest, byte[] manifest)
    {
        Directory.CreateDirectory(ArtifactDirectory);
        WriteAtomic(Path.Combine(ArtifactDirectory, payloadDigest + ".json"), payload);
        WriteAtomic(Path.Combine(ArtifactDirectory, payloadDigest + ".sig.json"), signature);
        WriteAtomic(Path.Combine(ArtifactDirectory, payloadDigest + ".manifest.json"), ManifestWrapper.Encode(manifestDigest, manifest));
    }

    public TrustSet LoadTrustSet()
    {
        if (!File.Exists(TrustPath))
            throw new ReconcileException(
                CloudArtifactErrorCodes.TrustSetInvalid,
                $"effective trust set not found: {TrustPath}",
                ReconcileExitCode.Verification);

        try
        {
            return TrustSet.Parse(File.ReadAllBytes(TrustPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ReconcileException(
                CloudArtifactErrorCodes.TrustSetInvalid,
                $"cannot read {TrustPath}: {e.Message}",
                ReconcileExitCode.Verification);
        }
    }

    public void SaveTrustSet(TrustSet trustSet) =>
        WriteAtomic(TrustPath, TrustJson.Write(trustSet));

    public async Task<ReconcileLock?> TryAcquireLockAsync(int timeoutSeconds, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_stateDirectory);
        return await ReconcileLock.AcquireAsync(LockPath, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
    }

    internal static void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            directory = ".";
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(
            directory,
            "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("n") + ".tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            if (!OperatingSystem.IsWindows() && !string.IsNullOrEmpty(directory))
                SyncDirectory(directory);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    internal static void SyncDirectory(string path)
    {
        var fd = open(path, OpenReadOnly | OpenDirectory, 0);
        if (fd < 0)
            throw new IOException($"cannot open directory for fsync: {path} (errno {Marshal.GetLastWin32Error()})");

        try
        {
            if (fsync(fd) != 0)
                throw new IOException($"cannot fsync directory: {path} (errno {Marshal.GetLastWin32Error()})");
        }
        finally
        {
            close(fd);
        }
    }

    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [DllImport("libc")]
    private static extern int close(int fd);

    private static string? TryReadManifestDigest(string path, out byte[]? manifestBytes)
    {
        manifestBytes = null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("digest", out var digest) || digest.GetString() is not { } value)
                return null;
            if (!root.TryGetProperty("manifest", out var manifest))
                return null;

            manifestBytes = manifest.GetBytesFromBase64();
            return value;
        }
        catch (Exception e) when (e is JsonException or IOException or FormatException)
        {
            return null;
        }
    }
}

internal static class TrustJson
{
    public static byte[] Write(TrustSet trustSet)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var key in trustSet.Keys.OrderBy(key => key.KeyId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("key_id", key.KeyId);
                writer.WriteString("public_key", Convert.ToBase64String(key.PublicKey));
                if (key.NotAfter is { } notAfter)
                    writer.WriteString("not_after", notAfter.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return buffer.ToArray();
    }
}

internal static class ManifestWrapper
{
    public static byte[] Encode(string digest, ReadOnlySpan<byte> manifest)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("digest", digest);
            writer.WriteBase64String("manifest", manifest);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}

/// <summary>Exclusive advisory lock on the state directory, matching the design's flock contract.</summary>
public sealed class ReconcileLock : IDisposable
{
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int LockUnlock = 8;

    private readonly int _fd;
    private bool _disposed;

    private ReconcileLock(int fd) => _fd = fd;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        flock(_fd, LockUnlock);
        close(_fd);
    }

    public static async Task<ReconcileLock?> AcquireAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var fd = open(path, OpenReadWrite | OpenCreate | OpenCloseOnExec, Convert.ToInt32("600", 8));
        if (fd < 0)
            throw new IOException($"cannot open reconcile lock {path} (errno {Marshal.GetLastWin32Error()})");

        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (flock(fd, LockExclusive | LockNonBlocking) == 0)
                return new ReconcileLock(fd);

            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                close(fd);
                return null;
            }

            await Task.Delay(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private const int OpenReadWrite = 2;
    private const int OpenCreate = 0x40;
    private const int OpenCloseOnExec = 0x80000;

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);
}
