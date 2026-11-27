using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class SystemdUnitTests
{
    [Fact]
    public void RepoTemplatesMatchAppendixA()
    {
        var root = RepoRoot();
        var daemon = File.ReadAllText(Path.Combine(root, "deploy", "systemd", "user", "narwhal.service"));
        var reconcile = File.ReadAllText(Path.Combine(root, "deploy", "systemd", "user", "narwhal-reconcile.service"));
        var timer = File.ReadAllText(Path.Combine(root, "deploy", "systemd", "user", "narwhal-reconcile.timer"));

        Assert.Contains("ExecStart=/var/lib/narwhal/agent/current/narwhal daemon --config /etc/narwhal/agent.toml", daemon, StringComparison.Ordinal);
        Assert.Contains("Type=simple", daemon, StringComparison.Ordinal);
        Assert.Contains("Restart=always", daemon, StringComparison.Ordinal);
        Assert.Contains("RestartSec=5", daemon, StringComparison.Ordinal);
        Assert.Contains("TimeoutStopSec=120", daemon, StringComparison.Ordinal);
        Assert.Contains("KillMode=process", daemon, StringComparison.Ordinal);
        Assert.Contains("Delegate=yes", daemon, StringComparison.Ordinal);
        Assert.Contains("PrivateTmp=yes", daemon, StringComparison.Ordinal);
        Assert.Contains("WantedBy=default.target", daemon, StringComparison.Ordinal);

        Assert.Contains("Type=oneshot", reconcile, StringComparison.Ordinal);
        Assert.Contains(
            "ExecStart=/var/lib/narwhal/agent/current/narwhal reconcile --config /etc/narwhal/agent.toml",
            reconcile,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RemainAfterExit", reconcile, StringComparison.Ordinal);

        Assert.Contains("OnBootSec=30s", timer, StringComparison.Ordinal);
        Assert.Contains("OnUnitActiveSec=60s", timer, StringComparison.Ordinal);
        Assert.Contains("Persistent=true", timer, StringComparison.Ordinal);
        Assert.Contains("WantedBy=timers.target", timer, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedTemplatesMatchTheRepoFiles()
    {
        var root = RepoRoot();
        foreach (var name in SystemdUnitTemplates.Names)
        {
            var repo = File.ReadAllText(Path.Combine(root, "deploy", "systemd", "user", name));
            Assert.Equal(repo, SystemdUnitTemplates.Load(name), StringComparer.Ordinal);
        }
    }

    [Fact]
    public void TemplatesDoNotSetPodmanHostileHardening()
    {
        foreach (var template in SystemdUnitTemplates.All())
        {
            SystemdUnitTemplates.Validate(template);
            Assert.DoesNotContain("ProtectHome", template.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("ProtectSystem", template.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("NoNewPrivileges", template.Content, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(SystemdUnitTemplates.DaemonUnit)]
    [InlineData(SystemdUnitTemplates.ReconcileService)]
    public void RejectsAUnitWithoutItsSubcommand(string name)
    {
        var content = "[Service]\nExecStart=/var/lib/narwhal/agent/current/narwhal --config /etc/narwhal/agent.toml\n";

        Assert.Throws<AgentConfigException>(() =>
            SystemdUnitTemplates.Validate(new SystemdUnitTemplate(name, content)));
    }

    [Theory]
    [InlineData(SystemdUnitTemplates.DaemonUnit, "daemon")]
    [InlineData(SystemdUnitTemplates.ReconcileService, "reconcile")]
    public void AcceptsAnExecStartSubcommandAtEndOfLine(string name, string subcommand)
    {
        var content = $"[Service]\nExecStart=/var/lib/narwhal/agent/current/narwhal {subcommand}";

        SystemdUnitTemplates.Validate(new SystemdUnitTemplate(name, content));
    }

    [Theory]
    [InlineData(SystemdUnitTemplates.DaemonUnit, "daemon")]
    [InlineData(SystemdUnitTemplates.ReconcileService, "reconcile")]
    public void AcceptsAnExecStartSubcommandFollowedByTab(string name, string subcommand)
    {
        var content = $"[Service]\nExecStart=/var/lib/narwhal/agent/current/narwhal {subcommand}\t--config /etc/narwhal/agent.toml";

        SystemdUnitTemplates.Validate(new SystemdUnitTemplate(name, content));
    }

    [Theory]
    [InlineData(SystemdUnitTemplates.DaemonUnit, "daemon")]
    [InlineData(SystemdUnitTemplates.ReconcileService, "reconcile")]
    public void RejectsAnExecStartWithASubcommandPrefix(string name, string subcommand)
    {
        var content = $"[Service]\nExecStart=/var/lib/narwhal/agent/current/narwhal {subcommand}x --config /etc/narwhal/agent.toml";

        Assert.Throws<AgentConfigException>(() =>
            SystemdUnitTemplates.Validate(new SystemdUnitTemplate(name, content)));
    }

    [Fact]
    public void GuardTemplatesUseTheStableBinaryAndRunAsNarwhal()
    {
        var root = RepoRoot();
        foreach (var name in SystemdUnitTemplates.GuardNames)
        {
            var repo = File.ReadAllText(Path.Combine(root, "deploy", "systemd", "system", name));
            Assert.Equal(repo, SystemdUnitTemplates.Load(name), StringComparer.Ordinal);
        }

        var service = SystemdUnitTemplates.Load(SystemdUnitTemplates.GuardService);
        Assert.Contains("User=narwhal", service, StringComparison.Ordinal);
        Assert.Contains(
            "ExecStart=/usr/local/libexec/narwhal guard --config /etc/narwhal/agent.toml",
            service,
            StringComparison.Ordinal);
        Assert.DoesNotContain("/var/lib/narwhal/agent/current", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ListenStream", service, StringComparison.Ordinal);
        Assert.Contains(
            "ConditionPathExists=/usr/local/libexec/narwhal",
            service,
            StringComparison.Ordinal);
        SystemdUnitTemplates.ValidateGuard(new SystemdUnitTemplate(SystemdUnitTemplates.GuardService, service));
    }

    [Fact]
    public void GuardPlanUsesSystemScopeAndEnablesTheTimer()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var plan = SystemdUnitInstaller.PlanGuard(directory);

        Assert.False(plan.UserScope);
        Assert.Equal(SystemdUnitTemplates.GuardTimer, plan.EnableUnit);
        Assert.Equal(2, plan.Files.Count);
        Assert.Equal(["daemon-reload"], plan.Commands[0]);
        Assert.Equal(["enable", "--now", SystemdUnitTemplates.GuardTimer], plan.Commands[1]);
        Assert.DoesNotContain(plan.Commands.SelectMany(command => command), value => value == "--user");
    }

    [Fact]
    public void GuardPlanRejectsARelativeSystemUnitDirectory()
    {
        Assert.Throws<AgentConfigException>(() => SystemdUnitInstaller.PlanGuard("etc/systemd/system"));
    }

    [Fact]
    public void GuardBinaryPathIsPinned()
    {
        Assert.Equal("/usr/local/libexec/narwhal", GuardInstaller.GuardBinaryPath);
    }

    [Fact]
    public async Task StopsOnTheFirstFailingSystemctlCommand()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var plan = SystemdUnitInstaller.Plan(Config("daemon"), Path.Combine(directory, "units"));
        var runner = new RecordingRunner(failOn: "--user disable --now narwhal-reconcile.timer");
        var reporter = new Narwhal.Cli.Reporter(false, false, false, new StringWriter(), new StringWriter());

        var error = await Assert.ThrowsAsync<ReconcileException>(() =>
            SystemdUnitInstaller.ApplyAsync(plan, dryRun: false, reporter, runner, CancellationToken.None));

        Assert.Equal(ReconcileExitCode.Runtime, error.ExitCode);
        Assert.Contains("systemctl --user disable --now narwhal-reconcile.timer exited 1", error.Message, StringComparison.Ordinal);
        Assert.Equal(
            [
                "--user daemon-reload",
                "--user disable --now narwhal-reconcile.timer",
            ],
            runner.Commands);
    }

    [Theory]
    [InlineData("daemon", "narwhal.service", "narwhal-reconcile.timer")]
    [InlineData("timer", "narwhal-reconcile.timer", "narwhal.service")]
    public void SelectsExactlyOneUnitForTheMode(string mode, string enable, string disable)
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var plan = SystemdUnitInstaller.Plan(Config(mode), directory);

        Assert.Equal(enable, plan.EnableUnit);
        Assert.Equal(disable, plan.DisableUnit);
        Assert.Equal(3, plan.Files.Count);
        Assert.Equal(["--user", "daemon-reload"], plan.Commands[0]);
        Assert.Equal(["--user", "disable", "--now", disable], plan.Commands[1]);
        Assert.Equal(["--user", "enable", "--now", enable], plan.Commands[2]);
    }

    [Fact]
    public async Task DryRunWritesNothingAndRunsNoCommands()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var unitDirectory = Path.Combine(directory, "units");
        var output = new StringWriter();
        var reporter = new Narwhal.Cli.Reporter(false, false, false, output, output);
        var plan = SystemdUnitInstaller.Plan(Config("daemon"), unitDirectory);

        await SystemdUnitInstaller.ApplyAsync(plan, dryRun: true, reporter, runner: null, CancellationToken.None);

        Assert.False(Directory.Exists(unitDirectory));
        Assert.Contains("would run systemctl --user enable --now narwhal.service", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("would run systemctl --user disable --now narwhal-reconcile.timer", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallWritesUnitsAndEnablesOneMode()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var unitDirectory = Path.Combine(directory, "units");
        var plan = SystemdUnitInstaller.Plan(Config("daemon"), unitDirectory);
        var runner = new RecordingRunner();
        var reporter = new Narwhal.Cli.Reporter(false, false, false, new StringWriter(), new StringWriter());

        await SystemdUnitInstaller.ApplyAsync(plan, dryRun: false, reporter, runner, CancellationToken.None);

        foreach (var name in SystemdUnitTemplates.Names)
        {
            var path = Path.Combine(unitDirectory, name);
            Assert.True(File.Exists(path));
            Assert.Equal(SystemdUnitTemplates.Load(name), File.ReadAllText(path), StringComparer.Ordinal);
        }

        Assert.Equal(
            [
                "--user daemon-reload",
                "--user disable --now narwhal-reconcile.timer",
                "--user enable --now narwhal.service",
            ],
            runner.Commands);
    }

    [Fact]
    public async Task UnchangedUnitsSkipDaemonReload()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var unitDirectory = Path.Combine(directory, "units");
        var reporter = new Narwhal.Cli.Reporter(false, false, false, new StringWriter(), new StringWriter());
        await SystemdUnitInstaller.ApplyAsync(
            SystemdUnitInstaller.Plan(Config("daemon"), unitDirectory),
            dryRun: false,
            reporter,
            new RecordingRunner(),
            CancellationToken.None);

        var plan = SystemdUnitInstaller.Plan(Config("daemon"), unitDirectory);
        Assert.All(plan.Files, file => Assert.False(file.Changed));
        Assert.DoesNotContain(plan.Commands, command => command.Contains("daemon-reload"));
    }

    [Fact]
    public void InstallsTheGuardBinaryWithTheExpectedModeAndIsIdempotent()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var directory = DaemonRunnerTests.TestDirectory();
        var source = Path.Combine(directory, "narwhal");
        var destination = Path.Combine(directory, "libexec", "narwhal");
        File.WriteAllText(source, "guard");
        var output = new StringWriter();
        var reporter = new Narwhal.Cli.Reporter(false, false, false, output, output);

        Assert.True(GuardInstaller.InstallBinary(source, destination, Pin(source), dryRun: false, reporter));
        Assert.Equal("guard", File.ReadAllText(destination));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            File.GetUnixFileMode(destination));

        Assert.False(GuardInstaller.InstallBinary(source, destination, Pin(source), dryRun: false, reporter));
        Assert.Contains($"{destination} is up to date", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DryRunDoesNotWriteTheGuardBinary()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var source = Path.Combine(directory, "narwhal");
        var destination = Path.Combine(directory, "libexec", "narwhal");
        File.WriteAllText(source, "guard");
        var reporter = new Narwhal.Cli.Reporter(false, false, false, new StringWriter(), new StringWriter());

        Assert.True(GuardInstaller.InstallBinary(source, destination, Pin(source), dryRun: true, reporter));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void RejectsASymlinkedGuardSource()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var real = Path.Combine(directory, "real");
        var source = Path.Combine(directory, "narwhal");
        File.WriteAllText(real, "guard");
        File.CreateSymbolicLink(source, real);
        var reporter = new Narwhal.Cli.Reporter(false, false, false, new StringWriter(), new StringWriter());

        Assert.Throws<AgentConfigException>(() =>
            GuardInstaller.InstallBinary(source, Path.Combine(directory, "dest"), Pin(real), dryRun: true, reporter));
    }

    [Fact]
    public void RejectsAnEmptyGuardSource()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var source = Path.Combine(directory, "narwhal");
        File.WriteAllText(source, "");
        var reporter = new Narwhal.Cli.Reporter(false, false, false, new StringWriter(), new StringWriter());

        Assert.Throws<AgentConfigException>(() =>
            GuardInstaller.InstallBinary(source, Path.Combine(directory, "dest"), new string('a', 64), dryRun: true, reporter));
    }

    [Fact]
    public void RejectsADirectoryGuardDestination()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var source = Path.Combine(directory, "narwhal");
        var destination = Path.Combine(directory, "libexec");
        File.WriteAllText(source, "guard");
        Directory.CreateDirectory(destination);
        var reporter = new Narwhal.Cli.Reporter(false, false, false, new StringWriter(), new StringWriter());

        Assert.Throws<AgentConfigException>(() =>
            GuardInstaller.InstallBinary(source, destination, Pin(source), dryRun: true, reporter));
    }

    [Fact]
    public void RejectsAGuardBinaryThatDoesNotMatchThePin()
    {
        var directory = DaemonRunnerTests.TestDirectory();
        var source = Path.Combine(directory, "narwhal");
        var destination = Path.Combine(directory, "libexec", "narwhal");
        File.WriteAllText(source, "guard");
        var reporter = new Narwhal.Cli.Reporter(false, false, false, new StringWriter(), new StringWriter());

        Assert.Throws<AgentConfigException>(() =>
            GuardInstaller.InstallBinary(source, destination, new string('0', 64), dryRun: false, reporter));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void RejectsAGroupWritableGuardSource()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var directory = DaemonRunnerTests.TestDirectory();
        var source = Path.Combine(directory, "narwhal");
        File.WriteAllText(source, "guard");
        File.SetUnixFileMode(
            source,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
            UnixFileMode.OtherRead);
        var reporter = new Narwhal.Cli.Reporter(false, false, false, new StringWriter(), new StringWriter());

        Assert.Throws<AgentConfigException>(() =>
            GuardInstaller.InstallBinary(
                source,
                Path.Combine(directory, "dest"),
                Pin(source),
                dryRun: true,
                reporter));
    }

    private static string Pin(string path) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private static AgentConfig Config(string mode) => AgentConfigLoader.Parse($$"""
        project = "shop"
        image_registry = "registry.example.com"
        desired_repo = "shop/state/target/desired"
        mode = "{{mode}}"

        [target]
        provider = "aws"
        region = "us-east-1"
        arch = "amd64"
        selector = { stack = "prod" }
        """);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "docs", "cloud.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private sealed class RecordingRunner(string? failOn = null) : IUnitCommandRunner
    {
        public List<string> Commands { get; } = [];

        public Task<int> RunAsync(string file, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            Assert.Equal("systemctl", file);
            var command = string.Join(' ', args);
            Commands.Add(command);
            return Task.FromResult(command == failOn ? 1 : 0);
        }
    }
}
