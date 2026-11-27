using System.Text;
using System.Text.Json;
using Narwhal.Cli;
using Narwhal.Cloud;

namespace Narwhal.Reconcile;

/// <summary><c>narwhal status</c>: fetch, verify, and print the signed host status.</summary>
public static class StatusCommand
{
    public static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(CliOptions options, Reporter reporter, CancellationToken cancellationToken)
    {
        return options.Flag("watch")
            ? await WatchAsync(options, reporter, cancellationToken)
            : await SingleAsync(options, reporter, cancellationToken);
    }

    private static async Task<int> SingleAsync(CliOptions options, Reporter reporter, CancellationToken cancellationToken)
    {
        var config = LoadConfig(options);
        var repository = ValidatedRepository(config, options);

        try
        {
            using var registry = OciRegistryClient.Create(config, reporter);
            var channel = CreateChannel(config, registry, reporter);
            var verified = await channel.FetchLatestAsync(cancellationToken);
            Print(reporter, verified.Document);
            return ExitCode(verified.Document.Phase);
        }
        catch (CloudArtifactException e)
        {
            throw new ReconcileException(
                e.Code,
                $"status verification failed: {e.Message}",
                ReconcileExitCode.Verification);
        }
        catch (RegistryException e)
        {
            throw new ReconcileException(
                "registry_unavailable",
                $"cannot read status from {repository}: {e.Message}",
                ReconcileExitCode.Runtime);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException ||
                                  (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ReconcileException(
                "registry_unavailable",
                $"cannot read status from {repository}: {e.Message}",
                ReconcileExitCode.Runtime);
        }
    }

    /// <summary>
    /// Polls the verified status every five seconds. Verification failures are terminal; a
    /// registry outage keeps watching because it is transient. Ctrl-C returns the last exit code.
    /// </summary>
    private static async Task<int> WatchAsync(CliOptions options, Reporter reporter, CancellationToken cancellationToken)
    {
        var config = LoadConfig(options);
        var repository = ValidatedRepository(config, options);

        using var registry = OciRegistryClient.Create(config, reporter);
        var channel = CreateChannel(config, registry, reporter);
        var watch = new StatusWatch(
            async token =>
            {
                try
                {
                    var verified = await channel.FetchLatestAsync(token);
                    Print(reporter, verified.Document);
                    var exitCode = ExitCode(verified.Document.Phase);
                    return new StatusWatchPoll(
                        exitCode,
                        verified.Document.Phase is ReconcilePhases.Failed or ReconcilePhases.ReplayRejected);
                }
                catch (CloudArtifactException e)
                {
                    throw new ReconcileException(
                        e.Code,
                        $"status verification failed: {e.Message}",
                        ReconcileExitCode.Verification);
                }
                catch (RegistryException e)
                {
                    reporter.Warn($"cannot read status from {repository}: {e.Message}");
                    return new StatusWatchPoll((int)ReconcileExitCode.Runtime, false);
                }
                catch (Exception e) when (e is HttpRequestException or JsonException ||
                                          (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    reporter.Warn($"cannot read status from {repository}: {e.Message}");
                    return new StatusWatchPoll((int)ReconcileExitCode.Runtime, false);
                }
            },
            (duration, token) => Task.Delay(duration, token),
            WatchInterval,
            reporter);

        return await watch.RunAsync(cancellationToken);
    }

    private static AgentConfig LoadConfig(CliOptions options)
    {
        var configPath = options.Value("config") ?? AgentConfigLoader.DefaultPath;
        return AgentConfigLoader.Load(configPath);
    }

    private static string ValidatedRepository(AgentConfig config, CliOptions options)
    {
        var targetId = StatusRepository.TargetId(config.Target);
        if (options.Value("target") is { } requestedTarget &&
            !string.Equals(requestedTarget, targetId, StringComparison.Ordinal))
        {
            throw new ReconcileException(
                "target_mismatch",
                $"target '{requestedTarget}' does not match the configured target '{targetId}'",
                ReconcileExitCode.Verification);
        }

        if (config.StatusKeys.IsEmpty)
        {
            throw new ReconcileException(
                CloudArtifactErrorCodes.TrustSetInvalid,
                "no status public key is registered for this target",
                ReconcileExitCode.Verification);
        }

        return config.StatusRepository ?? StatusRepository.Derive(config.Project, config.Target);
    }

    private static StatusArtifactChannel CreateChannel(
        AgentConfig config,
        IStatusRegistry registry,
        Reporter reporter) =>
        new(
            config.StatusRepository ?? StatusRepository.Derive(config.Project, config.Target),
            registry,
            config.StatusKeys,
            config.Expectation,
            signingKey: null,
            new StatusStore(config.StateDirectory),
            reporter);

    /// <summary>Maps a verified status phase to the documented exit code.</summary>
    public static int ExitCode(string phase) => phase switch
    {
        ReconcilePhases.Idle => 0,
        ReconcilePhases.Degraded => 3,
        ReconcilePhases.Failed or ReconcilePhases.ReplayRejected => 5,
        ReconcilePhases.AuthFailed or ReconcilePhases.RegistryUnavailable => 3,
        _ => 3,
    };

    private static void Print(Reporter reporter, ReconcileStatusDocument status)
    {
        if (reporter.Json)
        {
            reporter.Data(Encoding.UTF8.GetString(StatusDocumentCodec.Encode(status)));
            return;
        }

        reporter.Table(
            ["field", "value"],
            [
                ["project", status.Project],
                ["target", $"{status.Target.Provider}/{status.Target.Region}/{status.Target.Arch}"],
                ["phase", status.Phase],
                ["updated", status.Updated],
                ["status_seq", status.StatusSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                ["epoch", status.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                ["host", status.Host.Id ?? "-"],
                ["boot_id", status.Host.BootId ?? "-"],
                ["narwhal", status.Host.NarwhaldVersion],
                ["status_key_id", status.Host.StatusKeyId ?? "-"],
                ["highest_generation", status.HighestGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                ["blocked_generation", status.BlockedGeneration?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"],
                ["observed_generation", status.Observed?.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"],
                ["active_generation", status.Active?.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"],
                ["last_error", status.LastError is null ? "-" : $"{status.LastError.Code}: {status.LastError.Message}"],
            ]);

        if (status.Services.Count > 0)
        {
            reporter.Data(string.Empty);
            reporter.Table(
                ["service", "container", "state", "health", "restarts", "image"],
                status.Services
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => (IReadOnlyList<string>)
                    [
                        pair.Key,
                        pair.Value.Container,
                        pair.Value.State,
                        pair.Value.Health ?? "-",
                        pair.Value.Restarts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        pair.Value.ImageDigest,
                    ])
                    .ToList());
        }

        if (status.LogsTail.Count > 0)
        {
            reporter.Data(string.Empty);
            foreach (var line in status.LogsTail)
                reporter.Data($"{line.At} {line.Service}: {line.Line}");
        }
    }
}

internal sealed record StatusWatchPoll(int ExitCode, bool Terminal);

/// <summary>The watch loop, separated from the registry so its stop and exit behavior is testable.</summary>
internal sealed class StatusWatch(
    Func<CancellationToken, Task<StatusWatchPoll>> poll,
    Func<TimeSpan, CancellationToken, Task> delay,
    TimeSpan interval,
    Reporter reporter)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var lastExitCode = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            StatusWatchPoll result;
            try
            {
                result = await poll(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            lastExitCode = result.ExitCode;
            if (result.Terminal)
                return lastExitCode;

            try
            {
                await delay(interval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        reporter.Info("status watch stopped");
        return lastExitCode;
    }
}
