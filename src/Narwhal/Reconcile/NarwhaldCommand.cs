using Narwhal.Cli;

namespace Narwhal.Reconcile;

/// <summary>
/// The <c>narwhald</c> entrypoint. The published <c>narwhald</c> is the same binary as
/// <c>narwhal</c>; it selects this dispatcher from the executable name.
/// </summary>
public static class NarwhaldCommand
{
    private const string UsageText =
        """
        narwhald - narwhal reconcile agent

        usage:
          narwhald [--config <path>] [--json] [-v] [--no-color]
          narwhald reconcile [--config <path>] [--json]
          narwhald guard [--config <path>]
          narwhald install-units [--config <path>] [--unit-dir <path>] [--system-unit-dir <path>] [--dry-run]
          narwhald install-guard --system --guard-binary <path> --sha256 <pin> [--system-unit-dir <path>] [--dry-run]
        """;

    public static bool IsNarwhaldInvocation(string? processPath) =>
        processPath is not null &&
        Path.GetFileNameWithoutExtension(processPath) is "narwhald" or "narwhal-guard";

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        Invocation invocation;
        try
        {
            invocation = Parse(args);
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }

        if (invocation.Help)
        {
            Console.Out.WriteLine(UsageText);
            return 0;
        }

        var reporter = new Reporter(invocation.Json, invocation.Verbose, invocation.NoColor ? false : null);

        try
        {
            if (invocation.Command == "install-guard")
                return await RunInstallGuardAsync(invocation, reporter, cancellationToken);

            var config = AgentConfigLoader.Load(invocation.ConfigPath);
            return invocation.Command switch
            {
                "reconcile" => await RunReconcileAsync(config, invocation, reporter, cancellationToken),
                "guard" => await RunGuardAsync(config, reporter, cancellationToken),
                "install-units" => await RunInstallUnitsAsync(config, invocation, reporter, cancellationToken),
                _ => await RunDaemonAsync(config, invocation, reporter, cancellationToken),
            };
        }
        catch (OperationCanceledException)
        {
            return invocation.Command == "reconcile" ? 130 : 0;
        }
        catch (AgentConfigException e)
        {
            reporter.Error(e.Message);
            return 1;
        }
        catch (ReconcileException e)
        {
            reporter.Error(e.Message);
            return (int)e.ExitCode;
        }
        catch (IOException e)
        {
            reporter.Error(e.Message);
            return 3;
        }
    }

    private static async Task<int> RunDaemonAsync(
        AgentConfig config,
        Invocation invocation,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        BootPins.Validate(config.TrustDirectory);

        if (!string.Equals(config.Mode, "daemon", StringComparison.Ordinal))
        {
            reporter.Error($"agent mode is '{config.Mode}'; the timer runs narwhald reconcile instead");
            return 1;
        }

        await ReconcileCommand.RunAgentGuardAsync(config, reporter, cancellationToken);
        var store = new ReconcileStore(config.StateDirectory);
        var runner = new DaemonRunner(
            config,
            store,
            token => RunCycleAsync(config, store, reporter, invocation.Verbose, token),
            reporter);
        return await runner.RunAsync(cancellationToken);
    }

    private static async Task<DaemonCycleResult> RunCycleAsync(
        AgentConfig config,
        ReconcileStore store,
        Reporter reporter,
        bool verbose,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await ReconcileCommand.RunEngineAsync(
                config,
                store,
                reporter,
                verbose,
                cancellationToken);
            return new DaemonCycleResult(
                outcome.ExitCode,
                outcome.Status.Phase,
                outcome.RestartRequested,
                outcome.Busy);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentConfigException e)
        {
            reporter.Error(e.Message);
            return new DaemonCycleResult(ReconcileExitCode.Config, ReconcilePhases.Failed);
        }
        catch (ReconcileException e)
        {
            reporter.Error(e.Message);
            return new DaemonCycleResult(e.ExitCode, e.Phase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            reporter.Error(e.Message);
            return new DaemonCycleResult(ReconcileExitCode.Config, ReconcilePhases.Failed);
        }
    }

    private static async Task<int> RunReconcileAsync(
        AgentConfig config,
        Invocation invocation,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        var options = new CliOptions { Command = "reconcile", Json = invocation.Json, Verbose = invocation.Verbose };
        options.Values["config"] = invocation.ConfigPath;
        return await ReconcileCommand.RunAsync(options, reporter, cancellationToken);
    }

    /// <summary>
    /// Idempotent root-owned guard entrypoint. It never executes agent/current; it only inspects
    /// markers and swaps the current symlink back after a failed or hung upgrade.
    /// </summary>
    private static async Task<int> RunGuardAsync(
        AgentConfig config,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        var guard = new AgentUpgrader(config.StateDirectory, source: null, reporter);
        var reverted = await guard.RevertExpiredAsync(cancellationToken, nonBlocking: true);
        if (reverted is null)
            return 0;

        reporter.Warn(
            $"agent guard reverted {reverted.Version} to {reverted.PreviousVersion ?? "no previous version"}; " +
            "the agent records the block on its next cycle");
        return 0;
    }

    private static async Task<int> RunInstallUnitsAsync(
        AgentConfig config,
        Invocation invocation,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        var plan = SystemdUnitInstaller.Plan(config, invocation.UnitDirectory);
        var runner = invocation.DryRun ? null : new ProcessUnitCommandRunner(reporter, invocation.Verbose);
        await SystemdUnitInstaller.ApplyAsync(
            plan,
            invocation.DryRun,
            reporter,
            runner,
            cancellationToken);

        if (invocation.SystemUnitDirectory is { } systemDirectory)
        {
            if (!invocation.DryRun && !Environment.IsPrivilegedProcess)
                throw new AgentConfigException("installing guard system units requires root");

            var guardPlan = SystemdUnitInstaller.PlanGuard(systemDirectory);
            await SystemdUnitInstaller.ApplyAsync(
                guardPlan,
                invocation.DryRun,
                reporter,
                runner,
                cancellationToken);
        }

        return 0;
    }

    private static async Task<int> RunInstallGuardAsync(
        Invocation invocation,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        if (!invocation.System)
            throw new AgentConfigException("install-guard requires --system");
        if (invocation.GuardBinary is null)
            throw new AgentConfigException("install-guard requires --guard-binary <path>");
        if (invocation.GuardSha256 is null)
            throw new AgentConfigException("install-guard requires --sha256 <pin>");
        if (!invocation.DryRun && !Environment.IsPrivilegedProcess)
            throw new AgentConfigException("install-guard requires root");

        var runner = invocation.DryRun ? null : new ProcessUnitCommandRunner(reporter, invocation.Verbose);
        return await GuardInstaller.InstallAsync(
            invocation.GuardBinary,
            invocation.GuardSha256,
            invocation.SystemUnitDirectory ?? GuardInstaller.DefaultSystemUnitDirectory,
            invocation.DryRun,
            reporter,
            runner,
            cancellationToken);
    }

    private sealed record Invocation(
        string Command,
        string ConfigPath,
        bool Json,
        bool Verbose,
        bool NoColor,
        bool DryRun,
        bool Help,
        bool System,
        string? UnitDirectory,
        string? SystemUnitDirectory,
        string? GuardBinary,
        string? GuardSha256);

    private static Invocation Parse(string[] args)
    {
        var command = "daemon";
        var configPath = AgentConfigLoader.DefaultPath;
        var json = false;
        var verbose = false;
        var noColor = false;
        var dryRun = false;
        var help = false;
        var system = false;
        string? unitDirectory = null;
        string? systemUnitDirectory = null;
        string? guardBinary = null;
        string? guardSha256 = null;
        var commandSeen = false;

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (token is "-h" or "--help")
            {
                help = true;
                continue;
            }

            if (token is "--json")
            {
                json = true;
                continue;
            }

            if (token is "-v" or "--verbose")
            {
                verbose = true;
                continue;
            }

            if (token is "--no-color")
            {
                noColor = true;
                continue;
            }

            if (token is "--dry-run")
            {
                dryRun = true;
                continue;
            }

            if (token is "--system")
            {
                system = true;
                continue;
            }

            var (name, inlineValue) = SplitOption(token);
            switch (name)
            {
                case "config":
                    configPath = inlineValue ?? RequireValue(args, ref index, "--config");
                    continue;
                case "unit-dir":
                    unitDirectory = inlineValue ?? RequireValue(args, ref index, "--unit-dir");
                    continue;
                case "system-unit-dir":
                    systemUnitDirectory = inlineValue ?? RequireValue(args, ref index, "--system-unit-dir");
                    continue;
                case "guard-binary":
                    guardBinary = inlineValue ?? RequireValue(args, ref index, "--guard-binary");
                    continue;
                case "sha256":
                    guardSha256 = inlineValue ?? RequireValue(args, ref index, "--sha256");
                    continue;
            }

            if (token.StartsWith('-'))
                throw new UsageException($"unknown option '{token}'\n\n{UsageText}");

            if (commandSeen)
                throw new UsageException($"unexpected argument '{token}'\n\n{UsageText}");

            if (token is not ("reconcile" or "guard" or "install-units" or "install-guard"))
                throw new UsageException($"unknown command '{token}'\n\n{UsageText}");

            command = token;
            commandSeen = true;
        }

        if (dryRun && command is not ("install-units" or "install-guard"))
            throw new UsageException("--dry-run is only valid with install-units or install-guard");
        if (command != "install-guard" && (system || guardBinary is not null || guardSha256 is not null))
            throw new UsageException("--system, --guard-binary, and --sha256 are only valid with install-guard");

        return new Invocation(
            command,
            configPath,
            json,
            verbose,
            noColor,
            dryRun,
            help,
            system,
            unitDirectory,
            systemUnitDirectory,
            guardBinary,
            guardSha256);
    }

    private static (string Name, string? Value) SplitOption(string token)
    {
        if (!token.StartsWith("--", StringComparison.Ordinal))
            return (token, null);
        var trimmed = token[2..];
        var equals = trimmed.IndexOf('=');
        return equals < 0 ? (trimmed, null) : (trimmed[..equals], trimmed[(equals + 1)..]);
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new UsageException($"option '{option}' requires a value");
        return args[++index];
    }
}
