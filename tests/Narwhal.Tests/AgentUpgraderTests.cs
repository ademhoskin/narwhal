using System.Security.Cryptography;
using System.Text;
using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class AgentUpgraderTests
{
    private const string Repository = "registry.example.com/shop/narwhal";
    private static readonly byte[] OldBinary = Encoding.UTF8.GetBytes("agent 0.3.0");
    private static readonly byte[] NewBinary = Encoding.UTF8.GetBytes("agent 0.4.0");

    [Fact]
    public async Task StagesANewVersionUnderAVersionedDirectory()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var source = new FakeSource(NewBinary);
        var upgrader = CreateUpgrader(state, source);
        var spec = Spec("0.4.0", NewBinary);

        var result = await upgrader.EvaluateAsync(spec, new ReconcileState(), CancellationToken.None);

        Assert.Equal(AgentUpgradeAction.Staged, result.Action);
        Assert.Equal("0.4.0", CurrentTarget(state));
        Assert.True(File.Exists(Path.Combine(state, "agent", "0.4.0", "narwhal")));
        Assert.Equal(NewBinary, File.ReadAllBytes(Path.Combine(state, "agent", "0.4.0", "narwhal")));
        Assert.True(File.Exists(Path.Combine(state, "agent", "pending")));
        Assert.False(File.Exists(Path.Combine(state, "agent", "boot-ok")));
    }

    [Fact]
    public async Task VerifiesTheBinaryHashBeforeSwapping()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var source = new FakeSource(Encoding.UTF8.GetBytes("tampered"));
        var upgrader = CreateUpgrader(state, source);
        var spec = Spec("0.4.0", NewBinary);

        var result = await upgrader.EvaluateAsync(spec, new ReconcileState(), CancellationToken.None);

        Assert.Equal(AgentUpgradeAction.Failed, result.Action);
        Assert.True(result.DigestBlocked);
        Assert.Equal("0.3.0", CurrentTarget(state));
        var reverted = ReadReverted(state);
        Assert.NotNull(reverted);
        Assert.Equal(spec.BinarySha256, reverted!.BinarySha256);
        Assert.False(File.Exists(Path.Combine(state, "agent", "pending")));
        Assert.False(File.Exists(Path.Combine(state, "agent", "0.4.0", "narwhal")));
    }

    [Fact]
    public async Task ABlockedDigestIsNeverStagedAgain()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var source = new FakeSource(NewBinary);
        var upgrader = CreateUpgrader(state, source);
        var spec = Spec("0.4.0", NewBinary);
        var reconcileState = new ReconcileState { BlockedAgentDigest = spec.BinarySha256 };

        var result = await upgrader.EvaluateAsync(spec, reconcileState, CancellationToken.None);

        Assert.Equal(AgentUpgradeAction.Blocked, result.Action);
        Assert.Equal(0, source.Calls);
        Assert.Equal("0.3.0", CurrentTarget(state));
    }

    [Fact]
    public async Task RevertsAfterMoreThanThreeStartFailures()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var upgrader = CreateUpgrader(state, new FakeSource(NewBinary));
        var spec = Spec("0.4.0", NewBinary);
        await upgrader.EvaluateAsync(spec, new ReconcileState(), CancellationToken.None);

        for (var attempt = 1; attempt <= AgentUpgrader.MaximumStartAttempts; attempt++)
            Assert.Null(await upgrader.PrepareStartAsync(CancellationToken.None));

        var reverted = await upgrader.PrepareStartAsync(CancellationToken.None);

        Assert.NotNull(reverted);
        Assert.Equal(spec.BinarySha256, reverted!.BinarySha256);
        Assert.Equal("0.3.0", CurrentTarget(state));
        Assert.False(File.Exists(Path.Combine(state, "agent", "pending")));
        Assert.True(File.Exists(Path.Combine(state, "agent", "reverted")));
    }

    [Fact]
    public async Task RevertsWhenBootOkDoesNotArriveWithinTenMinutes()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 18, 0, 0, TimeSpan.Zero));
        var upgrader = new AgentUpgrader(state, new FakeSource(NewBinary), ReconcileTestData.Reporter(), clock);
        var spec = Spec("0.4.0", NewBinary);
        await upgrader.EvaluateAsync(spec, new ReconcileState(), CancellationToken.None);

        clock.Now = clock.Now.AddMinutes(11);
        var reverted = await upgrader.PrepareStartAsync(CancellationToken.None);

        Assert.NotNull(reverted);
        Assert.Equal("0.3.0", CurrentTarget(state));
        Assert.Equal(spec.BinarySha256, reverted!.BinarySha256);
    }

    [Fact]
    public async Task RevertsAnExpiredUpgradeWhileTheProcessKeepsRunning()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 18, 0, 0, TimeSpan.Zero));
        var upgrader = new AgentUpgrader(state, new FakeSource(NewBinary), ReconcileTestData.Reporter(), clock);
        await upgrader.EvaluateAsync(Spec("0.4.0", NewBinary), new ReconcileState(), CancellationToken.None);

        Assert.Null(await upgrader.RevertExpiredAsync(CancellationToken.None));
        clock.Now = clock.Now.AddMinutes(10);
        var reverted = await upgrader.RevertExpiredAsync(CancellationToken.None);

        Assert.NotNull(reverted);
        Assert.Equal("0.3.0", CurrentTarget(state));
        Assert.False(File.Exists(Path.Combine(state, "agent", "pending")));
    }

    [Fact]
    public async Task BootOkClearsThePendingMarkerAndStopsTheRevert()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var upgrader = CreateUpgrader(state, new FakeSource(NewBinary));
        var spec = Spec("0.4.0", NewBinary);
        await upgrader.EvaluateAsync(spec, new ReconcileState(), CancellationToken.None);

        Assert.Null(await upgrader.PrepareStartAsync(CancellationToken.None));
        await upgrader.MarkHealthyAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(state, "agent", "boot-ok")));
        Assert.False(File.Exists(Path.Combine(state, "agent", "pending")));
        Assert.Equal("0.4.0", CurrentTarget(state));
        Assert.Null(await upgrader.PrepareStartAsync(CancellationToken.None));
        Assert.Equal("0.4.0", CurrentTarget(state));
    }

    [Fact]
    public async Task MarkHealthyForThePendingVersionClearsIt()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var upgrader = CreateUpgrader(state, new FakeSource(NewBinary));
        var spec = Spec("0.4.0", NewBinary);
        await upgrader.EvaluateAsync(spec, new ReconcileState(), CancellationToken.None);

        await upgrader.MarkHealthyAsync(CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(state, "agent", "pending")));
        Assert.Equal("0.4.0", CurrentTarget(state));
    }

    [Fact]
    public async Task RefusesADowngrade()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.5.0", NewBinary);
        var source = new FakeSource(OldBinary);
        var upgrader = CreateUpgrader(state, source);
        var spec = Spec("0.4.0", OldBinary);

        var result = await upgrader.EvaluateAsync(spec, new ReconcileState(), CancellationToken.None);

        Assert.Equal(AgentUpgradeAction.DowngradeRefused, result.Action);
        Assert.Equal(0, source.Calls);
        Assert.Equal("0.5.0", CurrentTarget(state));
    }

    [Fact]
    public async Task CorruptMarkerIsQuarantinedAndIgnored()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var pending = Path.Combine(state, "agent", "pending");
        File.WriteAllText(pending, "{not json");
        var upgrader = CreateUpgrader(state, new FakeSource(NewBinary));

        Assert.Null(await upgrader.PrepareStartAsync(CancellationToken.None));

        Assert.False(File.Exists(pending));
        Assert.Single(Directory.GetFiles(Path.Combine(state, "agent"), "pending.corrupt.*"));
        Assert.Equal("0.3.0", CurrentTarget(state));
    }

    [Fact]
    public async Task RefusesToStageIntoASymlinkedVersionDirectory()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var agent = Path.Combine(state, "agent");
        Directory.CreateDirectory(Path.Combine(agent, "elsewhere"));
        File.CreateSymbolicLink(Path.Combine(agent, "0.4.0"), "elsewhere");
        var upgrader = CreateUpgrader(state, new FakeSource(NewBinary));

        await Assert.ThrowsAsync<AgentConfigException>(() =>
            upgrader.EvaluateAsync(Spec("0.4.0", NewBinary), new ReconcileState(), CancellationToken.None));

        Assert.Equal("0.3.0", CurrentTarget(state));
        Assert.False(File.Exists(Path.Combine(agent, "pending")));
        Assert.False(File.Exists(Path.Combine(agent, "elsewhere", "narwhal")));
    }

    [Fact]
    public async Task RefusesToStageOverASymlinkedBinary()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var agent = Path.Combine(state, "agent");
        Directory.CreateDirectory(Path.Combine(agent, "0.4.0"));
        File.CreateSymbolicLink(Path.Combine(agent, "0.4.0", "narwhal"), Path.Combine(agent, "0.3.0", "narwhal"));
        var upgrader = CreateUpgrader(state, new FakeSource(NewBinary));

        await Assert.ThrowsAsync<AgentConfigException>(() =>
            upgrader.EvaluateAsync(Spec("0.4.0", NewBinary), new ReconcileState(), CancellationToken.None));

        Assert.Equal("0.3.0", CurrentTarget(state));
        Assert.False(File.Exists(Path.Combine(agent, "pending")));
    }

    [Fact]
    public async Task RefusesToRevertToAPreviousBinaryThatFailsItsHash()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 18, 0, 0, TimeSpan.Zero));
        var upgrader = new AgentUpgrader(state, new FakeSource(NewBinary), ReconcileTestData.Reporter(), clock);
        await upgrader.EvaluateAsync(Spec("0.4.0", NewBinary), new ReconcileState(), CancellationToken.None);

        var pendingPath = Path.Combine(state, "agent", "pending");
        var previousSha = Convert.ToHexStringLower(SHA256.HashData(OldBinary));
        var marker = File.ReadAllText(pendingPath);
        Assert.Contains(previousSha, marker, StringComparison.Ordinal);
        File.WriteAllText(pendingPath, marker.Replace(previousSha, new string('0', 64), StringComparison.Ordinal));
        clock.Now = clock.Now.AddMinutes(11);

        var reverted = await upgrader.PrepareStartAsync(CancellationToken.None);

        Assert.NotNull(reverted);
        Assert.Equal("0.4.0", CurrentTarget(state));
        Assert.True(File.Exists(Path.Combine(state, "agent", "reverted")));
    }

    [Fact]
    public async Task RefusesToReuseAVersionForADifferentBinary()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.4.0", OldBinary);
        var source = new FakeSource(NewBinary);
        var upgrader = CreateUpgrader(state, source);
        var spec = Spec("0.4.0", NewBinary);

        var result = await upgrader.EvaluateAsync(spec, new ReconcileState(), CancellationToken.None);

        Assert.Equal(AgentUpgradeAction.Failed, result.Action);
        Assert.True(result.DigestBlocked);
        Assert.Equal("0.4.0", CurrentTarget(state));
        Assert.Equal(OldBinary, File.ReadAllBytes(Path.Combine(state, "agent", "0.4.0", "narwhal")));
    }

    [Fact]
    public async Task RejectsAnUnsafeVersionWithoutWritingOutsideTheAgentDirectory()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var source = new FakeSource(NewBinary);
        var upgrader = CreateUpgrader(state, source);
        var spec = Spec("../escape", NewBinary);

        var result = await upgrader.EvaluateAsync(spec, new ReconcileState(), CancellationToken.None);

        Assert.Equal(AgentUpgradeAction.Failed, result.Action);
        Assert.True(result.DigestBlocked);
        Assert.Equal(0, source.Calls);
        Assert.False(Directory.Exists(Path.Combine(state, "escape")));
    }

    [Fact]
    public async Task AnAlreadyInstalledBinaryIsANoOp()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.4.0", NewBinary);
        var source = new FakeSource(NewBinary);
        var upgrader = CreateUpgrader(state, source);
        var spec = Spec("0.4.0", NewBinary);

        var result = await upgrader.EvaluateAsync(spec, new ReconcileState(), CancellationToken.None);

        Assert.Equal(AgentUpgradeAction.UpToDate, result.Action);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task TheEngineStagesBeforeApplyingServicesAndRequestsARestart()
    {
        var state = ReconcileTestData.CreateStateDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var binary = NewBinary;
        var spec = Spec("0.4.0", binary);
        var artifact = AgentArtifact(spec);
        var source = new FakeSource(binary);

        var runtime = Runtime();
        var first = await RunEngineAsync(state, artifact, source, runtime);

        Assert.True(first.RestartRequested);
        Assert.Equal(ReconcileExitCode.Success, first.ExitCode);
        Assert.Empty(runtime.Created);
        Assert.Equal("0.4.0", CurrentTarget(state));
        Assert.True(File.Exists(Path.Combine(state, "agent", "pending")));
        Assert.False(File.Exists(Path.Combine(state, "agent", "boot-ok")));

        var second = await RunEngineAsync(state, artifact, source, runtime);

        Assert.False(second.RestartRequested);
        Assert.Equal(ReconcileExitCode.Success, second.ExitCode);
        Assert.Single(runtime.Created);
        Assert.True(File.Exists(Path.Combine(state, "agent", "boot-ok")));
        Assert.False(File.Exists(Path.Combine(state, "agent", "pending")));
    }

    [Fact]
    public async Task TheEngineDoesNotRetryABlockedDigest()
    {
        var state = ReconcileTestData.CreateStateDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var spec = Spec("0.4.0", NewBinary);
        var artifact = AgentArtifact(spec);
        var source = new FakeSource(NewBinary);
        new ReconcileStore(state).SaveState(new ReconcileState
        {
            BlockedAgentDigest = spec.BinarySha256,
            StatusEpoch = 1,
        });

        var runtime = Runtime();
        var outcome = await RunEngineAsync(state, artifact, source, runtime);

        Assert.Equal(ReconcileExitCode.Success, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Degraded, outcome.Status.Phase);
        Assert.Equal(AgentUpgrader.UpgradeBlockedCode, outcome.Status.LastError?.Code);
        Assert.Equal(0, source.Calls);
        Assert.Empty(runtime.Created);
        Assert.Equal("0.3.0", CurrentTarget(state));
    }

    [Fact]
    public async Task StatusReportsTheInstalledAgentVersion()
    {
        var state = ReconcileTestData.CreateStateDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var artifact = ReconcileTestData.Artifact(CloudArtifactTestData.PayloadJson(generation: 1));
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", artifact);
        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);
        var engine = new ReconcileEngine(
            Config(state),
            new ReconcileStore(state),
            registry,
            runtime,
            new FileSecretResolver(),
            ReconcileTestData.Reporter(),
            (_, _) => Task.CompletedTask,
            agentUpgrader: new AgentUpgrader(state, source: null, ReconcileTestData.Reporter()));

        var outcome = await engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal("0.3.0", outcome.Status.Host.NarwhalVersion);
    }

    [Fact]
    public async Task ABootOkWriteFailureDoesNotFailTheCycle()
    {
        var state = ReconcileTestData.CreateStateDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        Directory.CreateDirectory(Path.Combine(state, "agent", "boot-ok"));
        var artifact = ReconcileTestData.Artifact(CloudArtifactTestData.PayloadJson(generation: 1));
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", artifact);
        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);
        var engine = new ReconcileEngine(
            Config(state),
            new ReconcileStore(state),
            registry,
            runtime,
            new FileSecretResolver(),
            ReconcileTestData.Reporter(),
            (_, _) => Task.CompletedTask,
            agentUpgrader: new AgentUpgrader(state, source: null, ReconcileTestData.Reporter()));

        var outcome = await engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(ReconcileExitCode.Success, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Idle, outcome.Status.Phase);
    }

    [Fact]
    public async Task TheEngineRecordsAVerificationFailureAsBlocked()
    {
        var state = ReconcileTestData.CreateStateDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var spec = Spec("0.4.0", NewBinary);
        var artifact = AgentArtifact(spec);
        var source = new FakeSource(Encoding.UTF8.GetBytes("tampered"));

        var runtime = Runtime();
        var outcome = await RunEngineAsync(state, artifact, source, runtime);

        Assert.Equal(ReconcilePhases.Degraded, outcome.Status.Phase);
        Assert.Equal(AgentUpgrader.UpgradeFailedCode, outcome.Status.LastError?.Code);
        Assert.Equal(
            AgentDigest.FromBinarySha256(spec.BinarySha256),
            new ReconcileStore(state).LoadState().BlockedAgentDigest);
        Assert.Empty(runtime.Created);
        Assert.Equal("0.3.0", CurrentTarget(state));
        Assert.True(File.Exists(Path.Combine(state, "agent", "reverted")));
    }

    [Fact]
    public async Task MarkHealthyPreservesTheRevertMarker()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var upgrader = CreateUpgrader(state, new FakeSource(NewBinary));
        var spec = Spec("0.4.0", NewBinary);
        await upgrader.EvaluateAsync(spec, new ReconcileState(), CancellationToken.None);
        await upgrader.PrepareStartAsync(CancellationToken.None);
        await upgrader.PrepareStartAsync(CancellationToken.None);
        await upgrader.PrepareStartAsync(CancellationToken.None);
        Assert.NotNull(await upgrader.PrepareStartAsync(CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(state, "agent", "reverted")));

        await upgrader.MarkHealthyAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(state, "agent", "reverted")));
        Assert.False(File.Exists(Path.Combine(state, "agent", "pending")));
        Assert.Equal("0.3.0", CurrentTarget(state));
    }

    [Fact]
    public async Task AGuardRevertIsNotLostWhenTheOldCycleRecordsBootOk()
    {
        var state = ReconcileTestData.CreateStateDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var upgrader = CreateUpgrader(state, new FakeSource(NewBinary));
        var spec = Spec("0.4.0", NewBinary);
        await upgrader.EvaluateAsync(spec, new ReconcileState(), CancellationToken.None);
        await upgrader.PrepareStartAsync(CancellationToken.None);
        await upgrader.PrepareStartAsync(CancellationToken.None);
        await upgrader.PrepareStartAsync(CancellationToken.None);
        Assert.NotNull(await upgrader.PrepareStartAsync(CancellationToken.None));
        Assert.Equal("0.3.0", CurrentTarget(state));

        Assert.True(await upgrader.MarkHealthyAsync(CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(state, "agent", "reverted")));

        var source = new FakeSource(NewBinary);
        var outcome = await RunEngineAsync(state, AgentArtifact(spec), source, Runtime());

        Assert.Equal(ReconcilePhases.Degraded, outcome.Status.Phase);
        Assert.Equal(AgentUpgrader.UpgradeFailedCode, outcome.Status.LastError?.Code);
        Assert.Equal(
            AgentDigest.FromBinarySha256(spec.BinarySha256),
            new ReconcileStore(state).LoadState().BlockedAgentDigest);
        Assert.True(outcome.Status.Epoch >= 1);
        Assert.Equal(0, source.Calls);
        Assert.True(File.Exists(Path.Combine(state, "agent", "reverted")));
    }

    [Fact]
    public async Task AVersionsWithDoubleDotsAreRejected()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var source = new FakeSource(NewBinary);
        var upgrader = CreateUpgrader(state, source);

        var result = await upgrader.EvaluateAsync(Spec("1..2", NewBinary), new ReconcileState(), CancellationToken.None);

        Assert.Equal(AgentUpgradeAction.Failed, result.Action);
        Assert.True(result.DigestBlocked);
        Assert.Equal(0, source.Calls);
        Assert.Equal("0.3.0", CurrentTarget(state));
    }

    [Fact]
    public async Task ABusyUpgradeLockDefersBootOkAndRetriesNextTime()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.4.0", NewBinary);
        var upgrader = new AgentUpgrader(
            state,
            new FakeSource(NewBinary),
            ReconcileTestData.Reporter(),
            lockTimeout: TimeSpan.FromMilliseconds(5));
        using var held = await ReconcileLock.AcquireAsync(
            Path.Combine(state, "agent-upgrade.lock"),
            TimeSpan.Zero,
            CancellationToken.None);
        Assert.NotNull(held);

        Assert.False(await upgrader.MarkHealthyAsync(CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(state, "agent", "boot-ok")));

        held.Dispose();
        Assert.True(await upgrader.MarkHealthyAsync(CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(state, "agent", "boot-ok")));
    }

    [Fact]
    public async Task ABusyUpgradeLockStillBlocksAFailedBinary()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var upgrader = new AgentUpgrader(
            state,
            new FakeSource(Encoding.UTF8.GetBytes("tampered")),
            ReconcileTestData.Reporter(),
            lockTimeout: TimeSpan.FromMilliseconds(5));
        using var held = await ReconcileLock.AcquireAsync(
            Path.Combine(state, "agent-upgrade.lock"),
            TimeSpan.Zero,
            CancellationToken.None);
        Assert.NotNull(held);

        var result = await upgrader.EvaluateAsync(Spec("0.4.0", NewBinary), new ReconcileState(), CancellationToken.None);

        Assert.Equal(AgentUpgradeAction.Failed, result.Action);
        Assert.True(result.DigestBlocked);
        Assert.Contains("busy", result.Message, StringComparison.Ordinal);
        Assert.Equal("0.3.0", CurrentTarget(state));
    }

    [Fact]
    public async Task OrdersNumericVersionsNumerically()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.10.0", NewBinary);
        var source = new FakeSource(OldBinary);
        var upgrader = CreateUpgrader(state, source);

        var result = await upgrader.EvaluateAsync(Spec("0.9.0", OldBinary), new ReconcileState(), CancellationToken.None);

        Assert.Equal(AgentUpgradeAction.DowngradeRefused, result.Action);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task TreatsAPrereleaseAsOlderThanTheRelease()
    {
        var state = DaemonRunnerTests.TestDirectory();
        InstallCurrent(state, "0.4.0-alpha.1", OldBinary);
        var source = new FakeSource(NewBinary);
        var upgrader = CreateUpgrader(state, source);

        var result = await upgrader.EvaluateAsync(Spec("0.4.0", NewBinary), new ReconcileState(), CancellationToken.None);

        Assert.Equal(AgentUpgradeAction.Staged, result.Action);
        Assert.Equal("0.4.0", CurrentTarget(state));
    }

    [Fact]
    public void NormalizesALegacyBareBlockedDigestOnLoad()
    {
        var state = DaemonRunnerTests.TestDirectory();
        var store = new ReconcileStore(state);
        store.SaveState(new ReconcileState
        {
            StatusEpoch = 1,
            BlockedAgentDigest = new string('a', 64),
        });

        var loaded = store.LoadState();

        Assert.Equal("sha256:" + new string('a', 64), loaded.BlockedAgentDigest);
    }

    [Fact]
    public void BlockedAgentDigestRoundTripsThroughSignedStatus()
    {
        var document = StatusTestData.Document();
        document.Host.BlockedAgentDigest = "sha256:" + new string('b', 64);

        var decoded = StatusDocumentCodec.Decode(StatusDocumentCodec.Encode(document));

        Assert.Equal(document.Host.BlockedAgentDigest, decoded.Host.BlockedAgentDigest);
    }

    [Fact]
    public async Task StateLossDoesNotLetARemoteNullClearTheLocalBlock()
    {
        var state = ReconcileTestData.CreateStateDirectory();
        InstallCurrent(state, "0.3.0", OldBinary);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 18, 0, 0, TimeSpan.Zero));
        var guard = new AgentUpgrader(state, new FakeSource(NewBinary), ReconcileTestData.Reporter(), clock);
        var spec = Spec("0.4.0", NewBinary);
        await guard.EvaluateAsync(spec, new ReconcileState(), CancellationToken.None);
        clock.Now = clock.Now.AddMinutes(11);
        Assert.NotNull(await guard.RevertExpiredAsync(CancellationToken.None));

        var remote = StatusTestData.Document(highestGeneration: 1);
        remote.HighestGeneration = 0;
        remote.Observed = null;
        remote.Active = null;
        remote.Attempts = null;
        remote.Host.BlockedAgentDigest = null;
        var channel = new FakeStatusChannel { Remote = Verified(remote) };
        var artifact = AgentArtifact(spec);
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", artifact);
        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);
        var source = new FakeSource(NewBinary);
        var engine = new ReconcileEngine(
            Config(state),
            new ReconcileStore(state),
            registry,
            runtime,
            new FileSecretResolver(),
            ReconcileTestData.Reporter(),
            (_, _) => Task.CompletedTask,
            statusChannel: channel,
            statusKeyId: CloudArtifactTestData.KeyId1,
            agentUpgrader: new AgentUpgrader(state, source, ReconcileTestData.Reporter(), clock));

        var outcome = await engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(ReconcilePhases.Degraded, outcome.Status.Phase);
        Assert.Equal(AgentDigest.FromBinarySha256(spec.BinarySha256), new ReconcileStore(state).LoadState().BlockedAgentDigest);
        Assert.Equal(0, source.Calls);
        Assert.Equal("0.3.0", CurrentTarget(state));
        Assert.Contains(
            channel.Published,
            status => status.Phase == ReconcilePhases.Degraded &&
                      status.LastError?.Code == AgentUpgrader.UpgradeFailedCode);
    }

    private static VerifiedStatus Verified(ReconcileStatusDocument document)
    {
        var artifact = StatusTestData.Artifact(document: document);
        return new VerifiedStatus
        {
            Document = document,
            Payload = artifact.Payload,
            Signature = artifact.Signature,
            ManifestDigest = artifact.ManifestDigest,
            VerifyingKeyIds = [CloudArtifactTestData.KeyId1],
        };
    }

    private static AgentConfig Config(string state) => ReconcileTestData.Config(state);

    private static AgentUpgrader CreateUpgrader(string state, IAgentImageSource source) =>
        new(state, source, ReconcileTestData.Reporter());

    private static AgentSpec Spec(string version, byte[] binary, string? digest = null) => new()
    {
        Version = version,
        Repository = Repository,
        Digest = digest ?? "sha256:" + new string('d', 64),
        BinarySha256 = Convert.ToHexStringLower(SHA256.HashData(binary)),
    };

    private static TestArtifact AgentArtifact(AgentSpec spec)
    {
        var agent = $"{{\"version\":\"{spec.Version}\",\"repository\":\"{spec.Repository}\"," +
                    $"\"digest\":\"{spec.Digest}\",\"binary_sha256\":\"{spec.BinarySha256}\"}}";
        return ReconcileTestData.Artifact(CloudArtifactTestData.PayloadJson(generation: 1, agent: agent));
    }

    private static FakeRuntime Runtime() => new();

    private static async Task<ReconcileOutcome> RunEngineAsync(
        string state,
        TestArtifact artifact,
        IAgentImageSource source,
        FakeRuntime? runtime = null)
    {
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", artifact);
        runtime ??= new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);
        var store = new ReconcileStore(state);
        var engine = new ReconcileEngine(
            Config(state),
            store,
            registry,
            runtime,
            new FileSecretResolver(),
            ReconcileTestData.Reporter(),
            (_, _) => Task.CompletedTask,
            agentUpgrader: new AgentUpgrader(state, source, ReconcileTestData.Reporter()));
        return await engine.RunOnceAsync(CancellationToken.None);
    }

    private static void InstallCurrent(string state, string version, byte[] binary)
    {
        var directory = Path.Combine(state, "agent", version);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "narwhal"), binary);
        var link = Path.Combine(state, "agent", "current");
        if (File.Exists(link))
            File.Delete(link);
        File.CreateSymbolicLink(link, version);
    }

    private static string? CurrentTarget(string state) =>
        new FileInfo(Path.Combine(state, "agent", "current")).LinkTarget;

    private static AgentRevertedUpgrade? ReadReverted(string state)
    {
        var path = Path.Combine(state, "agent", "reverted");
        if (!File.Exists(path))
            return null;
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        return new AgentRevertedUpgrade(
            root.GetProperty("version").GetString() ?? "",
            root.GetProperty("digest").GetString() ?? "",
            root.GetProperty("binary_sha256").GetString() ?? "",
            root.TryGetProperty("previous_version", out var previous) ? previous.GetString() : null,
            root.GetProperty("at").GetString() ?? "");
    }

    private sealed class FakeSource(byte[] binary) : IAgentImageSource
    {
        public int Calls { get; private set; }

        public Task<byte[]> FetchBinaryAsync(AgentSpec spec, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(binary);
        }
    }

    internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
