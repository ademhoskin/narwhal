using Narwhal.Cli;

namespace Narwhal.Reconcile;

/// <summary>The result of one daemon cycle, reduced to what scheduling needs.</summary>
public sealed record DaemonCycleResult(
    ReconcileExitCode ExitCode,
    string Phase,
    bool RestartRequested = false,
    bool Busy = false);

/// <summary>Poll interval, jitter, and bounded exponential backoff for the daemon loop.</summary>
public static class DaemonBackoff
{
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Maximum = TimeSpan.FromMinutes(5);
    public const double JitterFraction = 0.2;

    /// <summary>Transport, registry, and authentication failures back off; everything else polls.</summary>
    public static bool IsTransportFailure(DaemonCycleResult result) =>
        result.Phase is ReconcilePhases.RegistryUnavailable or ReconcilePhases.AuthFailed;

    /// <param name="jitterSample">A value in [0,1] from the caller's random source.</param>
    public static TimeSpan NextDelay(
        DaemonCycleResult result,
        int consecutiveTransportFailures,
        int pollSeconds,
        double jitterSample)
    {
        if (jitterSample is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(jitterSample), "expected a sample in [0,1]");

        var backoff = IsTransportFailure(result);
        var nominal = backoff
            ? Exponential(consecutiveTransportFailures)
            : TimeSpan.FromSeconds(pollSeconds);
        var factor = 1 + ((jitterSample * 2 - 1) * JitterFraction);
        var seconds = nominal.TotalSeconds * factor;
        if (backoff)
            seconds = Math.Min(seconds, Maximum.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    private static TimeSpan Exponential(int consecutiveFailures)
    {
        var exponent = Math.Min(Math.Max(consecutiveFailures, 1), 10);
        var seconds = Minimum.TotalSeconds * Math.Pow(2, exponent - 1);
        return seconds >= Maximum.TotalSeconds ? Maximum : TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>
/// The narwhald poll loop. The lock is held for exactly one cycle and released before the
/// interval, so a one-shot invocation can run between polls.
/// </summary>
public sealed class DaemonRunner
{
    private readonly AgentConfig _config;
    private readonly ReconcileStore _store;
    private readonly Func<CancellationToken, Task<DaemonCycleResult>> _cycle;
    private readonly Reporter _reporter;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _jitter;

    public DaemonRunner(
        AgentConfig config,
        ReconcileStore store,
        Func<CancellationToken, Task<DaemonCycleResult>> cycle,
        Reporter reporter,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? jitter = null)
    {
        _config = config;
        _store = store;
        _cycle = cycle;
        _reporter = reporter;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _jitter = jitter ?? Random.Shared.NextDouble;
    }

    /// <returns>0 for a graceful stop or restart request.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var transportFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            DaemonCycleResult result;
            try
            {
                using var held = await _store.TryAcquireLockAsync(_config.LockTimeoutSeconds, cancellationToken);
                if (held is null)
                {
                    _reporter.Info("another reconcile is running");
                    result = new DaemonCycleResult(ReconcileExitCode.Success, ReconcilePhases.Idle, Busy: true);
                }
                else
                {
                    result = await _cycle(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e) when (e is AgentConfigException or IOException or UnauthorizedAccessException)
            {
                _reporter.Error(e.Message);
                result = new DaemonCycleResult(ReconcileExitCode.Config, ReconcilePhases.Failed);
            }

            if (result.RestartRequested)
            {
                _reporter.Info("restarting to run the staged agent");
                return 0;
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            if (DaemonBackoff.IsTransportFailure(result))
                transportFailures++;
            else
                transportFailures = 0;

            var delay = DaemonBackoff.NextDelay(
                result,
                transportFailures,
                _config.PollSeconds,
                _jitter());
            _reporter.Detail($"next reconcile in {delay.TotalSeconds:0.0}s");

            try
            {
                await _delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        _reporter.Info("narwhald stopped");
        return 0;
    }
}
