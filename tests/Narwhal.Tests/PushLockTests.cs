using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Narwhal.Podman;
using Xunit;

namespace Narwhal.Tests;

[Collection(MultiPlatformIntegrationTests.ProcessCollection.Name)]
public class PushLockTests
{
    [Fact]
    public async Task SecondAcquireTimesOutWhileTheFirstIsHeldAndSucceedsAfterRelease()
    {
        var reference = UniqueReference();
        using var first = await PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(5), default);

        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<PodmanException>(
            () => PushLock.AcquireAsync(reference, TimeSpan.FromMilliseconds(400), default));
        stopwatch.Stop();

        Assert.Contains("could not acquire the push lock", error.Message);
        Assert.Contains(reference, error.Message);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(300),
            $"gave up after {stopwatch.ElapsedMilliseconds}ms, before the timeout");

        first.Dispose();
        using var second = await PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(5), default);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task LockIsReleasedWhenTheScopeThrows()
    {
        var reference = UniqueReference();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var held = await PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(5), default);
            throw new InvalidOperationException("boom");
        });

        using var reacquired = await PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(5), default);
        Assert.NotNull(reacquired);
    }

    [Fact]
    public async Task WaitingAcquireHonorsCancellation()
    {
        var reference = UniqueReference();
        using var first = await PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(5), default);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PushLock.AcquireAsync(reference, TimeSpan.FromMinutes(1), cancellation.Token));
    }

    [Fact]
    public async Task DifferentReferencesDoNotBlockEachOther()
    {
        using var first = await PushLock.AcquireAsync(UniqueReference(), TimeSpan.FromSeconds(5), default);
        using var second = await PushLock.AcquireAsync(UniqueReference(), TimeSpan.FromSeconds(1), default);
        Assert.NotNull(second);
    }

    [Fact]
    public void LockPathIsPerUidAndIgnoresEnvironmentAndAliases()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = PushLock.LockPathFor("docker.io/library/alpine:latest");
        Assert.True(path.StartsWith("/run/user/", StringComparison.Ordinal)
            || path.StartsWith("/tmp/narwhal-push-locks-", StringComparison.Ordinal), path);
        Assert.Equal(path, PushLock.LockPathFor("library/alpine"));
        Assert.Equal(path, PushLock.LockPathFor("alpine"));
        Assert.Equal(path, PushLock.LockPathFor("docker.io/alpine"));
        Assert.Equal(path, PushLock.LockPathFor("index.docker.io/library/alpine"));
        Assert.Equal(path, PushLock.LockPathFor("registry-1.docker.io/library/alpine"));
        Assert.NotEqual(path, PushLock.LockPathFor("library/alpine:edge"));
    }

    [Fact]
    public void LockPathIsIndependentOfXdgAndTmpdir()
    {
        if (OperatingSystem.IsWindows())
            return;

        var previousXdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var previousTmp = Environment.GetEnvironmentVariable("TMPDIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", "/run/user/1");
            Environment.SetEnvironmentVariable("TMPDIR", "/tmp/one");
            var first = PushLock.LockPathFor(UniqueReference());

            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", "/run/user/2");
            Environment.SetEnvironmentVariable("TMPDIR", "/tmp/two");
            var second = PushLock.LockPathFor(UniqueReference());

            Assert.Equal(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousXdg);
            Environment.SetEnvironmentVariable("TMPDIR", previousTmp);
        }
    }

    [Fact]
    public async Task AcquireRepairsModesOnAnExistingLockFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var reference = UniqueReference();
        var path = PushLock.LockPathFor(reference);
        using (await PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(5), default))
        {
        }

        File.SetUnixFileMode(path, UnixFileMode.None);
        using (await PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(5), default))
        {
        }
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void LockDirectoryModesAreRepaired()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = PushLock.LockPathFor(UniqueReference());
        var directory = Path.GetDirectoryName(path)!;
        var parent = Path.GetDirectoryName(directory)!;
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.None);
            File.SetUnixFileMode(parent, UnixFileMode.None);

            _ = PushLock.LockPathFor(UniqueReference());

            const UnixFileMode expected =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(expected, File.GetUnixFileMode(directory));
            Assert.Equal(expected, File.GetUnixFileMode(parent));
        }
        finally
        {
            try
            {
                File.SetUnixFileMode(directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(parent,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception)
            {
                // Best-effort restoration; the next lock use repairs the modes.
            }
        }
    }

    [Fact]
    public void PasswdHomeIgnoresHomeEnvironment()
    {
        if (OperatingSystem.IsWindows())
            return;

        var previous = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("HOME", "/tmp/not-the-passwd-home");
            var home = PushLock.PasswdHomeForTests;
            Assert.False(string.IsNullOrEmpty(home));
            Assert.NotEqual("/tmp/not-the-passwd-home", home);
            Assert.True(Path.IsPathRooted(home));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previous);
        }
    }

    [Fact]
    public async Task RestrictiveUmaskCreatesRepairedPrivateDirectories()
    {
        if (OperatingSystem.IsWindows())
            return;

        var home = Path.Combine(Path.GetTempPath(), $"narwhal-umask-{Guid.NewGuid():n}");
        Directory.CreateDirectory(home, PrivateDirectoryMode);
        var previousHome = PushLock.HomeOverride;
        var previousIgnore = PushLock.IgnoreRuntimeDirectoryForTests;
        var previousMask = Umask(0x1FF);
        PushLock.HomeOverride = home;
        PushLock.IgnoreRuntimeDirectoryForTests = true;
        try
        {
            var reference = UniqueReference();
            var path = PushLock.LockPathFor(reference);
            using (await PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(5), default))
            {
            }

            foreach (var component in new[]
                     {
                         Path.Combine(home, ".local"),
                         Path.Combine(home, ".local", "state"),
                         Path.Combine(home, ".local", "state", "narwhal"),
                         Path.GetDirectoryName(path)!,
                     })
            {
                Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(component));
            }
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            Umask(previousMask);
            PushLock.HomeOverride = previousHome;
            PushLock.IgnoreRuntimeDirectoryForTests = previousIgnore;
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task HomeFallbackRepairsExistingZeroModeLocalAndState()
    {
        if (OperatingSystem.IsWindows())
            return;

        var home = Path.Combine(Path.GetTempPath(), $"narwhal-zero-{Guid.NewGuid():n}");
        var local = Path.Combine(home, ".local");
        var state = Path.Combine(local, "state");
        Directory.CreateDirectory(home, PrivateDirectoryMode);
        Directory.CreateDirectory(state, PrivateDirectoryMode);
        File.SetUnixFileMode(state, UnixFileMode.None);
        File.SetUnixFileMode(local, UnixFileMode.None);
        var previousHome = PushLock.HomeOverride;
        var previousIgnore = PushLock.IgnoreRuntimeDirectoryForTests;
        PushLock.HomeOverride = home;
        PushLock.IgnoreRuntimeDirectoryForTests = true;
        try
        {
            var reference = UniqueReference();
            using (await PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(5), default))
            {
            }
            Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(local));
            Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(state));
        }
        finally
        {
            PushLock.HomeOverride = previousHome;
            PushLock.IgnoreRuntimeDirectoryForTests = previousIgnore;
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task HomeFallbackCreatesPrivateLockPath()
    {
        if (OperatingSystem.IsWindows())
            return;

        var home = Path.Combine(Path.GetTempPath(), $"narwhal-home-{Guid.NewGuid():n}");
        Directory.CreateDirectory(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var previousHome = PushLock.HomeOverride;
        var previousIgnore = PushLock.IgnoreRuntimeDirectoryForTests;
        PushLock.HomeOverride = home;
        PushLock.IgnoreRuntimeDirectoryForTests = true;
        try
        {
            var reference = UniqueReference();
            var path = PushLock.LockPathFor(reference);
            var directory = Path.Combine(home, ".local", "state", "narwhal", "push-locks");
            Assert.Equal(directory, Path.GetDirectoryName(path));

            using (await PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(5), default))
            {
            }

            const UnixFileMode privateDirectory =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(privateDirectory, File.GetUnixFileMode(directory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            PushLock.HomeOverride = previousHome;
            PushLock.IgnoreRuntimeDirectoryForTests = previousIgnore;
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void LockPathUsesPasswdHomeWhenHomeIsOverridden()
    {
        if (OperatingSystem.IsWindows())
            return;

        var home = PushLock.PasswdHomeForTests;
        Assert.False(string.IsNullOrEmpty(home));
        var previousHomeEnv = Environment.GetEnvironmentVariable("HOME");
        var previousHome = PushLock.HomeOverride;
        var previousIgnore = PushLock.IgnoreRuntimeDirectoryForTests;
        try
        {
            Environment.SetEnvironmentVariable("HOME", "/tmp/not-the-passwd-home");
            PushLock.HomeOverride = null;
            PushLock.IgnoreRuntimeDirectoryForTests = true;
            var path = PushLock.LockPathFor(UniqueReference());
            Assert.StartsWith(
                Path.Combine(home!, ".local", "state", "narwhal", "push-locks") + Path.DirectorySeparatorChar,
                path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHomeEnv);
            PushLock.HomeOverride = previousHome;
            PushLock.IgnoreRuntimeDirectoryForTests = previousIgnore;
        }
    }

    [Fact]
    public async Task MissingHomeFallsBackToTmp()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tmpRoot = Path.Combine(Path.GetTempPath(), $"narwhal-tmp-{Guid.NewGuid():n}");
        Directory.CreateDirectory(tmpRoot, PrivateDirectoryMode);
        var previousHome = PushLock.HomeOverride;
        var previousIgnore = PushLock.IgnoreRuntimeDirectoryForTests;
        var previousTmp = PushLock.TmpRootForTests;
        PushLock.HomeOverride = Path.Combine(tmpRoot, "missing-home");
        PushLock.IgnoreRuntimeDirectoryForTests = true;
        PushLock.TmpRootForTests = tmpRoot;
        try
        {
            var reference = UniqueReference();
            var path = PushLock.LockPathFor(reference);
            Assert.StartsWith(Path.Combine(tmpRoot, "narwhal-push-locks-"), path);
            using (await PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(5), default))
            {
            }
            Assert.True(File.Exists(path));
        }
        finally
        {
            PushLock.HomeOverride = previousHome;
            PushLock.IgnoreRuntimeDirectoryForTests = previousIgnore;
            PushLock.TmpRootForTests = previousTmp;
            Directory.Delete(tmpRoot, recursive: true);
        }
    }

    [Fact]
    public void ReadOnlyHomeFallsBackToTmp()
    {
        if (OperatingSystem.IsWindows())
            return;

        var home = Path.Combine(Path.GetTempPath(), $"narwhal-readonly-{Guid.NewGuid():n}");
        Directory.CreateDirectory(home, PrivateDirectoryMode);
        var tmpRoot = Path.Combine(Path.GetTempPath(), $"narwhal-tmp-{Guid.NewGuid():n}");
        Directory.CreateDirectory(tmpRoot, PrivateDirectoryMode);
        File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var previousHome = PushLock.HomeOverride;
        var previousIgnore = PushLock.IgnoreRuntimeDirectoryForTests;
        var previousTmp = PushLock.TmpRootForTests;
        PushLock.HomeOverride = home;
        PushLock.IgnoreRuntimeDirectoryForTests = true;
        PushLock.TmpRootForTests = tmpRoot;
        try
        {
            var path = PushLock.LockPathFor(UniqueReference());
            Assert.StartsWith(Path.Combine(tmpRoot, "narwhal-push-locks-"), path);
        }
        finally
        {
            PushLock.HomeOverride = previousHome;
            PushLock.IgnoreRuntimeDirectoryForTests = previousIgnore;
            PushLock.TmpRootForTests = previousTmp;
            File.SetUnixFileMode(home, PrivateDirectoryMode);
            Directory.Delete(tmpRoot, recursive: true);
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void TmpSymlinkIsResolvedBeforeValidation()
    {
        if (OperatingSystem.IsWindows())
            return;

        var real = Path.Combine(Path.GetTempPath(), $"narwhal-tmp-real-{Guid.NewGuid():n}");
        Directory.CreateDirectory(real, PrivateDirectoryMode);
        var link = Path.Combine(Path.GetTempPath(), $"narwhal-tmp-link-{Guid.NewGuid():n}");
        Directory.CreateSymbolicLink(link, real);
        var previousHome = PushLock.HomeOverride;
        var previousIgnore = PushLock.IgnoreRuntimeDirectoryForTests;
        var previousTmp = PushLock.TmpRootForTests;
        PushLock.HomeOverride = Path.Combine(real, "missing-home");
        PushLock.IgnoreRuntimeDirectoryForTests = true;
        PushLock.TmpRootForTests = link;
        try
        {
            var path = PushLock.LockPathFor(UniqueReference());
            Assert.StartsWith(Path.Combine(real, "narwhal-push-locks-"), path);
        }
        finally
        {
            PushLock.HomeOverride = previousHome;
            PushLock.IgnoreRuntimeDirectoryForTests = previousIgnore;
            PushLock.TmpRootForTests = previousTmp;
            Directory.Delete(link);
            Directory.Delete(real, recursive: true);
        }
    }

    [Fact]
    public void FreeBsd32LStatOffsetsMatchFreeBsd13Layout()
    {
        Assert.True(PushLock.TryGetLStatOffsets(false, true, Architecture.X86, out var mode, out var uid));
        Assert.Equal(24, mode);
        Assert.Equal(28, uid);
        Assert.True(PushLock.TryGetLStatOffsets(false, true, Architecture.Arm, out mode, out uid));
        Assert.Equal(24, mode);
        Assert.Equal(28, uid);
        Assert.True(PushLock.TryGetLStatOffsets(false, false, Architecture.X64, out mode, out uid));
        Assert.Equal(24, mode);
        Assert.Equal(28, uid);
        Assert.False(PushLock.TryGetLStatOffsets(false, false, Architecture.S390x, out _, out _));
    }

    [Fact]
    public void HomeWithFileLocalFailsClosed()
    {
        if (OperatingSystem.IsWindows())
            return;

        var home = Path.Combine(Path.GetTempPath(), $"narwhal-file-local-{Guid.NewGuid():n}");
        Directory.CreateDirectory(home, PrivateDirectoryMode);
        File.WriteAllText(Path.Combine(home, ".local"), "not a directory");
        var previousHome = PushLock.HomeOverride;
        var previousIgnore = PushLock.IgnoreRuntimeDirectoryForTests;
        PushLock.HomeOverride = home;
        PushLock.IgnoreRuntimeDirectoryForTests = true;
        try
        {
            var path = Path.Combine(home, ".local", "state", "narwhal", "push-locks");
            var error = Assert.Throws<PodmanException>(() => PushLock.LockPathFor(UniqueReference()));
            Assert.Contains("is not a directory", error.Message);
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            PushLock.HomeOverride = previousHome;
            PushLock.IgnoreRuntimeDirectoryForTests = previousIgnore;
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void TmpRootOutsideTrustedParentIsRejected()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tmpRoot = Path.Combine(Path.GetTempPath(), $"narwhal-insecure-tmp-{Guid.NewGuid():n}");
        Directory.CreateDirectory(tmpRoot, PrivateDirectoryMode);
        File.SetUnixFileMode(tmpRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        var previousHome = PushLock.HomeOverride;
        var previousIgnore = PushLock.IgnoreRuntimeDirectoryForTests;
        var previousTmp = PushLock.TmpRootForTests;
        var previousValidate = PushLock.ValidateTmpRootForTests;
        PushLock.HomeOverride = Path.Combine(tmpRoot, "missing-home");
        PushLock.IgnoreRuntimeDirectoryForTests = true;
        PushLock.TmpRootForTests = tmpRoot;
        PushLock.ValidateTmpRootForTests = true;
        try
        {
            Assert.Throws<PodmanException>(() => PushLock.LockPathFor(UniqueReference()));
        }
        finally
        {
            PushLock.HomeOverride = previousHome;
            PushLock.IgnoreRuntimeDirectoryForTests = previousIgnore;
            PushLock.TmpRootForTests = previousTmp;
            PushLock.ValidateTmpRootForTests = previousValidate;
            File.SetUnixFileMode(tmpRoot, PrivateDirectoryMode);
            Directory.Delete(tmpRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(28)]
    [InlineData(69)]
    [InlineData(122)]
    public async Task NoSpaceFailsClosedWithoutPathSwitch(int errno)
    {
        if (OperatingSystem.IsWindows())
            return;

        var reference = UniqueReference();
        var path = PushLock.LockPathFor(reference);
        var previous = PushLock.OpenLockFileFailureForTests;
        PushLock.OpenLockFileFailureForTests = candidate =>
            candidate == path ? new IOException("injected no space", errno) : null;
        try
        {
            var error = await Assert.ThrowsAsync<PodmanException>(
                () => PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(1), default));
            Assert.Contains("cannot use the push lock", error.Message);
            Assert.True(File.Exists(path), "the zero-length partial file must be left in place");
            Assert.Equal(path, PushLock.LockPathFor(reference));
        }
        finally
        {
            PushLock.OpenLockFileFailureForTests = previous;
            File.Delete(path);
        }
    }

    [UnixFlockFact]
    public async Task EnospcFailureDoesNotUnlinkAHeldPartialFile()
    {
        var reference = UniqueReference();
        var path = PushLock.LockPathFor(reference);
        Assert.False(File.Exists(path));
        var previous = PushLock.OpenLockFileFailureForTests;
        var pidFile = Path.Combine(Path.GetTempPath(), $"narwhal-lock-child-{Guid.NewGuid():n}.pid");
        Process? child = null;
        var holderPid = 0;
        PushLock.OpenLockFileFailureForTests = candidate =>
        {
            if (candidate != path)
                return null;
            Assert.False(File.Exists(path), "the test starts with no partial file");
            // The hook owns the partial file's creation: create the zero-length
            // inode first, let the holder flock it, and only then fail the create.
            using (File.Create(path))
            {
            }
            Assert.Equal(0, new FileInfo(path).Length);

            var start = new ProcessStartInfo("setsid")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("bash");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("echo $$ > \"$1\"; exec flock -x \"$2\" sleep 120");
            start.ArgumentList.Add("_");
            start.ArgumentList.Add(pidFile);
            start.ArgumentList.Add(path);
            child = Process.Start(start)
                ?? throw new InvalidOperationException("could not start the lock holder");

            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (File.Exists(pidFile)
                    && int.TryParse(File.ReadAllText(pidFile).Trim(), out holderPid)
                    && LockHeld(path))
                    break;
                Thread.Sleep(50);
            }
            Assert.True(holderPid > 0 && LockHeld(path), "child process did not take the flock");
            return new InjectedNoSpaceException();
        };
        try
        {
            await Assert.ThrowsAsync<PodmanException>(
                () => PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(1), default));
            Assert.True(File.Exists(path), "the failure unlinked the holder's file");
            Assert.True(LockHeld(path), "the holder lost the lock after the failure");

            PushLock.OpenLockFileFailureForTests = previous;
            var error = await Assert.ThrowsAsync<PodmanException>(
                () => PushLock.AcquireAsync(reference, TimeSpan.FromMilliseconds(400), default));
            Assert.Contains("could not acquire the push lock", error.Message);
        }
        finally
        {
            PushLock.OpenLockFileFailureForTests = previous;
            if (holderPid > 0)
            {
                using var kill = Process.Start(new ProcessStartInfo("kill")
                {
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    ArgumentList = { "-KILL", "--", $"-{holderPid}" },
                });
                kill?.WaitForExit(5000);
            }
            else
            {
                try
                {
                    child?.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // The child may already have exited.
                }
            }
            child?.WaitForExit(5000);
            child?.Dispose();
            File.Delete(pidFile);
            File.Delete(path);
        }
    }

    [UnixFlockFact]
    public async Task NoSpaceCannotBypassACrossProcessHolderOnTheHomePath()
    {
        if (OperatingSystem.IsWindows())
            return;

        var home = Path.Combine(Path.GetTempPath(), $"narwhal-held-home-{Guid.NewGuid():n}");
        Directory.CreateDirectory(home, PrivateDirectoryMode);
        var previousHome = PushLock.HomeOverride;
        var previousIgnore = PushLock.IgnoreRuntimeDirectoryForTests;
        PushLock.HomeOverride = home;
        PushLock.IgnoreRuntimeDirectoryForTests = true;
        var reference = UniqueReference();
        var path = PushLock.LockPathFor(reference);
        Assert.StartsWith(home + Path.DirectorySeparatorChar, path);
        var pidFile = Path.Combine(Path.GetTempPath(), $"narwhal-lock-child-{Guid.NewGuid():n}.pid");
        var start = new ProcessStartInfo("setsid")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("bash");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("echo $$ > \"$1\"; exec flock -x \"$2\" sleep 120");
        start.ArgumentList.Add("_");
        start.ArgumentList.Add(pidFile);
        start.ArgumentList.Add(path);

        using var child = Process.Start(start)
            ?? throw new InvalidOperationException("could not start the lock holder");
        var holderPid = 0;
        var previous = PushLock.OpenLockFileFailureForTests;
        void KillHolder()
        {
            if (holderPid <= 0)
                return;
            using var kill = Process.Start(new ProcessStartInfo("kill")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                ArgumentList = { "-KILL", "--", $"-{holderPid}" },
            });
            kill?.WaitForExit(5000);
        }
        try
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (File.Exists(pidFile)
                    && int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim(), out holderPid)
                    && LockHeld(path))
                    break;
                await Task.Delay(50);
            }
            Assert.True(holderPid > 0 && LockHeld(path), "child process did not take the flock");

            PushLock.OpenLockFileFailureForTests = candidate =>
                candidate == path ? new InjectedNoSpaceException() : null;
            var error = await Assert.ThrowsAsync<PodmanException>(
                () => PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(1), default));
            Assert.Contains("cannot use the push lock", error.Message);
            Assert.True(File.Exists(path), "the holder's lock file disappeared");
            PushLock.OpenLockFileFailureForTests = previous;

            KillHolder();
            child.WaitForExit(5000);
            holderPid = 0;
            File.Delete(pidFile);

            // HomeOverride stays set, so this proves the killed holder's exact
            // path is released, not some other selected directory.
            Assert.Equal(path, PushLock.LockPathFor(reference));
            using var reacquired = await PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(5), default);
            Assert.NotNull(reacquired);
        }
        finally
        {
            PushLock.OpenLockFileFailureForTests = previous;
            KillHolder();
            if (!child.HasExited)
            {
                try
                {
                    child.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // The child may already have exited.
                }
            }
            child.WaitForExit(5000);
            File.Delete(pidFile);
            PushLock.HomeOverride = previousHome;
            PushLock.IgnoreRuntimeDirectoryForTests = previousIgnore;
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task FifoOnManagedStatFallbackFailsClosed()
    {
        if (OperatingSystem.IsWindows())
            return;

        var reference = UniqueReference();
        var path = PushLock.LockPathFor(reference);
        var previous = PushLock.ForceManagedStatForTests;
        PushLock.ForceManagedStatForTests = candidate => candidate == path;
        Assert.Equal(0, MkFifo(path, 0x180));
        try
        {
            var error = await Assert.ThrowsAsync<PodmanException>(
                () => PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(1), default));
            Assert.Contains("cannot inspect", error.Message);
        }
        finally
        {
            PushLock.ForceManagedStatForTests = previous;
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SocketOnManagedStatFallbackFailsClosed()
    {
        if (OperatingSystem.IsWindows())
            return;

        var reference = UniqueReference();
        var path = PushLock.LockPathFor(reference);
        var previous = PushLock.ForceManagedStatForTests;
        PushLock.ForceManagedStatForTests = candidate => candidate == path;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        try
        {
            var error = await Assert.ThrowsAsync<PodmanException>(
                () => PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(1), default));
            Assert.Contains("cannot inspect", error.Message);
        }
        finally
        {
            PushLock.ForceManagedStatForTests = previous;
            socket.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FifoOnLStatFallbackIsRejected()
    {
        if (OperatingSystem.IsWindows())
            return;

        var previous = PushLock.ForceLStatForTests;
        PushLock.ForceLStatForTests = true;
        try
        {
            var reference = UniqueReference();
            var path = PushLock.LockPathFor(reference);
            Assert.Equal(0, MkFifo(path, 0x180));
            try
            {
                var error = await Assert.ThrowsAsync<PodmanException>(
                    () => PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(1), default));
                Assert.Contains("not a regular file", error.Message);
            }
            finally
            {
                File.Delete(path);
            }
        }
        finally
        {
            PushLock.ForceLStatForTests = previous;
        }
    }

    [Fact]
    public async Task SymlinkOnLStatFallbackIsRejected()
    {
        if (OperatingSystem.IsWindows())
            return;

        var previous = PushLock.ForceLStatForTests;
        PushLock.ForceLStatForTests = true;
        try
        {
            var reference = UniqueReference();
            var path = PushLock.LockPathFor(reference);
            var target = path + ".target";
            File.WriteAllText(target, "not a lock");
            File.CreateSymbolicLink(path, target);
            try
            {
                var error = await Assert.ThrowsAsync<PodmanException>(
                    () => PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(1), default));
                Assert.Contains("not a regular file", error.Message);
            }
            finally
            {
                File.Delete(path);
                File.Delete(target);
            }
        }
        finally
        {
            PushLock.ForceLStatForTests = previous;
        }
    }

    [Fact]
    public async Task FifoLockFileIsRejected()
    {
        if (OperatingSystem.IsWindows())
            return;

        var reference = UniqueReference();
        var path = PushLock.LockPathFor(reference);
        Assert.Equal(0, MkFifo(path, 0x180));
        try
        {
            var error = await Assert.ThrowsAsync<PodmanException>(
                () => PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(1), default));
            Assert.Contains("not a regular file", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SymlinkedLockFileIsRejected()
    {
        if (OperatingSystem.IsWindows())
            return;

        var reference = UniqueReference();
        var path = PushLock.LockPathFor(reference);
        var target = path + ".target";
        await File.WriteAllTextAsync(target, "not a lock");
        File.CreateSymbolicLink(path, target);
        try
        {
            var error = await Assert.ThrowsAsync<PodmanException>(
                () => PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(1), default));
            Assert.Contains("not a regular file", error.Message);
        }
        finally
        {
            File.Delete(path);
            File.Delete(target);
        }
    }

    /// <summary>
    /// A separate process holds the flock, the parent times out, and SIGKILL of
    /// the holder's process group releases the kernel lock.
    /// </summary>
    [UnixFlockFact]
    public async Task CrossProcessHolderTimesOutAndSigkillReleases()
    {
        var reference = UniqueReference();
        var path = PushLock.LockPathFor(reference);
        using (await PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(5), default))
        {
        }

        var pidFile = Path.Combine(Path.GetTempPath(), $"narwhal-lock-child-{Guid.NewGuid():n}.pid");
        var start = new ProcessStartInfo("setsid")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("bash");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("echo $$ > \"$1\"; exec flock -x \"$2\" sleep 120");
        start.ArgumentList.Add("_");
        start.ArgumentList.Add(pidFile);
        start.ArgumentList.Add(path);

        using var child = Process.Start(start)
            ?? throw new InvalidOperationException("could not start the lock holder");

        var holderPid = 0;
        try
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (File.Exists(pidFile)
                    && int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim(), out holderPid)
                    && LockHeld(path))
                    break;
                await Task.Delay(50);
            }
            Assert.True(holderPid > 0 && LockHeld(path), "child process did not take the flock");

            var error = await Assert.ThrowsAsync<PodmanException>(
                () => PushLock.AcquireAsync(reference, TimeSpan.FromMilliseconds(400), default));
            Assert.Contains("could not acquire the push lock", error.Message);
        }
        finally
        {
            if (holderPid > 0)
            {
                using var kill = Process.Start(new ProcessStartInfo("kill")
                {
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    ArgumentList = { "-KILL", "--", $"-{holderPid}" },
                });
                kill?.WaitForExit(5000);
            }
            else
            {
                try
                {
                    child.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // The child may already have exited.
                }
            }
            child.WaitForExit(5000);
            File.Delete(pidFile);
        }

        using var reacquired = await PushLock.AcquireAsync(reference, TimeSpan.FromSeconds(5), default);
        Assert.NotNull(reacquired);
    }

    /// <summary>True when a separate flock process would block.</summary>
    private static bool LockHeld(string path)
    {
        using var probe = Process.Start(new ProcessStartInfo("flock")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            ArgumentList = { "-n", path, "true" },
        });
        if (probe is null)
            return true;
        probe.WaitForExit(5000);
        return probe.ExitCode != 0;
    }

    private static string UniqueReference() => $"test.invalid/narwhal-lock/{Guid.NewGuid():n}:latest";

    private sealed class InjectedNoSpaceException : IOException
    {
        public InjectedNoSpaceException() : base("injected no space") => HResult = 28;
    }

    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [DllImport("libc", EntryPoint = "mkfifo")]
    private static extern int MkFifo(string path, uint mode);

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint Umask(uint mask);
}

/// <summary>Cross-process flock test: Linux only, with the tools it needs.</summary>
public sealed class UnixFlockFactAttribute : FactAttribute
{
    public UnixFlockFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "the cross-process flock test needs Unix";
            return;
        }

        foreach (var tool in new[] { "setsid", "flock", "kill", "bash" })
        {
            if (!ToolExists(tool))
            {
                Skip = $"{tool} is not available";
                return;
            }
        }
    }

    private static bool ToolExists(string name)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(name, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return false;
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
