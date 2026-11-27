using Narwhal.Cli;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class StatusWatchTests
{
    [Fact]
    public async Task PollsUntilCancelledAndReturnsTheLastExitCode()
    {
        using var cancellation = new CancellationTokenSource();
        var polls = 0;
        var delays = 0;
        var watch = new StatusWatch(
            _ =>
            {
                polls++;
                return Task.FromResult(new StatusWatchPoll((int)ReconcileExitCode.Runtime, false));
            },
            (interval, _) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(5), interval);
                delays++;
                if (delays >= 2)
                    cancellation.Cancel();
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5),
            ReconcileTestData.Reporter());

        var exit = await watch.RunAsync(cancellation.Token);

        Assert.Equal(2, polls);
        Assert.Equal((int)ReconcileExitCode.Runtime, exit);
    }

    [Fact]
    public async Task ATerminalResultStopsWatchingImmediately()
    {
        var delayed = false;
        var watch = new StatusWatch(
            _ => Task.FromResult(new StatusWatchPoll((int)ReconcileExitCode.Verification, true)),
            (_, _) =>
            {
                delayed = true;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5),
            ReconcileTestData.Reporter());

        var exit = await watch.RunAsync(CancellationToken.None);

        Assert.Equal((int)ReconcileExitCode.Verification, exit);
        Assert.False(delayed);
    }

    [Fact]
    public async Task ACancelledTokenStopsWithoutPolling()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var polls = 0;
        var watch = new StatusWatch(
            _ =>
            {
                polls++;
                return Task.FromResult(new StatusWatchPoll(0, false));
            },
            (_, _) => Task.CompletedTask,
            TimeSpan.FromSeconds(5),
            ReconcileTestData.Reporter());

        var exit = await watch.RunAsync(cancellation.Token);

        Assert.Equal(0, exit);
        Assert.Equal(0, polls);
    }

    [Fact]
    public async Task CancellationDuringTheDelayReturnsTheLastResult()
    {
        using var cancellation = new CancellationTokenSource();
        var watch = new StatusWatch(
            _ => Task.FromResult(new StatusWatchPoll(0, false)),
            (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5),
            ReconcileTestData.Reporter());

        var exit = await watch.RunAsync(cancellation.Token);

        Assert.Equal(0, exit);
    }
}

public class AgentCommandTests
{
    [Fact]
    public async Task InstallUnitsDryRunWritesNothing()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var configPath = WriteConfig(directory, "daemon");
        var unitDirectory = Path.Combine(directory, "units");

        var exit = await AgentCommands.RunAsync(
            ["install-units", "--config", configPath, "--unit-dir", unitDirectory, "--dry-run"],
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(unitDirectory));
    }

    [Fact]
    public async Task InstallUnitsDryRunIncludesGuardSystemUnits()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var configPath = WriteConfig(directory, "daemon");
        var unitDirectory = Path.Combine(directory, "units");
        var systemUnitDirectory = Path.Combine(directory, "system-units");

        var exit = await AgentCommands.RunAsync(
            [
                "install-units",
                "--config", configPath,
                "--unit-dir", unitDirectory,
                "--system-unit-dir", systemUnitDirectory,
                "--dry-run",
            ],
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(unitDirectory));
        Assert.False(Directory.Exists(systemUnitDirectory));
    }

    [Fact]
    public async Task GuardRevertsAHungAgentWithoutRunningIt()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var configPath = WriteConfig(directory, "daemon");
        var agent = StageHungAgent(directory);

        var exit = await AgentCommands.RunAsync(["guard", "--config", configPath], CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("0.3.0", new FileInfo(Path.Combine(agent, "current")).LinkTarget);
        Assert.False(File.Exists(Path.Combine(agent, "pending")));
        Assert.True(File.Exists(Path.Combine(agent, "reverted")));
        Assert.False(File.Exists(Path.Combine(directory, "state.json")));

        var second = await AgentCommands.RunAsync(["guard", "--config", configPath], CancellationToken.None);
        Assert.Equal(0, second);
        Assert.Equal("0.3.0", new FileInfo(Path.Combine(agent, "current")).LinkTarget);
    }

    [Fact]
    public async Task GuardDoesNotTakeTheReconcileLock()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var configPath = WriteConfig(directory, "daemon");
        var agent = StageHungAgent(directory);
        using var held = await ReconcileLock.AcquireAsync(
            Path.Combine(directory, "lock"),
            TimeSpan.Zero,
            CancellationToken.None);
        Assert.NotNull(held);

        var exit = await AgentCommands.RunAsync(["guard", "--config", configPath], CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("0.3.0", new FileInfo(Path.Combine(agent, "current")).LinkTarget);
        Assert.False(File.Exists(Path.Combine(agent, "pending")));
    }

    [Fact]
    public async Task GuardSkipsWhileTheUpgradeLockIsHeld()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var configPath = WriteConfig(directory, "daemon");
        var agent = StageHungAgent(directory);
        using (await ReconcileLock.AcquireAsync(
                   Path.Combine(directory, "agent-upgrade.lock"),
                   TimeSpan.Zero,
                   CancellationToken.None))
        {
            var skipped = await AgentCommands.RunAsync(["guard", "--config", configPath], CancellationToken.None);
            Assert.Equal(0, skipped);
            Assert.Equal("0.4.0", new FileInfo(Path.Combine(agent, "current")).LinkTarget);
            Assert.True(File.Exists(Path.Combine(agent, "pending")));
        }

        var exit = await AgentCommands.RunAsync(["guard", "--config", configPath], CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("0.3.0", new FileInfo(Path.Combine(agent, "current")).LinkTarget);
    }

    [Fact]
    public async Task InstallGuardDryRunWritesNothing()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var binary = Path.Combine(directory, "narwhal");
        var unitDirectory = Path.Combine(directory, "system-units");
        File.WriteAllText(binary, "guard");
        var pin = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(binary)));

        var exit = await AgentCommands.RunAsync(
            [
                "install-guard",
                "--system",
                "--guard-binary", binary,
                "--sha256", pin,
                "--system-unit-dir", unitDirectory,
                "--dry-run",
            ],
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(unitDirectory));
    }

    [Fact]
    public async Task InstallGuardRequiresSystemBinaryAndPin()
    {
        Assert.Equal(1, await AgentCommands.RunAsync(["install-guard", "--dry-run"], CancellationToken.None));
        Assert.Equal(
            1,
            await AgentCommands.RunAsync(
                ["install-guard", "--system", "--dry-run"],
                CancellationToken.None));
        Assert.Equal(
            1,
            await AgentCommands.RunAsync(
                ["install-guard", "--system", "--guard-binary", "/dev/null", "--dry-run"],
                CancellationToken.None));
    }

    [Fact]
    public async Task InstallGuardRefusesANonRootCaller()
    {
        if (Environment.IsPrivilegedProcess)
            return;

        var directory = DaemonRunnerTests.TestDirectory();
        var binary = Path.Combine(directory, "narwhal");
        var unitDirectory = Path.Combine(directory, "system-units");
        File.WriteAllText(binary, "guard");
        var pin = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(binary)));

        var exit = await AgentCommands.RunAsync(
            [
                "install-guard",
                "--system",
                "--guard-binary", binary,
                "--sha256", pin,
                "--system-unit-dir", unitDirectory,
            ],
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(Directory.Exists(unitDirectory));
    }

    [Fact]
    public async Task SigtermCancellationStopsDaemonCleanly()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var configPath = WriteConfig(directory, "daemon");
        ReconcileTestData.CreateBootPins(directory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exit = await AgentCommands.RunAsync(["daemon", "--config", configPath], cancellation.Token);

        Assert.Equal(0, exit);
    }

    private static string StageHungAgent(string directory)
    {
        var agent = Path.Combine(directory, "agent");
        Directory.CreateDirectory(Path.Combine(agent, "0.3.0"));
        Directory.CreateDirectory(Path.Combine(agent, "0.4.0"));
        File.WriteAllText(Path.Combine(agent, "0.3.0", "narwhal"), "old");
        File.WriteAllText(Path.Combine(agent, "0.4.0", "narwhal"), "hung");
        File.CreateSymbolicLink(Path.Combine(agent, "current"), "0.4.0");
        var started = DateTimeOffset.UtcNow.AddMinutes(-20).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(agent, "pending"), $$"""
            {
              "version": "0.4.0",
              "digest": "sha256:{{new string('d', 64)}}",
              "binary_sha256": "{{new string('c', 64)}}",
              "previous_version": "0.3.0",
              "previous_binary_sha256": null,
              "attempts": 0,
              "first_started_at": "{{started}}",
              "started_at": "{{started}}"
            }
            """);
        return agent;
    }

    [Fact]
    public async Task TimerModeDoesNotStartTheDaemonLoop()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var configPath = WriteConfig(directory, "timer");
        ReconcileTestData.CreateBootPins(directory);

        var exit = await AgentCommands.RunAsync(["daemon", "--config", configPath], CancellationToken.None);

        Assert.Equal(1, exit);
    }

    [Fact]
    public void RejectsDryRunOutsideInstallCommands()
    {
        var error = Assert.Throws<UsageException>(() => CommandLine.Parse(["reconcile", "--dry-run"]));
        Assert.Contains("unknown option '--dry-run'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingConfigExitsOne()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "agent.toml");

        var exit = await AgentCommands.RunAsync(["daemon", "--config", missing], CancellationToken.None);

        Assert.Equal(1, exit);
    }

    private static string WriteConfig(string directory, string mode)
    {
        var trustDirectory = Path.Combine(directory, "boot-trust");
        Directory.CreateDirectory(trustDirectory);
        var path = Path.Combine(directory, "agent.toml");
        File.WriteAllText(path, $$"""
            project = "shop"
            image_registry = "registry.example.com"
            desired_repo = "shop/state/target/desired"
            mode = "{{mode}}"
            state_dir = "{{directory}}"
            trust_dir = "{{trustDirectory}}"

            [target]
            provider = "aws"
            region = "us-east-1"
            arch = "amd64"
            selector = { stack = "prod" }
            """);
        return path;
    }
}
