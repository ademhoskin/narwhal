using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class DaemonRunnerTests
{
    [Fact]
    public async Task RunsCyclesUntilCancelledAndStopsCleanly()
    {
        var directory = TestDirectory();
        var config = ReconcileTestData.Config(directory);
        var store = new ReconcileStore(directory);
        using var cancellation = new CancellationTokenSource();
        var cycles = 0;

        var runner = new DaemonRunner(
            config,
            store,
            _ =>
            {
                cycles++;
                return Task.FromResult(new DaemonCycleResult(ReconcileExitCode.Success, ReconcilePhases.Idle));
            },
            ReconcileTestData.Reporter(),
            (_, token) =>
            {
                if (cycles >= 3)
                    cancellation.Cancel();
                return Task.CompletedTask;
            },
            () => 0.5);

        var exit = await runner.RunAsync(cancellation.Token);

        Assert.Equal(0, exit);
        Assert.Equal(3, cycles);
    }

    [Fact]
    public async Task HoldsTheLockOnlyDuringACycle()
    {
        var directory = TestDirectory();
        var config = ReconcileTestData.Config(directory);
        var store = new ReconcileStore(directory);
        using var cancellation = new CancellationTokenSource();
        var cycleSawLocked = false;
        var delaySawFree = false;
        var cycles = 0;

        var runner = new DaemonRunner(
            config,
            store,
            async token =>
            {
                cycles++;
                using var probe = await store.TryAcquireLockAsync(0, token);
                cycleSawLocked = probe is null;
                return new DaemonCycleResult(ReconcileExitCode.Success, ReconcilePhases.Idle);
            },
            ReconcileTestData.Reporter(),
            async (_, token) =>
            {
                using var probe = await store.TryAcquireLockAsync(0, token);
                delaySawFree = probe is not null;
                cancellation.Cancel();
            },
            () => 0.5);

        await runner.RunAsync(cancellation.Token);

        Assert.Equal(1, cycles);
        Assert.True(cycleSawLocked);
        Assert.True(delaySawFree);
    }

    [Fact]
    public async Task AOneShotHoldingTheLockSkipsTheCycle()
    {
        var directory = TestDirectory();
        var config = ReconcileTestData.Config(directory);
        var store = new ReconcileStore(directory);
        using var held = await store.TryAcquireLockAsync(0, CancellationToken.None);
        Assert.NotNull(held);

        using var cancellation = new CancellationTokenSource();
        var cycles = 0;
        var runner = new DaemonRunner(
            config,
            store,
            _ =>
            {
                cycles++;
                return Task.FromResult(new DaemonCycleResult(ReconcileExitCode.Success, ReconcilePhases.Idle));
            },
            ReconcileTestData.Reporter(),
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            },
            () => 0.5);

        var exit = await runner.RunAsync(cancellation.Token);

        Assert.Equal(0, exit);
        Assert.Equal(0, cycles);
    }

    [Fact]
    public async Task AStagedUpgradeRequestsARestartInsteadOfSleeping()
    {
        var directory = TestDirectory();
        var config = ReconcileTestData.Config(directory);
        var store = new ReconcileStore(directory);
        var cycles = 0;
        var delayed = false;

        var runner = new DaemonRunner(
            config,
            store,
            _ =>
            {
                cycles++;
                return Task.FromResult(new DaemonCycleResult(
                    ReconcileExitCode.Success,
                    ReconcilePhases.Idle,
                    RestartRequested: true));
            },
            ReconcileTestData.Reporter(),
            (_, _) =>
            {
                delayed = true;
                return Task.CompletedTask;
            },
            () => 0.5);

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(1, cycles);
        Assert.False(delayed);
    }

    [Fact]
    public async Task StopsGracefullyWhenCancelledDuringACycle()
    {
        var directory = TestDirectory();
        var config = ReconcileTestData.Config(directory);
        var store = new ReconcileStore(directory);
        using var cancellation = new CancellationTokenSource();
        var delayCalled = false;

        var runner = new DaemonRunner(
            config,
            store,
            _ =>
            {
                cancellation.Cancel();
                return Task.FromResult(new DaemonCycleResult(ReconcileExitCode.Success, ReconcilePhases.Idle));
            },
            ReconcileTestData.Reporter(),
            (_, _) =>
            {
                delayCalled = true;
                return Task.CompletedTask;
            },
            () => 0.5);

        var exit = await runner.RunAsync(cancellation.Token);

        Assert.Equal(0, exit);
        Assert.False(delayCalled);
    }

    [Theory]
    [InlineData(30, 0.0, 24.0)]
    [InlineData(30, 0.5, 30.0)]
    [InlineData(30, 1.0, 36.0)]
    public void AppliesJitterToThePollInterval(int pollSeconds, double sample, double expectedSeconds)
    {
        var delay = DaemonBackoff.NextDelay(
            new DaemonCycleResult(ReconcileExitCode.Success, ReconcilePhases.Idle),
            0,
            pollSeconds,
            sample);

        Assert.Equal(expectedSeconds, delay.TotalSeconds, 3);
    }

    [Theory]
    [InlineData(1, 30.0)]
    [InlineData(2, 60.0)]
    [InlineData(3, 120.0)]
    [InlineData(4, 240.0)]
    [InlineData(10, 300.0)]
    [InlineData(50, 300.0)]
    public void BacksOffExponentiallyToFiveMinutes(int failures, double expectedSeconds)
    {
        var delay = DaemonBackoff.NextDelay(
            new DaemonCycleResult(ReconcileExitCode.Runtime, ReconcilePhases.RegistryUnavailable),
            failures,
            30,
            0.5);

        Assert.Equal(expectedSeconds, delay.TotalSeconds, 3);
    }

    [Fact]
    public void BackoffJitterStaysWithinTheFiveMinuteBound()
    {
        var low = DaemonBackoff.NextDelay(
            new DaemonCycleResult(ReconcileExitCode.Runtime, ReconcilePhases.AuthFailed),
            10,
            30,
            0.0);
        var high = DaemonBackoff.NextDelay(
            new DaemonCycleResult(ReconcileExitCode.Runtime, ReconcilePhases.AuthFailed),
            10,
            30,
            1.0);

        Assert.True(low > TimeSpan.Zero);
        Assert.True(high <= DaemonBackoff.Maximum);
    }

    [Theory]
    [InlineData(ReconcilePhases.Degraded, ReconcileExitCode.Runtime)]
    [InlineData(ReconcilePhases.Failed, ReconcileExitCode.Verification)]
    [InlineData(ReconcilePhases.Idle, ReconcileExitCode.Success)]
    public void VerificationAndHealthFailuresKeepThePollInterval(string phase, ReconcileExitCode exitCode)
    {
        var delay = DaemonBackoff.NextDelay(
            new DaemonCycleResult(exitCode, phase),
            7,
            30,
            0.5);

        Assert.Equal(30, delay.TotalSeconds, 3);
    }

    [Fact]
    public void RejectsAJitterSampleOutsideTheUnitInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DaemonBackoff.NextDelay(
            new DaemonCycleResult(ReconcileExitCode.Success, ReconcilePhases.Idle),
            0,
            30,
            1.5));
    }

    internal static string TestDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-daemon", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
