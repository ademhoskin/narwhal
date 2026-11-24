using System.Text.Json;
using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class ReconcileEngineTests
{
    [Fact]
    public async Task AppliesANewGenerationAndRecordsIt()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var artifact = Artifact(1);
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", artifact);
        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);

        var outcome = await RunAsync(directory, registry, runtime);

        Assert.Equal(ReconcileExitCode.Success, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Idle, outcome.Status.Phase);
        Assert.Equal(1, outcome.Status.ActiveGeneration);
        Assert.Single(runtime.Created);
        Assert.Equal(1, runtime.Created[0].Generation);
        Assert.False(File.Exists(new ReconcileStore(directory).JournalPath));

        var state = new ReconcileStore(directory).LoadState();
        Assert.Equal(1, state.HighestGeneration);
        Assert.Equal(1, state.ActiveGeneration);
        Assert.Equal(artifact.PayloadDigest, state.ActivePayloadDigest);
        Assert.Equal(artifact.Manifest.Digest, state.ActiveManifestDigest);
    }

    [Fact]
    public async Task ASecondRunIsANoOp()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var artifact = Artifact(1);
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", artifact);
        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);

        await RunAsync(directory, registry, runtime);
        var outcome = await RunAsync(directory, registry, runtime);

        Assert.Equal(ReconcileExitCode.Success, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Idle, outcome.Status.Phase);
        Assert.Single(runtime.Created);
    }

    [Fact]
    public async Task ABlockedGenerationIsRejectedWithoutTouchingContainers()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var artifact = Artifact(2);
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", artifact);
        var runtime = new FakeRuntime();
        var store = new ReconcileStore(directory);
        store.SaveState(new ReconcileState
        {
            HighestGeneration = 2,
            HighestPayloadDigest = artifact.PayloadDigest,
            BlockedGeneration = 2,
            StatusEpoch = 1,
        });

        var outcome = await RunAsync(directory, registry, runtime);

        Assert.Equal(ReconcileExitCode.Verification, outcome.ExitCode);
        Assert.Equal(CloudArtifactErrorCodes.GenerationBlocked, outcome.Status.LastError?.Code);
        Assert.Empty(runtime.Created);
        Assert.Empty(runtime.Removed);
    }

    [Fact]
    public async Task AHealthFailureRollsBackToTheCachedActiveGeneration()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var first = Artifact(1);
        var second = Artifact(
            2,
            env: "{\"LOG_LEVEL\":\"debug\"}",
            previous: Previous(first, 1),
            health: "{\"command\":\"true\",\"interval\":1,\"timeout\":1,\"retries\":1}");
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", second);
        registry.Add(ReconcileTestData.Desired, "g1", first);

        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);
        var firstPlan = ReconcileTestData.Plan();
        runtime.AddContainer(ReconcileTestData.Observation(firstPlan));
        runtime.SetHealth("api", "unhealthy", "healthy");

        var store = new ReconcileStore(directory);
        store.SaveState(ActiveState(first));

        var journalSeen = false;
        runtime.BeforeCreate = () =>
        {
            if (journalSeen)
                return;
            var journal = store.LoadJournal(out _);
            Assert.NotNull(journal);
            Assert.Equal(JournalPhases.Applying, journal!.Phase);
            Assert.Null(journal.BlockedGeneration);
            journalSeen = true;
        };

        var outcome = await RunAsync(directory, registry, runtime);

        Assert.True(journalSeen);
        Assert.Equal(ReconcileExitCode.Runtime, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Degraded, outcome.Status.Phase);
        Assert.Equal(2, outcome.Status.BlockedGeneration);
        Assert.Equal(1, outcome.Status.ActiveGeneration);
        Assert.Equal("health_unhealthy", outcome.Status.LastError?.Code);
        Assert.Equal(2, runtime.Created.Count);
        Assert.Equal(1, runtime.Created[^1].Generation);
        Assert.Equal(firstPlan.ConfigHash, runtime.Created[^1].Plan.ConfigHash);
        Assert.False(File.Exists(store.JournalPath));

        var state = store.LoadState();
        Assert.Equal(2, state.HighestGeneration);
        Assert.Equal(2, state.BlockedGeneration);
        Assert.Equal(1, state.ActiveGeneration);
        Assert.Equal(ReconcilePhases.Degraded, state.Phase);
    }

    [Fact]
    public async Task ARepositoryDigestMismatchFailsAndRollsBack()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var otherDigest = "sha256:" + new string('b', 64);
        var first = Artifact(1);
        var second = Artifact(2, imageDigest: otherDigest, previous: Previous(first, 1));
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", second);
        registry.Add(ReconcileTestData.Desired, "g1", first);

        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);
        runtime.AddContainer(ReconcileTestData.Observation(ReconcileTestData.Plan()));
        runtime.BadImageDigests.Add(otherDigest);

        var store = new ReconcileStore(directory);
        store.SaveState(ActiveState(first));

        var outcome = await RunAsync(directory, registry, runtime);

        Assert.Equal(ReconcileExitCode.Runtime, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Degraded, outcome.Status.Phase);
        Assert.Equal("image_digest_mismatch", outcome.Status.LastError?.Code);
        Assert.Contains($"{ReconcileTestData.Repository}@{otherDigest}", runtime.Pulled);
        Assert.Equal(1, outcome.Status.ActiveGeneration);
    }

    [Fact]
    public async Task SecretValuesNeverReachStateStatusOrJournal()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var secretPath = Path.Combine(directory, "db-password");
        const string secret = "hunter2-very-secret";
        File.WriteAllText(secretPath, secret);

        var artifact = Artifact(1, secrets: $"{{\"PASSWORD\":{{\"source\":\"file\",\"id\":\"{secretPath}\"}}}}");
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", artifact);
        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);

        var outcome = await RunAsync(directory, registry, runtime);
        var statusJson = JsonSerializer.Serialize(outcome.Status, ReconcileJsonContext.Compact.ReconcileStatusDocument);
        var stateJson = File.ReadAllText(new ReconcileStore(directory).StatePath);

        Assert.Equal(ReconcileExitCode.Success, outcome.ExitCode);
        Assert.Equal(secret, runtime.Secrets["shop-api-PASSWORD"]);
        Assert.DoesNotContain(secret, statusJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, stateJson, StringComparison.Ordinal);
        Assert.False(File.Exists(new ReconcileStore(directory).JournalPath));
        Assert.DoesNotContain(secret, runtime.Created[0].Plan.ConfigHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrphansAreRemovedOnlyAfterASuccessfulApplyAndVolumesArePreserved()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var artifact = Artifact(1, volumes: "[\"shop-data:/var/lib/data\"]");
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", artifact);
        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);

        var plan = ReconcileTestData.Plan(volumes: ["shop-data:/var/lib/data"]);
        runtime.AddContainer(ReconcileTestData.Observation(
            plan,
            mounts: [new MountObservation("volume", "shop-data", "/var/lib/data", false)]));
        var orphan = ReconcileTestData.Observation(
            ReconcileTestData.Plan(name: "web"),
            name: "narwhal-shop-web");
        runtime.AddContainer(orphan);

        var outcome = await RunAsync(directory, registry, runtime);

        Assert.Equal(ReconcileExitCode.Success, outcome.ExitCode);
        Assert.Contains("narwhal-shop-web", runtime.Removed);
        Assert.Contains("shop-data", runtime.EnsuredVolumes);
        Assert.Empty(runtime.Created);
    }

    [Fact]
    public async Task ResumesAnApplyingJournalIdempotently()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var artifact = Artifact(1);
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", artifact);
        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);

        var store = new ReconcileStore(directory);
        store.SaveJournal(new ReconcileJournal
        {
            Generation = 1,
            PayloadDigest = artifact.PayloadDigest,
            ManifestDigest = artifact.Manifest.Digest,
            Phase = JournalPhases.Applying,
            HighestGeneration = 1,
            HighestPayloadDigest = artifact.PayloadDigest,
            ActiveGeneration = null,
            Actions = [new JournalAction { Service = "api", Action = ServiceActions.Create }],
            StartedAt = "2026-11-24T18:30:00Z",
        });

        var outcome = await RunAsync(directory, registry, runtime);

        Assert.Equal(ReconcileExitCode.Success, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Idle, outcome.Status.Phase);
        Assert.Single(runtime.Created);
        Assert.Equal(1, runtime.Created[0].Generation);
        Assert.False(File.Exists(store.JournalPath));
        var state = store.LoadState();
        Assert.Equal(artifact.PayloadDigest, state.HighestPayloadDigest);
        Assert.Equal(1, state.ActiveGeneration);
    }

    [Fact]
    public async Task RecoversARollingBackJournalWithoutContactingTheRegistry()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var first = Artifact(1);
        var store = new ReconcileStore(directory);
        store.SaveArtifact(first.PayloadDigest, first.Payload, first.Signature, first.Manifest.Digest, first.Manifest.Bytes);
        store.SaveState(new ReconcileState
        {
            HighestGeneration = 2,
            ActiveGeneration = 1,
            ActivePayloadDigest = first.PayloadDigest,
            ActiveManifestDigest = first.Manifest.Digest,
            BlockedGeneration = 2,
            LastPayloadDigest = first.PayloadDigest,
            LastManifestDigest = first.Manifest.Digest,
            StatusEpoch = 1,
        });
        store.SaveJournal(new ReconcileJournal
        {
            Generation = 2,
            PayloadDigest = "sha256:" + new string('d', 64),
            ManifestDigest = "sha256:" + new string('e', 64),
            Phase = JournalPhases.RollingBack,
            HighestGeneration = 2,
            BlockedGeneration = 2,
            ActiveGeneration = 1,
            ActivePayloadDigest = first.PayloadDigest,
            ActiveManifestDigest = first.Manifest.Digest,
            Actions = [new JournalAction { Service = "api", Action = ServiceActions.Recreate, Touched = true }],
            LastError = new LastErrorInfo { Code = "health_timeout", Service = "api", Message = "timeout" },
            StartedAt = "2026-11-24T18:30:00Z",
        });

        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);
        runtime.AddContainer(ReconcileTestData.Observation(ReconcileTestData.Plan(generationHash: "sha256:failed")));
        var registry = new FakeRegistry { Unavailable = true };

        var outcome = await RunAsync(directory, registry, runtime);

        Assert.Equal(ReconcileExitCode.Runtime, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Degraded, outcome.Status.Phase);
        Assert.Equal(0, registry.ManifestRequests);
        Assert.Single(runtime.Created);
        Assert.Equal(1, runtime.Created[0].Generation);
        Assert.False(File.Exists(store.JournalPath));
    }

    [Fact]
    public async Task RegistryOutageReconcilesFromTheCachedArtifact()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var first = Artifact(1);
        var store = new ReconcileStore(directory);
        store.SaveArtifact(first.PayloadDigest, first.Payload, first.Signature, first.Manifest.Digest, first.Manifest.Bytes);
        store.SaveState(new ReconcileState
        {
            HighestGeneration = 1,
            HighestPayloadDigest = first.PayloadDigest,
            ActiveGeneration = 1,
            ActivePayloadDigest = first.PayloadDigest,
            ActiveManifestDigest = first.Manifest.Digest,
            LastPayloadDigest = first.PayloadDigest,
            LastManifestDigest = first.Manifest.Digest,
            Phase = ReconcilePhases.Idle,
            StatusEpoch = 1,
        });

        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);
        runtime.AddContainer(ReconcileTestData.Observation(ReconcileTestData.Plan()));

        var outcome = await RunAsync(directory, new FakeRegistry { Unavailable = true }, runtime);

        Assert.Equal(ReconcileExitCode.Success, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Idle, outcome.Status.Phase);
        Assert.Empty(runtime.Created);
    }

    [Fact]
    public async Task APlannedButUntouchedServiceIsNotRolledBack()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var first = Artifact(1);
        var second = Artifact(
            2,
            previous: Previous(first, 1),
            servicesJson:
                $"{{\"api\":{{\"image\":\"{CloudArtifactTestData.ImageDigest}\",\"ports\":[\"8080:8080\"],\"env\":{{\"LOG_LEVEL\":\"debug\"}},\"health\":{{\"command\":\"true\",\"interval\":1,\"timeout\":1,\"retries\":1}}}}," +
                $"\"worker\":{{\"image\":\"{CloudArtifactTestData.ImageDigest}\",\"ports\":[]}}}}");
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", second);
        registry.Add(ReconcileTestData.Desired, "g1", first);

        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);
        runtime.AddContainer(ReconcileTestData.Observation(ReconcileTestData.Plan()));
        runtime.SetHealth("api", "unhealthy", "healthy");

        var store = new ReconcileStore(directory);
        store.SaveState(ActiveState(first));

        var outcome = await RunAsync(directory, registry, runtime);

        Assert.Equal(ReconcileExitCode.Runtime, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Degraded, outcome.Status.Phase);
        Assert.Equal(1, outcome.Status.ActiveGeneration);
        Assert.DoesNotContain(runtime.Created, entry => entry.Plan.Name == "worker");
        Assert.DoesNotContain("narwhal-shop-worker", runtime.Removed);
        Assert.All(runtime.Created, entry => Assert.Equal("api", entry.Plan.Name));
        Assert.Equal(2, runtime.Created.Count);
    }

    [Fact]
    public async Task ABlockedGenerationWithAnActiveGenerationIsDegraded()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var first = Artifact(1);
        var second = Artifact(2, previous: Previous(first, 1));
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", second);
        var runtime = new FakeRuntime();

        var store = new ReconcileStore(directory);
        store.SaveState(new ReconcileState
        {
            HighestGeneration = 2,
            HighestPayloadDigest = second.PayloadDigest,
            BlockedGeneration = 2,
            ActiveGeneration = 1,
            ActivePayloadDigest = first.PayloadDigest,
            ActiveManifestDigest = first.Manifest.Digest,
            Phase = ReconcilePhases.Idle,
            StatusEpoch = 1,
        });

        var outcome = await RunAsync(directory, registry, runtime);

        Assert.Equal(ReconcileExitCode.Verification, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Degraded, outcome.Status.Phase);
        Assert.Equal(2, outcome.Status.BlockedGeneration);
        Assert.Equal(1, outcome.Status.ActiveGeneration);
        Assert.Equal(CloudArtifactErrorCodes.GenerationBlocked, outcome.Status.LastError?.Code);
        Assert.Equal(ReconcilePhases.Degraded, store.LoadState().Phase);
    }

    [Fact]
    public async Task ABlockedGenerationAfterAFailedRollbackStaysFailed()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var first = Artifact(41);
        var second = Artifact(42, previous: Previous(first, 41));
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", second);
        var runtime = new FakeRuntime();

        var store = new ReconcileStore(directory);
        store.SaveState(new ReconcileState
        {
            HighestGeneration = 42,
            HighestPayloadDigest = second.PayloadDigest,
            BlockedGeneration = 42,
            ActiveGeneration = 41,
            ActivePayloadDigest = first.PayloadDigest,
            ActiveManifestDigest = first.Manifest.Digest,
            LastPayloadDigest = second.PayloadDigest,
            LastManifestDigest = second.Manifest.Digest,
            Phase = ReconcilePhases.Failed,
            RollbackFailed = true,
            LastError = new LastErrorInfo
            {
                Code = "rollback_failed",
                Service = "api",
                Message = "the previous generation could not be restored",
                At = "2026-11-24T18:30:00Z",
            },
            StatusEpoch = 1,
        });

        var outcome = await RunAsync(directory, registry, runtime);

        Assert.Equal(ReconcileExitCode.Verification, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Failed, outcome.Status.Phase);
        Assert.Equal("rollback_failed", outcome.Status.LastError?.Code);
        Assert.Equal("failed", outcome.Status.Active?.State);

        var persisted = store.LoadState();
        Assert.Equal(ReconcilePhases.Failed, persisted.Phase);
        Assert.Equal("rollback_failed", persisted.LastError?.Code);
        Assert.True(persisted.RollbackFailed);
    }

    [Fact]
    public async Task ABlockedGenerationAfterATargetMismatchIsDegraded()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var first = Artifact(41);
        var second = Artifact(42, previous: Previous(first, 41));
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", second);
        var runtime = new FakeRuntime();

        var store = new ReconcileStore(directory);
        store.SaveState(new ReconcileState
        {
            HighestGeneration = 42,
            HighestPayloadDigest = second.PayloadDigest,
            BlockedGeneration = 42,
            ActiveGeneration = 41,
            ActivePayloadDigest = first.PayloadDigest,
            ActiveManifestDigest = first.Manifest.Digest,
            LastPayloadDigest = second.PayloadDigest,
            LastManifestDigest = second.Manifest.Digest,
            Phase = ReconcilePhases.Failed,
            LastError = new LastErrorInfo
            {
                Code = CloudArtifactErrorCodes.TargetMismatch,
                Message = "artifact provider, region, or arch does not match the configured target",
                At = "2026-11-24T18:30:00Z",
            },
            StatusEpoch = 1,
        });

        var outcome = await RunAsync(directory, registry, runtime);

        Assert.Equal(ReconcileExitCode.Verification, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Degraded, outcome.Status.Phase);
        Assert.Equal(41, outcome.Status.Active?.Generation);
        Assert.Equal("degraded", outcome.Status.Active?.State);
        Assert.False(store.LoadState().RollbackFailed);
    }

    [Fact]
    public async Task AVerificationFailureLeavesAnUntouchedActiveGenerationHealthy()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var first = Artifact(1);
        var second = Artifact(2, previous: Previous(first, 1), region: "eu-west-1");
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", second);
        var runtime = new FakeRuntime();

        var store = new ReconcileStore(directory);
        store.SaveState(ActiveState(first));

        var outcome = await RunAsync(directory, registry, runtime);

        Assert.Equal(ReconcileExitCode.Verification, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Failed, outcome.Status.Phase);
        Assert.Equal(CloudArtifactErrorCodes.TargetMismatch, outcome.Status.LastError?.Code);
        Assert.Equal(1, outcome.Status.Active?.Generation);
        Assert.Equal("healthy", outcome.Status.Active?.State);
        Assert.Empty(runtime.Created);
        Assert.Empty(runtime.Removed);
    }

    [Fact]
    public async Task AnOfflineRunPrefersTheActiveCachedArtifactOverTheFailedHighest()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var first = Artifact(1);
        var second = Artifact(2, previous: Previous(first, 1));
        var store = new ReconcileStore(directory);
        store.SaveArtifact(first.PayloadDigest, first.Payload, first.Signature, first.Manifest.Digest, first.Manifest.Bytes);
        store.SaveState(new ReconcileState
        {
            HighestGeneration = 2,
            HighestPayloadDigest = second.PayloadDigest,
            BlockedGeneration = 2,
            ActiveGeneration = 1,
            ActivePayloadDigest = first.PayloadDigest,
            ActiveManifestDigest = first.Manifest.Digest,
            LastPayloadDigest = second.PayloadDigest,
            LastManifestDigest = second.Manifest.Digest,
            Phase = ReconcilePhases.Degraded,
            StatusEpoch = 1,
        });

        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);
        runtime.AddContainer(ReconcileTestData.Observation(ReconcileTestData.Plan()));

        var outcome = await RunAsync(directory, new FakeRegistry { Unavailable = true }, runtime);

        Assert.Equal(ReconcileExitCode.Success, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Idle, outcome.Status.Phase);
        Assert.Equal(1, outcome.Status.ActiveGeneration);
        Assert.Empty(runtime.Created);

        var state = store.LoadState();
        Assert.Equal(2, state.HighestGeneration);
        Assert.Equal(second.PayloadDigest, state.HighestPayloadDigest);
        Assert.Equal(2, state.BlockedGeneration);
        Assert.Equal(1, state.ActiveGeneration);
    }

    [Fact]
    public async Task UnavailableRegistryWithoutACacheFails()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var second = Artifact(2);
        var store = new ReconcileStore(directory);
        store.SaveState(new ReconcileState
        {
            HighestGeneration = 2,
            HighestPayloadDigest = second.PayloadDigest,
            Phase = ReconcilePhases.Degraded,
            StatusEpoch = 1,
        });

        var runtime = new FakeRuntime();
        var outcome = await RunAsync(directory, new FakeRegistry { Unavailable = true }, runtime);

        Assert.Equal(ReconcileExitCode.Runtime, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.RegistryUnavailable, outcome.Status.Phase);
        Assert.Empty(runtime.Created);
    }

    [Fact]
    public void StatusDocumentsDoNotEmbedSignatureMaterial()
    {
        Assert.Null(typeof(ReconcileStatusDocument).GetProperty("Signature"));
        Assert.Null(typeof(ReconcileStatusDocument).GetProperty("StatusSignature"));
    }

    [Fact]
    public void TrustSetsComparePublicKeyBytesAndExpiry()
    {
        var key = new TrustedKey
        {
            KeyId = CloudArtifactTestData.KeyId1,
            PublicKey = CloudArtifactTestData.PublicKey1,
        };
        var other = new TrustedKey
        {
            KeyId = CloudArtifactTestData.KeyId1,
            PublicKey = CloudArtifactTestData.PublicKey1,
        };

        Assert.True(ReconcileEngine.TrustSetsEqual(TrustSet.FromKeys([key]), TrustSet.FromKeys([other])));
        Assert.False(ReconcileEngine.TrustSetsEqual(
            TrustSet.FromKeys([key]),
            TrustSet.FromKeys([new TrustedKey
            {
                KeyId = CloudArtifactTestData.KeyId1,
                PublicKey = CloudArtifactTestData.PublicKey1,
                NotAfter = DateTimeOffset.Parse("2027-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            }])));

        var rotated = TrustSet.FromKeys([new TrustedKey
        {
            KeyId = CloudArtifactTestData.KeyId2,
            PublicKey = CloudArtifactTestData.PublicKey2,
        }]);
        Assert.False(ReconcileEngine.TrustSetsEqual(TrustSet.FromKeys([key]), rotated));
    }

    private static async Task<ReconcileOutcome> RunAsync(string directory, FakeRegistry registry, FakeRuntime runtime)
    {
        var store = new ReconcileStore(directory);
        var engine = new ReconcileEngine(
            ReconcileTestData.Config(directory),
            store,
            registry,
            runtime,
            new FileSecretResolver(),
            ReconcileTestData.Reporter(),
            (_, _) => Task.CompletedTask);
        return await engine.RunOnceAsync(CancellationToken.None);
    }

    private static ReconcileState ActiveState(TestArtifact artifact) => new()
    {
        HighestGeneration = 1,
        HighestPayloadDigest = artifact.PayloadDigest,
        ActiveGeneration = 1,
        ActivePayloadDigest = artifact.PayloadDigest,
        ActiveManifestDigest = artifact.Manifest.Digest,
        LastPayloadDigest = artifact.PayloadDigest,
        LastManifestDigest = artifact.Manifest.Digest,
        Phase = ReconcilePhases.Idle,
        StatusEpoch = 1,
    };

    private static TestArtifact Artifact(
        long generation,
        string? previous = null,
        string? env = null,
        string? secrets = null,
        string? volumes = null,
        string? health = null,
        string imageDigest = CloudArtifactTestData.ImageDigest,
        IReadOnlyList<string>? dependsOn = null,
        string? servicesJson = null,
        string region = "us-east-1")
    {
        var depends = dependsOn is null ? "" : ",\"depends_on\":[\"" + string.Join("\",\"", dependsOn) + "\"]";
        var extra = (env is null ? "" : ",\"env\":" + env) +
                    (secrets is null ? "" : ",\"secrets\":" + secrets) +
                    (volumes is null ? "" : ",\"volumes\":" + volumes) +
                    (health is null ? "" : ",\"health\":" + health) +
                    depends;
        var services = servicesJson ??
            $"{{\"api\":{{\"image\":\"{imageDigest}\",\"ports\":[\"8080:8080\"]{extra}}}}}";
        var images = $"{{\"{imageDigest}\":{{\"repository\":\"{ReconcileTestData.Repository}\",\"platform\":\"linux/amd64\",\"size\":1}}}}";
        return ReconcileTestData.Artifact(CloudArtifactTestData.PayloadJson(
            generation: generation,
            previous: previous,
            region: region,
            services: services,
            images: images));
    }

    private static string Previous(TestArtifact artifact, long generation) =>
        $"{{\"generation\":{generation},\"digest\":\"{artifact.PayloadDigest}\",\"manifest_digest\":\"{artifact.Manifest.Digest}\"}}";
}
