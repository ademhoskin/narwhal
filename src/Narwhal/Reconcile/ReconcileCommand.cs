using System.Text.Json;
using Narwhal.Cli;

namespace Narwhal.Reconcile;

/// <summary><c>narwhal reconcile</c>: one engine cycle, optionally printing the status document.</summary>
/// <remarks>
/// Publishing is best effort: a registry failure queues the signed status and never changes the
/// cycle's exit code.
/// </remarks>
public static class ReconcileCommand
{
    public static async Task<int> RunAsync(CliOptions options, Reporter reporter, CancellationToken cancellationToken)
    {
        var configPath = options.Value("config") ?? AgentConfigLoader.DefaultPath;

        AgentConfig config;
        try
        {
            config = AgentConfigLoader.Load(configPath);
            BootPins.Validate(config.TrustDirectory);
        }
        catch (AgentConfigException e)
        {
            return ConfigFailure(options, reporter, e.Message);
        }

        var store = new ReconcileStore(config.StateDirectory);

        try
        {
            using var held = await store.TryAcquireLockAsync(config.LockTimeoutSeconds, cancellationToken);
            if (held is null)
            {
                reporter.Info("another reconcile is running");
                if (options.Json)
                    WriteJson(BusyStatus(config, store));
                return (int)ReconcileExitCode.Success;
            }

            await RunAgentGuardAsync(config, reporter, cancellationToken);
            var outcome = await RunEngineAsync(config, store, reporter, options.Verbose, cancellationToken);

            if (options.Json)
                WriteJson(outcome.Status);
            return (int)outcome.ExitCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentConfigException e)
        {
            return ConfigFailure(options, reporter, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            reporter.Error(e.Message);
            if (options.Json)
                WriteError(e.Message, (int)ReconcileExitCode.Config);
            return (int)ReconcileExitCode.Config;
        }
    }

    internal static async Task RunAgentGuardAsync(
        AgentConfig config,
        Reporter reporter,
        CancellationToken cancellationToken)
    {
        try
        {
            var guard = new AgentUpgrader(config.StateDirectory, source: null, reporter);
            await guard.PrepareStartAsync(cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or AgentConfigException)
        {
            reporter.Warn($"agent guard could not run: {e.Message}");
        }
    }

    /// <summary>Builds the engine and runs one cycle. The caller owns the lock.</summary>
    internal static async Task<ReconcileOutcome> RunEngineAsync(
        AgentConfig config,
        ReconcileStore store,
        Reporter reporter,
        bool verbose,
        CancellationToken cancellationToken)
    {
        using var registry = OciRegistryClient.Create(config, reporter, verbose);
        var runner = new ProcessRunner(reporter, verbose);
        var runtime = new PodmanRuntime(
            runner,
            reporter,
            imageAuth: new ImagePullAuth(config.ImageRegistry, config.ImageRegistryAuth));
        var statusKey = LoadStatusKey(config, reporter);
        var statusRepository = config.StatusRepository ??
                               StatusRepository.Derive(config.Project, config.Target);
        IStatusChannel statusChannel = new StatusArtifactChannel(
            statusRepository,
            registry,
            config.StatusKeys,
            config.Expectation,
            statusKey,
            new StatusStore(config.StateDirectory),
            reporter);
        var upgrader = new AgentUpgrader(
            config.StateDirectory,
            new RegistryAgentImageSource(registry),
            reporter);
        var engine = new ReconcileEngine(
            config,
            store,
            registry,
            runtime,
            new FileSecretResolver(),
            reporter,
            statusChannel: statusChannel,
            statusKeyId: statusKey?.KeyId,
            agentUpgrader: upgrader);
        return await engine.RunOnceAsync(cancellationToken);
    }

    internal static StatusSigningKey? LoadStatusKey(AgentConfig config, Reporter reporter)
    {
        if (config.StatusKeyPath is null)
        {
            reporter.Warn("status_key is not configured; signed status will not be published");
            return null;
        }

        StatusSigningKey key;
        try
        {
            key = StatusSigningKey.Load(config.StatusKeyPath);
        }
        catch (AgentConfigException e)
        {
            reporter.Warn($"{e.Message}; signed status will not be published");
            return null;
        }

        if (!config.StatusKeys.IsEmpty && !config.StatusKeys.TryFind(key.KeyId, out _))
            reporter.Warn($"status key {key.KeyId} is not registered in status_keys");
        return key;
    }

    private static ReconcileStatusDocument BusyStatus(AgentConfig config, ReconcileStore store)
    {
        ReconcileState state;
        try
        {
            state = store.LoadState();
        }
        catch (AgentConfigException)
        {
            state = new ReconcileState();
        }

        return new ReconcileStatusDocument
        {
            Project = config.Project,
            Target = new StatusTarget
            {
                Provider = config.Target.Provider,
                Region = config.Target.Region,
                Arch = config.Target.Arch,
                Selector = new Dictionary<string, string>(config.Target.Selector, StringComparer.Ordinal),
            },
            Epoch = state.StatusEpoch,
            StatusSeq = state.StatusSeq,
            Updated = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            Host = new StatusHost
            {
                NarwhaldVersion = new AgentUpgrader(config.StateDirectory, source: null, new Reporter(false, false))
                    .InstalledVersion ?? App.Version,
            },
            Phase = state.Phase,
            HighestGeneration = state.HighestGeneration,
            BlockedGeneration = state.BlockedGeneration,
            Active = state.ActiveGeneration is { } generation && state.ActivePayloadDigest is { } digest
                ? new StatusActive { Generation = generation, PayloadDigest = digest }
                : null,
            LastError = state.LastError,
        };
    }

    private static int ConfigFailure(CliOptions options, Reporter reporter, string message)
    {
        reporter.Error(message);
        if (options.Json)
            WriteError(message, (int)ReconcileExitCode.Config);
        return (int)ReconcileExitCode.Config;
    }

    private static void WriteJson(ReconcileStatusDocument status) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(status, ReconcileJsonContext.Compact.ReconcileStatusDocument));

    private static void WriteError(string message, int exitCode)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", false);
            writer.WriteString("error", message.Replace("error: ", "", StringComparison.Ordinal));
            writer.WriteNumber("exit_code", exitCode);
            writer.WriteEndObject();
        }

        Console.Out.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
