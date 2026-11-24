using System.Net.Http;
using System.Text;
using System.Text.Json;
using Narwhal.Cli;
using Narwhal.Cloud;

namespace Narwhal.Reconcile;

/// <summary><c>narwhal status</c>: fetch, verify, and print the signed host status.</summary>
public static class StatusCommand
{
    public static async Task<int> RunAsync(CliOptions options, Reporter reporter, CancellationToken cancellationToken)
    {
        var configPath = options.Value("config") ?? AgentConfigLoader.DefaultPath;
        var config = AgentConfigLoader.Load(configPath);

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

        var repository = config.StatusRepository ?? StatusRepository.Derive(config.Project, config.Target);
        try
        {
            using var registry = OciRegistryClient.Create(config, reporter);
            var channel = new StatusArtifactChannel(
                repository,
                registry,
                config.StatusKeys,
                config.Expectation,
                signingKey: null,
                new StatusStore(config.StateDirectory),
                reporter);
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
                ["narwhald", status.Host.NarwhaldVersion],
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
