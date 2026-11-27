using System.Globalization;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Narwhal.Cli;
using Narwhal.Cloud;

namespace Narwhal.Reconcile;

/// <summary>The result of comparing a signed agent block against the installed binary.</summary>
public enum AgentUpgradeAction
{
    None,
    UpToDate,
    Staged,
    Blocked,
    Failed,
    DowngradeRefused,
}

/// <summary>
/// One upgrade decision. <see cref="DigestBlocked"/> means the failed binary digest must be
/// recorded so the same binary is never staged again.
/// </summary>
public sealed record AgentUpgradeResult(
    AgentUpgradeAction Action,
    string? Message = null,
    bool DigestBlocked = false);

public sealed record AgentPendingUpgrade(
    string Version,
    string Digest,
    string BinarySha256,
    string? PreviousVersion,
    string? PreviousBinarySha256,
    int Attempts,
    string FirstStartedAt,
    string StartedAt);

public sealed record AgentRevertedUpgrade(
    string Version,
    string Digest,
    string BinarySha256,
    string? PreviousVersion,
    string At);

public sealed record AgentBootOk(
    string Version,
    string? Digest,
    string? BinarySha256,
    string At);

/// <summary>Fetches the binary from an agent image addressed by digest.</summary>
public interface IAgentImageSource
{
    Task<byte[]> FetchBinaryAsync(AgentSpec spec, CancellationToken cancellationToken);
}

/// <summary>
/// Selects the narwhal layer from an OCI agent image. A raw binary layer uses
/// <c>application/vnd.narwhal.agent.binary.v1+octet-stream</c>; tar and gzip layers extract the
/// first regular file named <c>narwhal</c>.
/// </summary>
public sealed class RegistryAgentImageSource(IRegistryClient registry) : IAgentImageSource
{
    public const string BinaryMediaType = "application/vnd.narwhal.agent.binary.v1+octet-stream";
    public const string TarMediaType = "application/vnd.narwhal.agent.v1.tar";
    public const string TarGzipMediaType = "application/vnd.narwhal.agent.v1.tar+gzip";
    public const int MaximumBinaryBytes = 256 * 1024 * 1024;

    private static readonly string[] TarMediaTypes =
    [
        TarMediaType,
        TarGzipMediaType,
        "application/vnd.oci.image.layer.v1.tar",
        "application/vnd.oci.image.layer.v1.tar+gzip",
        "application/vnd.docker.image.rootfs.diff.tar",
        "application/vnd.docker.image.rootfs.diff.tar.gzip",
    ];

    public async Task<byte[]> FetchBinaryAsync(AgentSpec spec, CancellationToken cancellationToken)
    {
        var manifest = await registry.GetManifestAsync(spec.Repository, spec.Digest, cancellationToken);
        if (!string.Equals(manifest.Digest, spec.Digest, StringComparison.Ordinal))
        {
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.DigestMismatch,
                $"agent image {spec.Repository}@{spec.Digest} resolved to {manifest.Digest}");
        }

        byte[]? candidate = null;
        foreach (var layer in manifest.Layers)
        {
            if (!string.Equals(layer.MediaType, BinaryMediaType, StringComparison.Ordinal) &&
                !TarMediaTypes.Contains(layer.MediaType, StringComparer.Ordinal))
                continue;

            var blob = await registry.GetBlobAsync(spec.Repository, layer.Digest, cancellationToken);
            if (string.Equals(layer.MediaType, BinaryMediaType, StringComparison.Ordinal))
            {
                if (candidate is not null)
                    throw DuplicateBinary();
                if (blob.Length > MaximumBinaryBytes)
                    throw new CloudArtifactException(
                        CloudArtifactErrorCodes.LayerInvalid,
                        "agent binary exceeds the size limit");
                candidate = blob;
                continue;
            }

            var extracted = ExtractBinary(blob, IsGzip(layer.MediaType, blob));
            if (extracted is null)
                continue;
            if (candidate is not null)
                throw DuplicateBinary();
            candidate = extracted;
        }

        if (candidate is null)
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.LayerInvalid,
                "agent image has no narwhal binary");
        return candidate;
    }

    private static CloudArtifactException DuplicateBinary() =>
        new(
            CloudArtifactErrorCodes.LayerInvalid,
            "agent image has more than one narwhal binary");

    private static bool IsGzip(string? mediaType, ReadOnlySpan<byte> blob) =>
        mediaType is TarGzipMediaType or "application/vnd.oci.image.layer.v1.tar+gzip"
            or "application/vnd.docker.image.rootfs.diff.tar.gzip" ||
        (blob.Length >= 2 && blob[0] == 0x1f && blob[1] == 0x8b);

    private static byte[]? ExtractBinary(byte[] blob, bool gzip)
    {
        try
        {
            return ExtractBinaryCore(blob, gzip);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            throw new CloudArtifactException(
                CloudArtifactErrorCodes.LayerInvalid,
                $"agent archive is invalid: {e.Message}");
        }
    }

    private static byte[]? ExtractBinaryCore(byte[] blob, bool gzip)
    {
        using var source = new MemoryStream(blob, writable: false);
        using Stream tarStream = gzip ? new GZipStream(source, CompressionMode.Decompress) : source;
        using var reader = new TarReader(tarStream);
        byte[]? binary = null;

        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                continue;
            if (!string.Equals(Path.GetFileName(entry.Name), "narwhal", StringComparison.Ordinal))
                continue;
            if (entry.DataStream is null)
                continue;
            if (binary is not null)
                throw new CloudArtifactException(
                    CloudArtifactErrorCodes.LayerInvalid,
                    "agent layer contains more than one narwhal binary");

            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = entry.DataStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaximumBinaryBytes)
                    throw new CloudArtifactException(
                        CloudArtifactErrorCodes.LayerInvalid,
                        "agent binary exceeds the size limit");
                output.Write(buffer, 0, read);
            }

            binary = output.ToArray();
        }

        return binary;
    }
}

/// <summary>
/// Stages signed agent upgrades under <c>agent/</c> and implements the boot-ok revert guard.
/// </summary>
/// <remarks>
/// The guard runs when the daemon starts. It reverts to the previous version when a staged version
/// has not written <c>agent/boot-ok</c> after more than three starts or ten minutes.
/// </remarks>
public sealed class AgentUpgrader
{
    public const string UpgradeFailedCode = "agent_upgrade_failed";
    public const string UpgradeBlockedCode = "agent_upgrade_blocked";
    public const int MaximumStartAttempts = 3;
    public static readonly TimeSpan BootOkTimeout = TimeSpan.FromMinutes(10);

    private static readonly Regex VersionPattern = new(
        "^(?!.*\\.\\.)[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$",
        RegexOptions.CultureInvariant);

    private readonly string _agentDirectory;
    private readonly string _upgradeLockPath;
    private readonly IAgentImageSource? _source;
    private readonly Reporter _reporter;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lockTimeout;

    public AgentUpgrader(
        string stateDirectory,
        IAgentImageSource? source,
        Reporter reporter,
        TimeProvider? time = null,
        TimeSpan? lockTimeout = null)
    {
        _agentDirectory = Path.Combine(Path.GetFullPath(stateDirectory), "agent");
        _upgradeLockPath = Path.Combine(Path.GetFullPath(stateDirectory), "agent-upgrade.lock");
        _source = source;
        _reporter = reporter;
        _time = time ?? TimeProvider.System;
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);
    }

    public string CurrentLink => Path.Combine(_agentDirectory, "current");

    internal string? InstalledVersion => CurrentVersion();

    internal string PendingPath => Path.Combine(_agentDirectory, "pending");

    internal string RevertedPath => Path.Combine(_agentDirectory, "reverted");

    internal string BootOkPath => Path.Combine(_agentDirectory, "boot-ok");

    internal string VersionBinary(string version)
    {
        ValidateVersion(version);
        EnsureNotSymlink(Path.Combine(_agentDirectory, version));
        var path = Path.GetFullPath(Path.Combine(_agentDirectory, version, "narwhal"));
        EnsureContained(path);
        EnsureNotSymlink(path);
        return path;
    }

    /// <summary>
    /// Runs the start guard and returns the currently recorded revert, if any. It completes an
    /// interrupted swap, counts repeated starts, and reverts a staged binary that never boots.
    /// </summary>
    public async Task<AgentRevertedUpgrade?> PrepareStartAsync(CancellationToken cancellationToken)
    {
        using var held = await AcquireUpgradeLockAsync(cancellationToken);
        if (held is null)
        {
            _reporter.Warn("agent upgrade state is busy");
            return null;
        }

        return PrepareStartLocked();
    }

    private AgentRevertedUpgrade? PrepareStartLocked()
    {
        Directory.CreateDirectory(_agentDirectory);
        var pending = ReadPending();
        var reverted = ReadReverted();
        if (pending is null)
            return reverted;

        var bootOk = ReadBootOk();
        if (bootOk is not null && string.Equals(bootOk.Version, pending.Version, StringComparison.Ordinal))
        {
            DeletePending();
            return reverted;
        }

        var current = ReadCurrentVersion();
        if (!string.Equals(current, pending.Version, StringComparison.Ordinal))
        {
            if (HasStagedBinary(pending.Version))
            {
                SetCurrent(pending.Version);
            }
            else
            {
                _reporter.Warn($"pending agent {pending.Version} has no staged binary");
                return Revert(pending);
            }
        }

        var now = _time.GetUtcNow();
        var firstStarted = ParseTimestamp(pending.FirstStartedAt) ?? now;
        var updated = pending with
        {
            Attempts = pending.Attempts + 1,
            StartedAt = Timestamp(now),
        };

        if (updated.Attempts > MaximumStartAttempts || now - firstStarted >= BootOkTimeout)
        {
            WritePending(updated);
            return Revert(updated);
        }

        WritePending(updated);
        return reverted;
    }

    /// <summary>
    /// Reverts a staged version whose boot-ok never arrived within ten minutes, even when the
    /// process has not restarted.
    /// </summary>
    public async Task<AgentRevertedUpgrade?> RevertExpiredAsync(
        CancellationToken cancellationToken,
        bool nonBlocking = false)
    {
        using var held = nonBlocking
            ? await ReconcileLock.AcquireAsync(_upgradeLockPath, TimeSpan.Zero, cancellationToken)
            : await AcquireUpgradeLockAsync(cancellationToken);
        if (held is null)
            return null;

        return RevertExpiredLocked();
    }

    private AgentRevertedUpgrade? RevertExpiredLocked()
    {
        var pending = ReadPending();
        if (pending is null)
            return null;

        var bootOk = ReadBootOk();
        if (bootOk is not null && string.Equals(bootOk.Version, pending.Version, StringComparison.Ordinal))
        {
            DeletePending();
            return null;
        }

        var firstStarted = ParseTimestamp(pending.FirstStartedAt);
        var expired = firstStarted is not null && _time.GetUtcNow() - firstStarted >= BootOkTimeout;
        if (!expired && pending.Attempts <= MaximumStartAttempts)
            return null;

        return Revert(pending);
    }

    /// <summary>Compares a verified desired agent block with the installed version.</summary>
    public async Task<AgentUpgradeResult> EvaluateAsync(
        AgentSpec spec,
        ReconcileState state,
        CancellationToken cancellationToken)
    {
        var desiredDigest = AgentDigest.FromBinarySha256(spec.BinarySha256);
        if (!VersionPattern.IsMatch(spec.Version))
            return await RecordFailureAsync(
                spec,
                CurrentVersion(),
                "agent version is not a safe path component",
                cancellationToken);

        var early = CompareDesired(spec, state, desiredDigest);
        if (early is not null)
            return early;

        var currentVersion = CurrentVersion();
        var currentHash = currentVersion is null ? null : Sha256File(VersionBinary(currentVersion));

        if (string.Equals(currentHash, spec.BinarySha256, StringComparison.Ordinal))
            return new AgentUpgradeResult(AgentUpgradeAction.UpToDate);

        if (currentVersion is not null && CompareVersions(spec.Version, currentVersion) < 0)
        {
            _reporter.Warn(
                $"agent_downgrade_refused: desired {spec.Version} is lower than running {currentVersion}");
            return new AgentUpgradeResult(
                AgentUpgradeAction.DowngradeRefused,
                $"refusing to downgrade from {currentVersion} to {spec.Version}");
        }

        if (currentVersion is not null && string.Equals(spec.Version, currentVersion, StringComparison.Ordinal))
        {
            return await RecordFailureAsync(
                spec,
                currentVersion,
                $"agent {spec.Version} is already installed with a different binary",
                cancellationToken);
        }

        byte[] binary;
        if (_source is null)
            return new AgentUpgradeResult(AgentUpgradeAction.Failed, "no agent image source is configured");

        try
        {
            binary = await _source.FetchBinaryAsync(spec, cancellationToken);
        }
        catch (Exception e) when (e is RegistryException or CloudArtifactException)
        {
            if (e is CloudArtifactException)
                return await RecordFailureAsync(spec, currentVersion, e.Message, cancellationToken);

            _reporter.Warn($"cannot fetch agent image: {e.Message}");
            return new AgentUpgradeResult(AgentUpgradeAction.Failed, e.Message);
        }

        var actual = Sha256Hex(binary);
        if (!string.Equals(actual, spec.BinarySha256, StringComparison.Ordinal))
        {
            return await RecordFailureAsync(
                spec,
                currentVersion,
                $"agent binary hashed to {actual}, expected {spec.BinarySha256}",
                cancellationToken);
        }

        using var held = await AcquireUpgradeLockAsync(cancellationToken);
        if (held is null)
            return new AgentUpgradeResult(AgentUpgradeAction.Failed, "agent upgrade state is busy");

        var current = CompareDesired(spec, state, desiredDigest);
        if (current is not null)
            return current;

        var installedVersion = CurrentVersion();
        var installedHash = installedVersion is null ? null : Sha256File(VersionBinary(installedVersion));
        if (string.Equals(installedHash, spec.BinarySha256, StringComparison.Ordinal))
            return new AgentUpgradeResult(AgentUpgradeAction.UpToDate);

        if (installedVersion is not null && CompareVersions(spec.Version, installedVersion) < 0)
        {
            return new AgentUpgradeResult(
                AgentUpgradeAction.DowngradeRefused,
                $"refusing to downgrade from {installedVersion} to {spec.Version}");
        }

        if (installedVersion is not null && string.Equals(spec.Version, installedVersion, StringComparison.Ordinal))
        {
            return RecordFailureLocked(
                spec,
                installedVersion,
                $"agent {spec.Version} is already installed with a different binary");
        }

        try
        {
            InstallBinary(spec.Version, binary);
            var now = Timestamp(_time.GetUtcNow());
            WritePending(new AgentPendingUpgrade(
                spec.Version,
                spec.Digest,
                spec.BinarySha256,
                installedVersion,
                installedHash,
                0,
                now,
                now));
            SetCurrent(spec.Version);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _reporter.Error($"cannot stage agent {spec.Version}: {e.Message}");
            return new AgentUpgradeResult(AgentUpgradeAction.Failed, e.Message);
        }

        _reporter.Info($"staged agent {spec.Version} ({spec.BinarySha256})");
        return new AgentUpgradeResult(AgentUpgradeAction.Staged);
    }

    private AgentUpgradeResult? CompareDesired(
        AgentSpec spec,
        ReconcileState state,
        string desiredDigest)
    {
        var blocked = AgentDigest.TryNormalize(state.BlockedAgentDigest);
        if (string.Equals(blocked, desiredDigest, StringComparison.Ordinal))
            return new AgentUpgradeResult(
                AgentUpgradeAction.Blocked,
                $"agent binary {desiredDigest} failed previously and is blocked");

        var currentVersion = CurrentVersion();
        var currentHash = currentVersion is null ? null : Sha256File(VersionBinary(currentVersion));
        if (string.Equals(currentHash, spec.BinarySha256, StringComparison.Ordinal))
            return new AgentUpgradeResult(AgentUpgradeAction.UpToDate);
        if (currentVersion is not null && CompareVersions(spec.Version, currentVersion) < 0)
        {
            _reporter.Warn(
                $"agent_downgrade_refused: desired {spec.Version} is lower than running {currentVersion}");
            return new AgentUpgradeResult(
                AgentUpgradeAction.DowngradeRefused,
                $"refusing to downgrade from {currentVersion} to {spec.Version}");
        }

        return null;
    }

    /// <summary>Records a successful reconcile and clears a pending marker for the running version.</summary>
    public async Task<bool> MarkHealthyAsync(CancellationToken cancellationToken)
    {
        using var held = await AcquireUpgradeLockAsync(cancellationToken);
        if (held is null)
        {
            _reporter.Warn("agent upgrade state is busy; boot-ok was not recorded");
            return false;
        }

        MarkHealthyLocked();
        return true;
    }

    private void MarkHealthyLocked()
    {
        var current = ReadCurrentVersion();
        if (current is null)
            return;

        var pending = ReadPending();
        var digest = pending is not null && string.Equals(pending.Version, current, StringComparison.Ordinal)
            ? pending.Digest
            : null;
        var hash = Sha256File(VersionBinary(current));
        WriteBootOk(new AgentBootOk(current, digest, hash, Timestamp(_time.GetUtcNow())));

        if (pending is not null && string.Equals(pending.Version, current, StringComparison.Ordinal))
            DeletePending();
    }

    internal AgentRevertedUpgrade? ReadReverted() => ReadRevertedFile();

    /// <summary>
    /// Reads the durable revert record under the upgrade lock. A missing marker never clears an
    /// earlier block; callers apply the record idempotently.
    /// </summary>
    public async Task<AgentRevertedUpgrade?> ReadRevertedAsync(CancellationToken cancellationToken)
    {
        using var held = await AcquireUpgradeLockAsync(cancellationToken);
        if (held is null)
        {
            _reporter.Warn("agent upgrade state is busy; the revert record was not read");
            return null;
        }

        return ReadRevertedFile();
    }

    private async Task<AgentUpgradeResult> RecordFailureAsync(
        AgentSpec spec,
        string? currentVersion,
        string message,
        CancellationToken cancellationToken)
    {
        using var held = await AcquireUpgradeLockAsync(cancellationToken);
        if (held is null)
            return new AgentUpgradeResult(
                AgentUpgradeAction.Failed,
                "agent upgrade state is busy; the failed digest is blocked until state is writable",
                DigestBlocked: true);

        return RecordFailureLocked(spec, currentVersion, message);
    }

    private AgentUpgradeResult RecordFailureLocked(AgentSpec spec, string? currentVersion, string message)
    {
        try
        {
            WriteReverted(new AgentRevertedUpgrade(
                spec.Version,
                spec.Digest,
                spec.BinarySha256,
                currentVersion,
                Timestamp(_time.GetUtcNow())));
            DeletePending();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _reporter.Warn($"cannot record failed agent upgrade: {e.Message}");
        }

        _reporter.Warn($"agent upgrade failed: {message}");
        return new AgentUpgradeResult(AgentUpgradeAction.Failed, message, DigestBlocked: true);
    }

    private async Task<ReconcileLock?> AcquireUpgradeLockAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var held = await ReconcileLock.AcquireAsync(_upgradeLockPath, _lockTimeout, cancellationToken);
            if (held is not null)
                return held;
        }

        return null;
    }

    private AgentRevertedUpgrade Revert(AgentPendingUpgrade pending)
    {
        ValidateVersion(pending.Version);
        if (pending.PreviousVersion is { } previous)
            ValidateVersion(previous);

        var now = Timestamp(_time.GetUtcNow());
        if (pending.PreviousVersion is { } knownGood)
        {
            var previousPath = StagedBinary(knownGood);
            if (previousPath is null || IsSymlink(previousPath))
            {
                _reporter.Warn("previous agent binary is missing or a symlink; current is unchanged");
            }
            else if (pending.PreviousBinarySha256 is { } expected &&
                     !string.Equals(Sha256File(previousPath), expected, StringComparison.Ordinal))
            {
                _reporter.Warn("previous agent binary failed its SHA-256 check; current is unchanged");
            }
            else
            {
                SetCurrent(knownGood);
            }
        }
        else
        {
            _reporter.Warn("no previous agent binary is available; current points at the failed version");
        }

        var reverted = new AgentRevertedUpgrade(
            pending.Version,
            pending.Digest,
            pending.BinarySha256,
            pending.PreviousVersion,
            now);
        WriteReverted(reverted);
        DeletePending();
        _reporter.Warn($"reverted agent {pending.Version}; blocked binary {pending.BinarySha256}");
        return reverted;
    }

    private void InstallBinary(string version, byte[] binary)
    {
        ValidateVersion(version);
        var directory = Path.Combine(_agentDirectory, version);
        Directory.CreateDirectory(directory);
        var target = VersionBinary(version);
        var temporary = Path.Combine(directory, "narwhal." + Guid.NewGuid().ToString("n") + ".tmp");

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 81920,
                Options = FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(binary);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(
                    temporary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(temporary, target, overwrite: true);
            ReconcileStore.SyncDirectory(directory);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private void SetCurrent(string version)
    {
        ValidateVersion(version);
        Directory.CreateDirectory(_agentDirectory);
        var temporary = Path.Combine(_agentDirectory, ".current-" + Guid.NewGuid().ToString("n"));
        try
        {
            File.CreateSymbolicLink(temporary, version);
            AtomicReplace(temporary, CurrentLink);
            ReconcileStore.SyncDirectory(_agentDirectory);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void AtomicReplace(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Move(source, destination, overwrite: true);
            return;
        }

        if (rename(source, destination) != 0)
            throw new IOException(
                $"cannot replace {destination} (errno {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
    }

    private string? CurrentVersion()
    {
        try
        {
            var target = new FileInfo(CurrentLink).LinkTarget;
            if (target is null)
                return null;
            if (target.Contains('/', StringComparison.Ordinal) ||
                target.Contains(Path.DirectorySeparatorChar) ||
                target.Contains("..", StringComparison.Ordinal))
                return null;
            var name = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar));
            return VersionPattern.IsMatch(name) ? name : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? ReadCurrentVersion() => CurrentVersion();

    private AgentPendingUpgrade? ReadPending()
    {
        JsonDocument? document = null;
        try
        {
            document = ReadJson(PendingPath);
            if (document is null)
                return null;
            var root = document.RootElement;
            var pending = new AgentPendingUpgrade(
                root.GetProperty("version").GetString() ?? "",
                root.GetProperty("digest").GetString() ?? "",
                root.GetProperty("binary_sha256").GetString() ?? "",
                OptionalString(root, "previous_version"),
                OptionalString(root, "previous_binary_sha256"),
                root.GetProperty("attempts").GetInt32(),
                root.GetProperty("first_started_at").GetString() ?? "",
                root.GetProperty("started_at").GetString() ?? "");
            ValidateVersion(pending.Version);
            if (pending.PreviousVersion is { } previous)
                ValidateVersion(previous);
            AgentDigest.FromBinarySha256(pending.BinarySha256);
            if (pending.PreviousBinarySha256 is { } previousBinary)
                AgentDigest.FromBinarySha256(previousBinary);
            return pending;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or AgentConfigException)
        {
            Quarantine(PendingPath, e.Message);
            return null;
        }
        finally
        {
            document?.Dispose();
        }
    }

    private void WritePending(AgentPendingUpgrade pending)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("version", pending.Version);
            writer.WriteString("digest", pending.Digest);
            writer.WriteString("binary_sha256", pending.BinarySha256);
            writer.WriteString("previous_version", pending.PreviousVersion);
            writer.WriteString("previous_binary_sha256", pending.PreviousBinarySha256);
            writer.WriteNumber("attempts", pending.Attempts);
            writer.WriteString("first_started_at", pending.FirstStartedAt);
            writer.WriteString("started_at", pending.StartedAt);
            writer.WriteEndObject();
        }

        ReconcileStore.WriteAtomic(PendingPath, buffer.ToArray());
    }

    private void DeletePending()
    {
        try
        {
            File.Delete(PendingPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _reporter.Warn($"cannot remove {PendingPath}: {e.Message}");
        }
    }

    private AgentRevertedUpgrade? ReadRevertedFile()
    {
        JsonDocument? document = null;
        try
        {
            document = ReadJson(RevertedPath);
            if (document is null)
                return null;
            var root = document.RootElement;
            var reverted = new AgentRevertedUpgrade(
                root.GetProperty("version").GetString() ?? "",
                root.GetProperty("digest").GetString() ?? "",
                root.GetProperty("binary_sha256").GetString() ?? "",
                OptionalString(root, "previous_version"),
                root.GetProperty("at").GetString() ?? "");
            ValidateVersion(reverted.Version);
            if (reverted.PreviousVersion is { } previous)
                ValidateVersion(previous);
            AgentDigest.FromBinarySha256(reverted.BinarySha256);
            return reverted;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or AgentConfigException)
        {
            Quarantine(RevertedPath, e.Message);
            return null;
        }
        finally
        {
            document?.Dispose();
        }
    }

    private void WriteReverted(AgentRevertedUpgrade reverted)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("version", reverted.Version);
            writer.WriteString("digest", reverted.Digest);
            writer.WriteString("binary_sha256", reverted.BinarySha256);
            writer.WriteString("previous_version", reverted.PreviousVersion);
            writer.WriteString("at", reverted.At);
            writer.WriteEndObject();
        }

        ReconcileStore.WriteAtomic(RevertedPath, buffer.ToArray());
    }

    private AgentBootOk? ReadBootOk()
    {
        JsonDocument? document = null;
        try
        {
            document = ReadJson(BootOkPath);
            if (document is null)
                return null;
            var root = document.RootElement;
            var bootOk = new AgentBootOk(
                root.GetProperty("version").GetString() ?? "",
                OptionalString(root, "digest"),
                OptionalString(root, "binary_sha256"),
                root.GetProperty("at").GetString() ?? "");
            ValidateVersion(bootOk.Version);
            if (bootOk.BinarySha256 is { } binarySha256)
                AgentDigest.FromBinarySha256(binarySha256);
            return bootOk;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or AgentConfigException)
        {
            Quarantine(BootOkPath, e.Message);
            return null;
        }
        finally
        {
            document?.Dispose();
        }
    }

    private void WriteBootOk(AgentBootOk bootOk)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("version", bootOk.Version);
            writer.WriteString("digest", bootOk.Digest);
            writer.WriteString("binary_sha256", bootOk.BinarySha256);
            writer.WriteString("at", bootOk.At);
            writer.WriteEndObject();
        }

        ReconcileStore.WriteAtomic(BootOkPath, buffer.ToArray());
    }

    private static JsonDocument? ReadJson(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new AgentConfigException($"cannot read {path}: {e.Message}");
        }
    }

    private void Quarantine(string path, string reason)
    {
        var suffix = _time.GetUtcNow().UtcDateTime.ToString(
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture);
        var target = path + ".corrupt." + suffix + "." + Guid.NewGuid().ToString("n");
        try
        {
            File.Move(path, target);
            _reporter.Warn($"quarantined {path} as {target}: {reason}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _reporter.Warn($"cannot quarantine {path}: {e.Message}");
        }
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int CompareVersions(string left, string right)
    {
        if (TryParseSemver(left, out var leftSemver) && TryParseSemver(right, out var rightSemver))
            return CompareSemver(leftSemver, rightSemver);
        return string.CompareOrdinal(left, right);
    }

    private static bool TryParseSemver(string value, out (int Major, int Minor, int Patch, string[] Pre) parsed)
    {
        parsed = default;
        var withoutBuild = value.Split('+', 2)[0];
        var dash = withoutBuild.IndexOf('-');
        var core = dash >= 0 ? withoutBuild[..dash] : withoutBuild;
        var pre = dash >= 0 ? withoutBuild[(dash + 1)..].Split('.') : [];
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 3)
            return false;

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        parsed = (numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    private static int CompareSemver(
        (int Major, int Minor, int Patch, string[] Pre) left,
        (int Major, int Minor, int Patch, string[] Pre) right)
    {
        var core = left.Major.CompareTo(right.Major);
        if (core != 0)
            return core;
        core = left.Minor.CompareTo(right.Minor);
        if (core != 0)
            return core;
        core = left.Patch.CompareTo(right.Patch);
        if (core != 0)
            return core;

        if (left.Pre.Length == 0 || right.Pre.Length == 0)
            return right.Pre.Length.CompareTo(left.Pre.Length);

        for (var i = 0; i < Math.Min(left.Pre.Length, right.Pre.Length); i++)
        {
            var leftNumeric = int.TryParse(left.Pre[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = int.TryParse(right.Pre[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            int result;
            if (leftNumeric && rightNumeric)
                result = leftNumber.CompareTo(rightNumber);
            else if (leftNumeric)
                result = -1;
            else if (rightNumeric)
                result = 1;
            else
                result = string.CompareOrdinal(left.Pre[i], right.Pre[i]);
            if (result != 0)
                return result;
        }

        return left.Pre.Length.CompareTo(right.Pre.Length);
    }

    private static void ValidateVersion(string version)
    {
        if (!VersionPattern.IsMatch(version))
            throw new AgentConfigException(
                $"agent version '{version}' is not a safe path component");
    }

    private void EnsureContained(string path)
    {
        var root = _agentDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.Ordinal))
            throw new AgentConfigException($"agent path escapes the agent directory: {path}");
    }

    private static void EnsureNotSymlink(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null)
                throw new AgentConfigException($"agent version path is a symlink: {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AgentConfigException($"cannot inspect agent version path {path}: {e.Message}");
        }
    }

    private string? StagedBinary(string version)
    {
        try
        {
            var path = VersionBinary(version);
            return File.Exists(path) ? path : null;
        }
        catch (AgentConfigException)
        {
            return null;
        }
    }

    private bool HasStagedBinary(string version) => StagedBinary(version) is not null;

    private static bool IsSymlink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string Sha256File(string path)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var metadata = PosixFile.StatHandle(handle);
            if (!metadata.IsRegular)
                return "";
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long offset = 0;
            int read;
            while ((read = RandomAccess.Read(handle, buffer, offset)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                offset += read;
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    [System.Runtime.InteropServices.DllImport(
        "libc",
        SetLastError = true,
        CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern int rename(string oldPath, string newPath);
}
