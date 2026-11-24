using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Narwhal.Config;

namespace Narwhal.Podman;

/// <summary>
/// Cross-process exclusion for one pushed image reference, held for the whole
/// load/create/push/restore sequence.
/// </summary>
/// <remarks>
/// The lock is an exclusive <see cref="FileShare.None"/> handle on a file in a
/// per-UID directory. The platform enforces it with flock on Unix (share modes
/// on Windows), so the kernel drops it when the holder exits or is killed; a
/// leftover file is harmless. The path ignores XDG_RUNTIME_DIR, HOME, and
/// TMPDIR so every process started by the same user agrees on it:
/// /run/user/&lt;uid&gt; when it is private and writable, otherwise
/// &lt;passwd-home&gt;/.local/state/narwhal/push-locks. The home comes from
/// getpwuid, not the environment. If the uid has no passwd home or it is
/// unusable, a sticky-checked per-uid directory under /tmp is the last resort;
/// that path is predictable, so a local user can pre-create it and deny
/// service, and /run/user or a real home is preferred. Processes in different
/// containers or mount namespaces do not share this filesystem lock.
/// </remarks>
public sealed class PushLock : IDisposable
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x1;
    private const uint StatxMode = 0x2;
    private const uint StatxUid = 0x8;
    private const ushort TypeMask = 0xF000;
    private const ushort TypeDirectory = 0x4000;
    private const ushort TypeRegular = 0x8000;
    private const ushort UserExecuteBit = 0x040;
    private const ushort UserWriteBit = 0x080;
    private const ushort GroupWriteBit = 0x010;
    private const ushort OtherWriteBit = 0x002;
    private const ushort StickyBit = 0x200;
    private const int Enoent = 2;
    private const int Eperm = 1;
    private const int Einval = 22;
    private const int Enosys = 38;
    private const UnixFileMode PrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Lazy<string?> UserHome = new(FindHomeOrNull);

    /// <summary>Test hook: use this home instead of the passwd home.</summary>
    internal static string? HomeOverride { get; set; }

    /// <summary>Test hook: exercise the home fallback even with /run/user.</summary>
    internal static bool IgnoreRuntimeDirectoryForTests { get; set; }

    /// <summary>Test hook: exercise the lstat fallback.</summary>
    internal static bool ForceLStatForTests { get; set; }

    /// <summary>Test hook: exercise the managed-stat fallback for matching paths.</summary>
    internal static Func<string, bool>? ForceManagedStatForTests { get; set; }

    /// <summary>Test hook: replace the last-resort /tmp root.</summary>
    internal static string? TmpRootForTests { get; set; }

    /// <summary>Test hook: validate an overridden /tmp root.</summary>
    internal static bool ValidateTmpRootForTests { get; set; }

    /// <summary>Test hook: simulate a lock-file create failure.</summary>
    internal static Func<string, IOException?>? OpenLockFileFailureForTests { get; set; }

    /// <summary>The passwd home for this euid, for portability tests.</summary>
    internal static string? PasswdHomeForTests => UserHome.Value;

    private static string HomeDirectory() => HomeOverride ?? UserHome.Value
        ?? throw new PodmanException("no passwd home directory is available");

    private readonly FileStream _stream;
    private bool _disposed;

    private PushLock(FileStream stream) => _stream = stream;

    /// <summary>Lock file used for <paramref name="reference"/>.</summary>
    public static string LockPathFor(string reference)
    {
        var directory = LockDirectory();
        EnsureLockDirectory(directory);
        return Path.Combine(directory, Hashing.Sha256Hex(CanonicalReference(reference)) + ".lock");
    }

    /// <summary>
    /// Wait up to <paramref name="timeout"/> for the lock for
    /// <paramref name="reference"/>. Retries only lock contention; any other
    /// filesystem error is reported immediately.
    /// </summary>
    /// <exception cref="PodmanException">
    /// The lock could not be acquired before the timeout, or the lock path
    /// cannot be used safely.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> fired while waiting.
    /// </exception>
    public static async Task<PushLock> AcquireAsync(
        string reference, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var path = LockPathFor(reference);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new PushLock(OpenLockFile(path));
            }
            catch (IOException ex) when (IsLockConflict(ex))
            {
                if (stopwatch.Elapsed >= timeout)
                    throw new PodmanException(
                        $"could not acquire the push lock for '{reference}' within {timeout.TotalSeconds:0}s; " +
                        "another narwhal push may be running", ex);
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PodmanException($"cannot use the push lock '{path}': {ex.Message}", ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stream.Dispose();
    }

    private static FileStream OpenLockFile(string path)
    {
        PrepareLockFile(path);
        if (OpenLockFileFailureForTests?.Invoke(path) is { } injected)
        {
            // Simulate a create that left an empty file behind.
            if (!File.Exists(path))
            {
                using (File.Create(path))
                {
                }
            }
            throw injected;
        }
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.None);
        try
        {
            PrepareLockFile(path);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, PrivateFile);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Repair and validate an existing lock file before/after opening.</summary>
    private static void PrepareLockFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        var euid = GetEuid();
        if (!TryStat(path, out var info, out var errno))
        {
            if (errno == Enoent)
                return;
            throw new PodmanException($"cannot inspect push lock '{path}' (errno {errno})");
        }
        if ((info.Mode & TypeMask) != TypeRegular)
            throw new PodmanException($"push lock '{path}' is not a regular file (symlink or special file)");
        if (info.UidKnown && info.Uid != euid)
            throw new PodmanException($"push lock '{path}' is owned by uid {info.Uid}, expected {euid}");
        File.SetUnixFileMode(path, PrivateFile);
    }

    private static string LockDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Path.GetTempPath(), "narwhal-push-locks");

        var euid = GetEuid();
        var runtimeRoot = $"/run/user/{euid}";
        if (!IgnoreRuntimeDirectoryForTests)
        {
            if (TryStat(runtimeRoot, out var info, out var errno))
            {
                if (IsUsableRuntimeRoot(info, euid))
                    return Path.Combine(runtimeRoot, "narwhal", "push-locks");
                // Read-only or shared-writable runtime dir: use the home fallback.
            }
            else if (errno != Enoent)
            {
                throw new PodmanException($"cannot inspect '{runtimeRoot}' (errno {errno})");
            }
        }

        if (TryHomeLockDirectory(euid, out var homeDirectory))
            return homeDirectory;

        return TmpLockDirectory(euid);
    }

    private static bool TryHomeLockDirectory(uint euid, out string directory)
    {
        directory = "";
        var home = HomeOverride ?? UserHome.Value;
        if (home is null)
            return false;
        if (!TryStat(home, out var info, out _))
            return false;
        if ((info.Mode & TypeMask) != TypeDirectory
            || (info.UidKnown && info.Uid != euid)
            || (info.Mode & (GroupWriteBit | OtherWriteBit)) != 0
            || (info.Mode & (UserWriteBit | UserExecuteBit)) != (UserWriteBit | UserExecuteBit))
        {
            return false;
        }
        directory = Path.Combine(home, ".local", "state", "narwhal", "push-locks");
        return true;
    }

    /// <summary>
    /// Last resort for uids without a passwd entry or usable home. The path is
    /// predictable, so another local user can pre-create it and cause a denial
    /// of service; /run/user and the passwd home are tried first.
    /// </summary>
    private static string TmpLockDirectory(uint euid)
    {
        var root = TmpRootForTests ?? "/tmp";
        try
        {
            var resolved = new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is not null)
                root = resolved.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Validation below reports an unusable root.
        }

        if (TmpRootForTests is null || ValidateTmpRootForTests)
        {
            if (!TryStat(root, out var info, out var errno))
                throw new PodmanException($"cannot inspect {root} for the fallback push lock (errno {errno})");
            if ((info.Mode & TypeMask) != TypeDirectory
                || (info.UidKnown && info.Uid != 0)
                || (info.Mode & OtherWriteBit) == 0
                || (info.Mode & StickyBit) == 0)
            {
                throw new PodmanException(
                    $"refusing to place push locks in {root}: it must be root-owned, world-writable, and sticky; " +
                    $"fix it, provide a passwd home for uid {euid}, or provide /run/user/{euid}");
            }
        }
        return Path.Combine(root, $"narwhal-push-locks-{euid}", "push-locks");
    }

    private static bool IsUsableRuntimeRoot(StatInfo info, uint euid) =>
        (info.Mode & TypeMask) == TypeDirectory
        && (!info.UidKnown || info.Uid == euid)
        && (info.Mode & (GroupWriteBit | OtherWriteBit)) == 0
        && (info.Mode & (UserWriteBit | UserExecuteBit)) == (UserWriteBit | UserExecuteBit);

    /// <summary>Home from passwd, resolved to its final target, never HOME.</summary>
    private static string? FindHomeOrNull()
    {
        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var euid = GetEuid();
        var pointer = GetPwUid(euid);
        if (pointer == IntPtr.Zero)
            return null;

        var dir = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()
            ? Marshal.PtrToStructure<PasswdBsd>(pointer).Dir
            : Marshal.PtrToStructure<PasswdLinux>(pointer).Dir;
        var home = Marshal.PtrToStringUTF8(dir);
        if (string.IsNullOrEmpty(home) || !Path.IsPathRooted(home))
            return null;

        try
        {
            var resolved = new DirectoryInfo(home).ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is not null)
                home = resolved.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave the passwd value; directory selection validates it.
        }
        return home;
    }

    private static void EnsureLockDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        var euid = GetEuid();
        foreach (var (path, tighten) in DirectoryComponents(directory, euid))
            EnsurePrivateComponent(path, euid, tighten);
    }

    /// <summary>
    /// /run/user/&lt;uid&gt; is validated but left alone because the session
    /// manager owns it. The home fallback repairs and tightens .local, state,
    /// and the narwhal-owned directories; the home itself is validated only.
    /// </summary>
    private static IReadOnlyList<(string Path, bool Tighten)> DirectoryComponents(string directory, uint euid)
    {
        var runtimeRoot = $"/run/user/{euid}";
        if (directory.StartsWith(runtimeRoot + "/", StringComparison.Ordinal))
            return [(runtimeRoot, false), (Path.Combine(runtimeRoot, "narwhal"), true), (directory, true)];

        var tmpMarker = $"narwhal-push-locks-{euid}";
        if (Path.GetFileName(Path.GetDirectoryName(directory)) == tmpMarker)
            return [(Path.GetDirectoryName(directory)!, true), (directory, true)];
        var home = HomeDirectory();
        var local = Path.Combine(home, ".local");
        var state = Path.Combine(local, "state");
        return
        [
            (home, false),
            (local, true),
            (state, true),
            (Path.Combine(state, "narwhal"), true),
            (directory, true),
        ];
    }

    private static void EnsurePrivateComponent(string path, uint euid, bool tighten)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unix lock directories are not used on Windows");

        if (!TryStat(path, out _, out var errno))
        {
            if (errno != Enoent)
                throw new PodmanException($"cannot inspect lock directory '{path}' (errno {errno})");
            if (string.Equals(path, HomeOverride ?? UserHome.Value, StringComparison.Ordinal))
            {
                throw new PodmanException(
                    $"home directory '{path}' for uid {euid} does not exist; " +
                    $"create it, fix passwd, or provide a usable /run/user/{euid}");
            }
            try
            {
                Directory.CreateDirectory(path, PrivateDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PodmanException($"cannot create lock directory '{path}': {ex.Message}", ex);
            }
        }

        ValidateDirectory(path, euid);
        if (!tighten)
        {
            if (!TryStat(path, out var info, out errno))
                throw new PodmanException($"cannot inspect lock directory '{path}' (errno {errno})");
            if ((info.Mode & (UserWriteBit | UserExecuteBit)) != (UserWriteBit | UserExecuteBit))
            {
                throw new PodmanException(
                    $"lock path component '{path}' is not writable and searchable by its owner; " +
                    "tighten its mode or provide a usable /run/user/<uid>");
            }
            if ((info.Mode & (GroupWriteBit | OtherWriteBit)) != 0)
            {
                throw new PodmanException(
                    $"lock path component '{path}' is group- or world-writable; " +
                    "tighten its mode or provide a usable /run/user/<uid>");
            }
            return;
        }

        // A restrictive umask can create the directory with mode 0000; the
        // owner can still chmod it, and children need the repaired mode.
        try
        {
            File.SetUnixFileMode(path, PrivateDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PodmanException($"cannot restrict lock directory '{path}': {ex.Message}", ex);
        }
        // Re-check after create/chmod so a swapped path cannot slip through.
        ValidateDirectory(path, euid);
    }

    private static void ValidateDirectory(string path, uint euid)
    {
        if (!TryStat(path, out var info, out var errno))
            throw new PodmanException($"cannot inspect lock directory '{path}' (errno {errno})");
        if ((info.Mode & TypeMask) != TypeDirectory)
            throw new PodmanException($"lock path '{path}' is not a directory (symlink or special file)");
        if (info.UidKnown && info.Uid != euid)
        {
            throw new PodmanException(
                $"lock directory '{path}' is owned by uid {info.Uid}, expected {euid}; " +
                $"another user may have pre-created it. Remove it as that user or provide a usable /run/user/{euid}");
        }
    }

    /// <summary>
    /// Docker Hub aliases and short names that name the same remote tag share
    /// one lock. The tag itself stays case-sensitive; registry and repository
    /// do not.
    /// </summary>
    private static string CanonicalReference(string reference)
    {
        var value = reference.Trim();
        foreach (var prefix in new[] { "registry-1.docker.io/", "index.docker.io/", "docker.io/" })
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                value = value[prefix.Length..];
                break;
            }
        }

        var digestAt = value.IndexOf('@');
        var name = digestAt >= 0 ? value[..digestAt] : value;
        var digest = digestAt >= 0 ? value[digestAt..].ToLowerInvariant() : "";
        if (!name.Contains('/'))
            name = "library/" + name;

        var lastSlash = name.LastIndexOf('/');
        var lastColon = name.LastIndexOf(':');
        string repository;
        string tag;
        if (lastColon > lastSlash)
        {
            repository = name[..lastColon];
            tag = name[lastColon..];
        }
        else
        {
            repository = name;
            tag = ":latest";
        }
        return repository.ToLowerInvariant() + tag + digest;
    }

    private static bool IsLockConflict(IOException error) =>
        error.HResult is unchecked((int)0x80070020) or 11 or 35
        || (error.HResult & 0xFFFF) is 11 or 32 or 35;

    private static bool TryStat(string path, out StatInfo info, out int errno)
    {
        if (ForceManagedStatForTests?.Invoke(path) == true)
            return TryStatManaged(path, out info, out errno);

        if (!ForceLStatForTests && OperatingSystem.IsLinux())
        {
            try
            {
                var bytes = Utf8.GetBytes(path + "\0");
                var result = Statx(AtFdCwd, bytes, AtSymlinkNoFollow,
                    StatxType | StatxMode | StatxUid, out var raw);
                if (result == 0)
                {
                    // Only trust the fields the kernel says it filled in.
                    if ((raw.Mask & (StatxType | StatxMode)) == (StatxType | StatxMode))
                    {
                        info = new StatInfo(raw.Mode, raw.Uid, UidKnown: (raw.Mask & StatxUid) != 0);
                        errno = 0;
                        return true;
                    }
                }
                else
                {
                    errno = Marshal.GetLastPInvokeError();
                    // ENOSYS is an old kernel/libc; EINVAL/EPERM appear on
                    // filesystems that cannot answer statx. lstat is the fallback.
                    if (errno is not (Enosys or Eperm or Einval))
                    {
                        info = default;
                        return false;
                    }
                }
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                // Older libc: use lstat below.
            }
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            if (TryLStat(path, out info, out errno))
                return true;
            if (errno == Enosys)
                return TryStatManaged(path, out info, out errno);
            return false;
        }

        return TryStatManaged(path, out info, out errno);
    }

    /// <summary>
    /// lstat gives the file type without following links, so a FIFO, socket, or
    /// symlink cannot pose as the lock file. Offsets are the platform's struct
    /// stat; unsupported layouts return ENOSYS for the managed fallback.
    /// </summary>
    private static bool TryLStat(string path, out StatInfo info, out int errno)
    {
        var buffer = new byte[512];
        var bytes = Utf8.GetBytes(path + "\0");
        try
        {
            if (LStat(bytes, buffer) != 0)
            {
                errno = Marshal.GetLastPInvokeError();
                info = default;
                return false;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            errno = Enosys;
            info = default;
            return false;
        }

        int modeOffset;
        int uidOffset;
        if (!TryGetLStatOffsets(OperatingSystem.IsMacOS(), OperatingSystem.IsFreeBSD(),
                RuntimeInformation.ProcessArchitecture, out modeOffset, out uidOffset))
        {
            errno = Enosys;
            info = default;
            return false;
        }

        info = new StatInfo(MemoryMarshal.Read<ushort>(buffer.AsSpan(modeOffset)),
            MemoryMarshal.Read<uint>(buffer.AsSpan(uidOffset)), UidKnown: true);
        errno = 0;
        return true;
    }

    internal static bool TryGetLStatOffsets(
        bool macOS, bool freeBsd, Architecture architecture, out int modeOffset, out int uidOffset)
    {
        modeOffset = 0;
        uidOffset = 0;
        if (macOS)
        {
            modeOffset = 4;
            uidOffset = 16;
            return true;
        }
        if (freeBsd)
        {
            // FreeBSD 13 keeps dev, ino, and nlink 64-bit on 32-bit too.
            if (architecture is Architecture.X64 or Architecture.Arm64
                    or Architecture.X86 or Architecture.Arm)
            {
                modeOffset = 24;
                uidOffset = 28;
                return true;
            }
            return false;
        }
        if (architecture == Architecture.X64)
        {
            modeOffset = 24;
            uidOffset = 28;
            return true;
        }
        if (architecture is Architecture.Arm64 or Architecture.X86 or Architecture.Arm)
        {
            modeOffset = 16;
            uidOffset = 24;
            return true;
        }
        return false;
    }

    /// <summary>
    /// There is no portable managed call that reports the Unix file type, so
    /// this fails closed instead of treating a FIFO, socket, or device as a
    /// regular file.
    /// </summary>
    private static bool TryStatManaged(string path, out StatInfo info, out int errno)
    {
        _ = path;
        info = default;
        errno = Enosys;
        return false;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    [DllImport("libc", EntryPoint = "getpwuid")]
    private static extern IntPtr GetPwUid(uint uid);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int dirfd, byte[] path, int flags, uint mask, out StatxBuffer buffer);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat(byte[] path, byte[] buffer);

    private readonly record struct StatInfo(ushort Mode, uint Uid, bool UidKnown);

    [StructLayout(LayoutKind.Sequential)]
    private struct PasswdLinux
    {
        public IntPtr Name;
        public IntPtr Password;
        public uint Uid;
        public uint Gid;
        public IntPtr Gecos;
        public IntPtr Dir;
        public IntPtr Shell;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PasswdBsd
    {
        public IntPtr Name;
        public IntPtr Password;
        public uint Uid;
        public uint Gid;
        public long Change;
        public IntPtr Class;
        public IntPtr Gecos;
        public IntPtr Dir;
        public IntPtr Shell;
        public long Expire;
    }

    // Kernel struct statx prefix: uid is at offset 20 and mode at offset 28 on
    // every Linux ABI; Size keeps the kernel's full 256-byte write in bounds.
    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct StatxBuffer
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint Uid;
        public uint Gid;
        public ushort Mode;
        public ushort Spare;
    }
}
