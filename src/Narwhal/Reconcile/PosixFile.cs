using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Narwhal.Reconcile;

/// <summary>Minimal Linux file metadata for open-handle validation and root-owned bootstrap sources.</summary>
internal static class PosixFile
{
    public const uint TypeMask = 0xF000;
    public const uint TypeRegular = 0x8000;
    public const uint GroupOrOtherWrite = 0x12;

    private const int AtFdCwd = -100;
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNofollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;

    public readonly record struct Metadata(uint Mode, uint Uid, uint Gid, ulong Size)
    {
        public bool IsRegular => (Mode & TypeMask) == TypeRegular;
        public bool IsGroupOrOtherWritable => (Mode & GroupOrOtherWrite) != 0;
    }

    public static Metadata StatHandle(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("statx is Linux-only");

        var fd = (int)handle.DangerousGetHandle();
        if (statx(fd, "", AtEmptyPath, StatxBasicStats, out var stat) != 0)
            throw new IOException($"cannot stat the opened file (errno {Marshal.GetLastWin32Error()})");
        return new Metadata(stat.Mode, stat.Uid, stat.Gid, stat.Size);
    }

    public static Metadata StatPath(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("statx is Linux-only");

        if (statx(AtFdCwd, path, AtSymlinkNofollow, StatxBasicStats, out var stat) != 0)
            throw new IOException($"cannot stat {path} (errno {Marshal.GetLastWin32Error()})");
        return new Metadata(stat.Mode, stat.Uid, stat.Gid, stat.Size);
    }

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct Statx
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint Uid;
        public uint Gid;
        public ushort Mode;
        private ushort _spare;
        public ulong Inode;
        public ulong Size;
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int statx(int dirfd, string pathname, int flags, uint mask, out Statx statxbuf);
}
