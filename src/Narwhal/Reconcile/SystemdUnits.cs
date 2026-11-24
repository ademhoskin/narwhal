using Narwhal.Cli;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;

namespace Narwhal.Reconcile;

/// <summary>A user unit template shipped in <c>deploy/systemd/user</c> and embedded in the binary.</summary>
public sealed record SystemdUnitTemplate(string Name, string Content);

/// <summary>One unit file the installer will write.</summary>
public sealed record PlannedUnitFile(string Name, string Path, string Content, bool Changed);

/// <summary>The files and systemctl commands for one mode.</summary>
public sealed record SystemdUnitPlan(
    string UnitDirectory,
    string EnableUnit,
    string DisableUnit,
    IReadOnlyList<PlannedUnitFile> Files,
    IReadOnlyList<IReadOnlyList<string>> Commands,
    bool UserScope = true);

public interface IUnitCommandRunner
{
    Task<int> RunAsync(string file, IReadOnlyList<string> args, CancellationToken cancellationToken);
}

/// <summary>Reads the unit templates and renders the mode-specific install plan.</summary>
public static class SystemdUnitTemplates
{
    public const string DaemonUnit = "narwhald.service";
    public const string ReconcileService = "narwhald-reconcile.service";
    public const string ReconcileTimer = "narwhald-reconcile.timer";
    public const string GuardService = "narwhald-guard.service";
    public const string GuardTimer = "narwhald-guard.timer";

    public static readonly string[] Names = [DaemonUnit, ReconcileService, ReconcileTimer];
    public static readonly string[] GuardNames = [GuardService, GuardTimer];

    public static string Load(string name)
    {
        var resource = "Narwhal.Units." + name;
        using var stream = typeof(SystemdUnitTemplates).Assembly.GetManifestResourceStream(resource)
                           ?? throw new AgentConfigException($"embedded unit template not found: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<SystemdUnitTemplate> All() =>
        Names.Select(name => new SystemdUnitTemplate(name, Load(name))).ToList();

    public static IReadOnlyList<SystemdUnitTemplate> AllGuard() =>
        GuardNames.Select(name => new SystemdUnitTemplate(name, Load(name))).ToList();

    /// <summary>Rejects directives the design explicitly does not set for rootless podman.</summary>
    public static void Validate(SystemdUnitTemplate template)
    {
        foreach (var forbidden in new[] { "ProtectHome=", "ProtectSystem=", "NoNewPrivileges=" })
        {
            if (template.Content.Contains(forbidden, StringComparison.Ordinal))
                throw new AgentConfigException(
                    $"{template.Name}: {forbidden.TrimEnd('=')} must not be set for rootless podman");
        }

        if (template.Name is DaemonUnit or ReconcileService &&
            !template.Content.Contains(
                "ExecStart=/var/lib/narwhal/agent/current/narwhald",
                StringComparison.Ordinal))
        {
            throw new AgentConfigException($"{template.Name}: missing the pinned narwhald ExecStart");
        }
    }

    /// <summary>Validates the root timer that supervises a stable guard binary, never agent/current.</summary>
    public static void ValidateGuard(SystemdUnitTemplate template)
    {
        foreach (var forbidden in new[] { "ProtectHome=", "ProtectSystem=", "NoNewPrivileges=", "ListenStream=", "ListenDatagram=" })
        {
            if (template.Content.Contains(forbidden, StringComparison.Ordinal))
                throw new AgentConfigException(
                    $"{template.Name}: {forbidden.TrimEnd('=')} must not be set");
        }

        if (template.Name == GuardService)
        {
            if (!template.Content.Contains("User=narwhal", StringComparison.Ordinal))
                throw new AgentConfigException($"{template.Name}: expected User=narwhal");
            if (!template.Content.Contains(
                    "ExecStart=/usr/local/libexec/narwhal-guard guard --config /etc/narwhal/agent.toml",
                    StringComparison.Ordinal))
            {
                throw new AgentConfigException(
                    $"{template.Name}: expected the stable /usr/local/libexec/narwhal-guard ExecStart");
            }

            if (template.Content.Contains("/var/lib/narwhal/agent/current", StringComparison.Ordinal))
                throw new AgentConfigException($"{template.Name}: the guard must not run agent/current");
            if (!template.Content.Contains(
                    "ConditionPathExists=/usr/local/libexec/narwhal-guard",
                    StringComparison.Ordinal))
            {
                throw new AgentConfigException(
                    $"{template.Name}: expected ConditionPathExists for the guard binary");
            }
        }
    }
}

/// <summary>
/// Installs the stable root-owned guard binary and the root units. The binary never comes from
/// agent/current; bootstrap passes an explicit path so a failed upgrade cannot replace the guard.
/// </summary>
public static class GuardInstaller
{
    public const string GuardBinaryPath = "/usr/local/libexec/narwhal-guard";
    public const string DefaultSystemUnitDirectory = "/etc/systemd/system";
    private const UnixFileMode InstalledMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode InstalledDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public static async Task<int> InstallAsync(
        string source,
        string sha256Pin,
        string unitDirectory,
        bool dryRun,
        Reporter reporter,
        IUnitCommandRunner? runner,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathRooted(unitDirectory))
            throw new AgentConfigException("system unit directory must be an absolute path");
        InstallBinary(source, GuardBinaryPath, sha256Pin, dryRun, reporter);

        var plan = SystemdUnitInstaller.PlanGuard(unitDirectory);
        await SystemdUnitInstaller.ApplyAsync(plan, dryRun, reporter, runner, cancellationToken);
        return 0;
    }

    public static bool InstallBinary(
        string source,
        string destination,
        string sha256Pin,
        bool dryRun,
        Reporter reporter)
    {
        if (!OperatingSystem.IsLinux())
            throw new AgentConfigException("install-guard is supported on Linux only");
        var expected = AgentDigest.Normalize(sha256Pin)[AgentDigest.Prefix.Length..];

        if (Directory.Exists(destination))
            throw new AgentConfigException($"refusing to install the guard over a directory: {destination}");
        if (new FileInfo(destination).LinkTarget is not null)
            throw new AgentConfigException($"refusing to install the guard over a symlink: {destination}");

        using var handle = OpenValidatedSource(source);
        if (dryRun)
        {
            var actual = HashHandle(handle);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new AgentConfigException(
                    $"guard binary SHA-256 is {actual}, expected {expected}");
            reporter.Info($"would install {source} to {destination}");
            return true;
        }

        if (File.Exists(destination) &&
            string.Equals(HashFile(destination), expected, StringComparison.Ordinal) &&
            File.GetUnixFileMode(destination) == InstalledMode)
        {
            reporter.Info($"{destination} is up to date");
            return false;
        }

        var directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory, InstalledDirectoryMode);
        }

        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("n");
        try
        {
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81920,
                       FileOptions.SequentialScan))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long offset = 0;
                int read;
                while ((read = RandomAccess.Read(handle, buffer, offset)) > 0)
                {
                    output.Write(buffer, 0, read);
                    hash.AppendData(buffer, 0, read);
                    offset += read;
                }

                output.Flush(true);
                var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    throw new AgentConfigException(
                        $"guard binary SHA-256 is {actual}, expected {expected}");
            }

            File.SetUnixFileMode(temporary, InstalledMode);
            File.Move(temporary, destination, overwrite: true);
            ReconcileStore.SyncDirectory(directory);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        reporter.Info($"installed {destination}");
        return true;
    }

    private static SafeFileHandle OpenValidatedSource(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new AgentConfigException($"guard binary must not be a symlink: {path}");

        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AgentConfigException($"guard binary not found or unreadable: {path}: {e.Message}");
        }

        try
        {
            var metadata = PosixFile.StatHandle(handle);
            if (!metadata.IsRegular)
                throw new AgentConfigException($"guard binary must be a regular file: {path}");
            if (metadata.Size == 0)
                throw new AgentConfigException($"guard binary is empty: {path}");
            if (metadata.IsGroupOrOtherWritable)
                throw new AgentConfigException($"guard binary must not be group or world writable: {path}");
            if (Environment.IsPrivilegedProcess && metadata.Uid != 0)
                throw new AgentConfigException($"guard binary must be owned by root: {path}");

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string HashFile(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return HashHandle(handle);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string HashHandle(SafeFileHandle handle)
    {
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
}

/// <summary>
/// Writes the user units under <c>~/.config/systemd/user</c> and enables exactly one of the
/// daemon service or the timer, matching the configured mode.
/// </summary>
public static class SystemdUnitInstaller
{
    public static SystemdUnitPlan Plan(AgentConfig config, string? unitDirectory = null)
    {
        var templates = SystemdUnitTemplates.All();
        foreach (var template in templates)
            SystemdUnitTemplates.Validate(template);

        var daemonMode = string.Equals(config.Mode, "daemon", StringComparison.Ordinal);
        var enable = daemonMode ? SystemdUnitTemplates.DaemonUnit : SystemdUnitTemplates.ReconcileTimer;
        var disable = daemonMode ? SystemdUnitTemplates.ReconcileTimer : SystemdUnitTemplates.DaemonUnit;

        var directory = unitDirectory ?? DefaultUnitDirectory();
        var files = templates
            .Select(template =>
            {
                var path = Path.Combine(directory, template.Name);
                var changed = !File.Exists(path) || !string.Equals(File.ReadAllText(path), template.Content, StringComparison.Ordinal);
                return new PlannedUnitFile(template.Name, path, template.Content, changed);
            })
            .ToList();

        var commands = new List<IReadOnlyList<string>>();
        if (files.Any(file => file.Changed))
            commands.Add(["--user", "daemon-reload"]);
        commands.Add(["--user", "disable", "--now", disable]);
        commands.Add(["--user", "enable", "--now", enable]);

        return new SystemdUnitPlan(directory, enable, disable, files, commands);
    }

    /// <summary>Plans the root-owned guard service and timer. Bootstrap installs these as root.</summary>
    public static SystemdUnitPlan PlanGuard(string unitDirectory)
    {
        if (!Path.IsPathRooted(unitDirectory))
            throw new AgentConfigException("system unit directory must be an absolute path");

        var templates = SystemdUnitTemplates.AllGuard();
        foreach (var template in templates)
            SystemdUnitTemplates.ValidateGuard(template);

        var files = templates
            .Select(template =>
            {
                var path = Path.Combine(unitDirectory, template.Name);
                var changed = !File.Exists(path) || !string.Equals(File.ReadAllText(path), template.Content, StringComparison.Ordinal);
                return new PlannedUnitFile(template.Name, path, template.Content, changed);
            })
            .ToList();

        var commands = new List<IReadOnlyList<string>>();
        if (files.Any(file => file.Changed))
            commands.Add(["daemon-reload"]);
        commands.Add(["enable", "--now", SystemdUnitTemplates.GuardTimer]);
        return new SystemdUnitPlan(
            unitDirectory,
            SystemdUnitTemplates.GuardTimer,
            DisableUnit: "",
            files,
            commands,
            UserScope: false);
    }

    public static async Task ApplyAsync(
        SystemdUnitPlan plan,
        bool dryRun,
        Reporter reporter,
        IUnitCommandRunner? runner,
        CancellationToken cancellationToken)
    {
        foreach (var file in plan.Files)
        {
            if (!file.Changed)
            {
                reporter.Info($"{file.Path} is up to date");
                continue;
            }

            if (dryRun)
            {
                reporter.Info($"would write {file.Path}");
                continue;
            }

            WriteUnit(file.Path, file.Content);
            reporter.Info($"wrote {file.Path}");
        }

        foreach (var command in plan.Commands)
        {
            if (dryRun)
            {
                reporter.Info("would run systemctl " + string.Join(' ', command));
                continue;
            }

            var arguments = new[] { "systemctl" }.Concat(command).ToList();
            var exitCode = runner is null
                ? throw new AgentConfigException("no systemctl runner is available")
                : await runner.RunAsync("systemctl", command, cancellationToken);
            reporter.Detail($"{string.Join(' ', arguments)} exited {exitCode}");
            if (exitCode != 0)
                throw new ReconcileException(
                    "systemctl_failed",
                    $"systemctl {string.Join(' ', command)} exited {exitCode}",
                    ReconcileExitCode.Runtime);
        }
    }

    public static string DefaultUnitDirectory()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                throw new AgentConfigException("cannot resolve the user unit directory: HOME is unset");
            configHome = Path.Combine(home, ".config");
        }

        return Path.Combine(configHome, "systemd", "user");
    }

    private static void WriteUnit(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new AgentConfigException($"invalid unit path: {path}");
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("n") + ".tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode =
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            using (var stream = new FileStream(temporary, options))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            ReconcileStore.SyncDirectory(directory);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}

/// <summary>Runs systemctl through the existing process runner.</summary>
public sealed class ProcessUnitCommandRunner(Reporter reporter, bool verbose) : IUnitCommandRunner
{
    private readonly ProcessRunner _runner = new(reporter, verbose);

    public async Task<int> RunAsync(string file, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(file, args, new ProcessOptions(), cancellationToken);
        return result.ExitCode;
    }
}
