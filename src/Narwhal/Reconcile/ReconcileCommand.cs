using System.Text.Json;
using Narwhal.Cli;
using Narwhal.Podman;

namespace Narwhal.Reconcile;

/// <summary><c>narwhal reconcile</c>: one engine cycle, optionally printing the status document.</summary>
/// <remarks>
/// Status signing and publishing are deferred to the narwhald/status task. This command returns the
/// unsigned status document for inspection and never pushes it to the status repository.
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

            using var registry = OciRegistryClient.Create(config, reporter);
            var runner = new ProcessRunner(reporter, options.Verbose);
            var runtime = new PodmanRuntime(
                runner,
                reporter,
                imageAuth: new ImagePullAuth(config.ImageRegistry, config.ImageRegistryAuth));
            var engine = new ReconcileEngine(config, store, registry, runtime, new FileSecretResolver(), reporter);
            var outcome = await engine.RunOnceAsync(cancellationToken);

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
