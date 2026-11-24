using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class StatusOrderingTests
{
    [Fact]
    public async Task StateLossBumpsTheEpochAndRestoresBlockedStateFromSignedStatus()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var remoteDocument = StatusTestData.Document(
            statusSeq: 10,
            epoch: 3,
            phase: ReconcilePhases.Degraded,
            highestGeneration: 7,
            blockedGeneration: 5);
        remoteDocument.Host.BlockedAgentDigest = StatusTestData.Digest('e');
        var channel = new FakeStatusChannel { Remote = Verified(remoteDocument) };

        var outcome = await RunAsync(directory, channel);

        var state = new ReconcileStore(directory).LoadState();
        Assert.Equal(4, state.StatusEpoch);
        Assert.Equal(11, state.StatusSeq);
        Assert.Equal(7, state.HighestGeneration);
        Assert.Equal(5, state.BlockedGeneration);
        Assert.Equal(StatusTestData.Digest('e'), state.BlockedAgentDigest);
        Assert.Equal(ReconcilePhases.ReplayRejected, outcome.Status.Phase);
        Assert.Equal(11, outcome.Status.StatusSeq);
        Assert.Equal(4, outcome.Status.Epoch);
    }

    [Fact]
    public async Task IntactStateUsesMaxLocalAndRemoteSequenceAndEpoch()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var store = new ReconcileStore(directory);
        store.SaveState(new ReconcileState
        {
            StatusEpoch = 2,
            StatusSeq = 20,
            Phase = ReconcilePhases.Idle,
        });
        var channel = new FakeStatusChannel
        {
            Remote = Verified(StatusTestData.Document(statusSeq: 10, epoch: 3, highestGeneration: 0)),
        };

        var outcome = await RunAsync(directory, channel);

        var state = store.LoadState();
        Assert.Equal(ReconcileExitCode.Success, outcome.ExitCode);
        Assert.Equal(3, state.StatusEpoch);
        Assert.Equal(24, state.StatusSeq);
        Assert.Equal(1, state.HighestGeneration);
    }

    [Fact]
    public async Task ANewerQueuedStatusAdvancesTheBaselineWithoutRegressing()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var store = new ReconcileStore(directory);
        store.SaveState(new ReconcileState
        {
            StatusEpoch = 2,
            StatusSeq = 10,
            Phase = ReconcilePhases.Idle,
        });
        new StatusStore(directory).Enqueue(new StatusSnapshot
        {
            Epoch = 2,
            StatusSeq = 20,
            Payload = [1],
            Signature = [2],
            Manifest = [3],
            ManifestDigest = StatusTestData.Digest('a'),
            RecordedAt = "2026-11-24T18:32:10Z",
        });
        var channel = new FakeStatusChannel();

        await RunAsync(directory, channel);

        Assert.NotEmpty(channel.Published);
        Assert.All(channel.Published, document => Assert.True(document.StatusSeq > 20));
        Assert.Equal(
            channel.Published.Count,
            channel.Published.DistinctBy(document => (document.Epoch, document.StatusSeq)).Count());
    }

    [Fact]
    public async Task PublishesTheCycleStatusAndRecordsTheHeartbeat()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var channel = new FakeStatusChannel();

        var outcome = await RunAsync(directory, channel);

        Assert.Equal(ReconcileExitCode.Success, outcome.ExitCode);
        Assert.Equal(4, channel.Published.Count);
        Assert.Equal(
            [ReconcilePhases.Pulling, ReconcilePhases.Applying, ReconcilePhases.Verifying, ReconcilePhases.Idle],
            channel.Published.Select(document => document.Phase).ToArray());
        var published = channel.Published[^1];
        Assert.Equal(4, published.StatusSeq);
        Assert.Equal("i-test-host", published.Host.Id);
        Assert.Equal(CloudArtifactTestData.KeyId1, published.Host.StatusKeyId);
        Assert.Equal(ReconcilePhases.Idle, published.Phase);
        Assert.Equal(1, published.Active?.Generation);

        var state = new ReconcileStore(directory).LoadState();
        Assert.False(state.StatusPublishPending);
        Assert.NotNull(state.LastStatusPublishedAt);
        Assert.NotNull(state.LastStatusFingerprint);
    }

    [Fact]
    public async Task APublishFailurePreservesTheCycleResult()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var channel = new FakeStatusChannel { PublishFails = true };

        var outcome = await RunAsync(directory, channel);

        Assert.Equal(ReconcileExitCode.Success, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Idle, outcome.Status.Phase);
        Assert.Empty(channel.Published);
        var state = new ReconcileStore(directory).LoadState();
        Assert.True(state.StatusPublishPending);
        Assert.Null(state.LastStatusPublishedAt);
    }

    [Fact]
    public async Task APublishExceptionPreservesTheCycleResult()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var channel = new FakeStatusChannel { ThrowOnPublish = true };

        var outcome = await RunAsync(directory, channel);

        Assert.Equal(ReconcileExitCode.Success, outcome.ExitCode);
        Assert.True(new ReconcileStore(directory).LoadState().StatusPublishPending);
    }

    [Fact]
    public async Task AnUnchangedIdleCycleDoesNotRepublishBeforeTheHeartbeat()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var channel = new FakeStatusChannel();
        var runtime = new FakeRuntime();

        await RunAsync(directory, channel, fakeRuntime: runtime);
        await RunAsync(directory, channel, fakeRuntime: runtime);

        Assert.Equal(4, channel.Published.Count);
        Assert.Equal(2, channel.ReadCalls);
    }

    [Fact]
    public async Task PendingPublishRetriesOnTheNextCycle()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var channel = new FakeStatusChannel { PublishFails = true };
        var runtime = new FakeRuntime();

        await RunAsync(directory, channel, fakeRuntime: runtime);
        channel.PublishFails = false;
        await RunAsync(directory, channel, fakeRuntime: runtime);

        Assert.Single(channel.Published);
        Assert.Equal(5, channel.Published[^1].StatusSeq);
        Assert.Equal(ReconcilePhases.Idle, channel.Published[^1].Phase);
        Assert.False(new ReconcileStore(directory).LoadState().StatusPublishPending);
    }

    [Fact]
    public void CorruptStateIsTreatedAsLoss()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var store = new ReconcileStore(directory);
        File.WriteAllText(store.StatePath, "{not json");

        var state = store.LoadStateForRecovery(out var lost, out var warning);

        Assert.True(lost);
        Assert.NotNull(warning);
        Assert.Equal(0, state.HighestGeneration);
    }

    [Fact]
    public async Task StateLossRetainsTheLastKnownEpochFromHistory()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var store = new StatusStore(directory);
        store.SaveHistory(new StatusSnapshot
        {
            Epoch = 5,
            StatusSeq = 50,
            Payload = [1],
            Signature = [2],
            Manifest = [3],
            ManifestDigest = StatusTestData.Digest('a'),
            RecordedAt = "2026-11-24T18:32:10Z",
        });
        var channel = new FakeStatusChannel();

        await RunAsync(directory, channel);

        var state = new ReconcileStore(directory).LoadState();
        Assert.Equal(6, state.StatusEpoch);
        Assert.Equal(54, state.StatusSeq);
        Assert.True(state.StatusStateLost);
    }

    [Fact]
    public async Task AChangedHostIdentityBumpsTheEpoch()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var store = new ReconcileStore(directory);
        store.SaveState(new ReconcileState
        {
            StatusEpoch = 2,
            StatusSeq = 10,
            LastHostId = "old-host",
            Phase = ReconcilePhases.Idle,
        });
        var channel = new FakeStatusChannel();

        await RunAsync(directory, channel, config: ReconcileTestData.Config(directory, hostId: "new-host"));

        var state = store.LoadState();
        Assert.Equal(3, state.StatusEpoch);
        Assert.Equal("new-host", state.LastHostId);
    }

    [Fact]
    public async Task AMissingArtifactCacheBumpsTheEpochOnce()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var store = new ReconcileStore(directory);
        store.SaveState(new ReconcileState
        {
            StatusEpoch = 2,
            StatusSeq = 10,
            ActiveGeneration = 1,
            ActivePayloadDigest = StatusTestData.Digest('c'),
            Phase = ReconcilePhases.Idle,
        });
        var channel = new FakeStatusChannel();

        await RunAsync(directory, channel);

        var state = store.LoadState();
        Assert.Equal(3, state.StatusEpoch);
        Assert.True(state.ArtifactCacheMissing);

        await RunAsync(directory, channel, fakeRuntime: new FakeRuntime());
        var recovered = store.LoadState();
        Assert.Equal(3, recovered.StatusEpoch);
        Assert.False(recovered.ArtifactCacheMissing);
    }

    [Fact]
    public async Task AnUnreadableJournalBumpsTheEpochOnce()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var store = new ReconcileStore(directory);
        store.SaveState(new ReconcileState
        {
            StatusEpoch = 2,
            StatusSeq = 10,
            Phase = ReconcilePhases.Idle,
        });
        File.WriteAllText(store.JournalPath, "{not json");
        var channel = new FakeStatusChannel();

        await RunAsync(directory, channel);
        Assert.True(store.LoadState().StatusStateLost);

        await RunAsync(directory, channel, fakeRuntime: new FakeRuntime());

        var state = store.LoadState();
        Assert.Equal(3, state.StatusEpoch);
        Assert.False(state.StatusStateLost);
    }

    [Fact]
    public async Task FailureLogsAreBoundedAndRedactedBeforePublishing()
    {
        const string secret = "super-secret-value";
        var directory = ReconcileTestData.CreateStateDirectory();
        var secretPath = Path.Combine(directory, "password");
        File.WriteAllText(secretPath, secret);

        var services =
            "{\"api\":{\"image\":\"" + CloudArtifactTestData.ImageDigest + "\",\"ports\":[\"8080:8080\"]," +
            "\"secrets\":{\"PASSWORD\":{\"source\":\"file\",\"id\":\"" + secretPath.Replace("\\", "/", StringComparison.Ordinal) + "\"}}," +
            "\"health\":{\"command\":\"true\",\"interval\":1,\"timeout\":1,\"retries\":1}}}";
        var payloadJson = CloudArtifactTestData.PayloadJson(generation: 1, services: services);
        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);
        runtime.SetHealth("api", "unhealthy");
        runtime.SetLogs("narwhal-shop-api", "token=" + secret, "Authorization: Bearer topsecret");
        var channel = new FakeStatusChannel();

        var outcome = await RunAsync(
            directory,
            channel,
            payloadJson,
            ReconcileTestData.Config(directory, logRedact: ["Bearer topsecret"]),
            runtime);

        Assert.Equal(ReconcileExitCode.Runtime, outcome.ExitCode);
        Assert.Equal(ReconcilePhases.Failed, outcome.Status.Phase);
        Assert.NotEmpty(outcome.Status.LogsTail);
        var serialized = System.Text.Encoding.UTF8.GetString(StatusDocumentCodec.Encode(outcome.Status));
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecret", serialized, StringComparison.Ordinal);
        Assert.Contains("redacted", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, File.ReadAllText(new ReconcileStore(directory).StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureMessagesAreRedactedBeforeJournalAndStateWrites()
    {
        const string secret = "super-secret-value";
        var directory = ReconcileTestData.CreateStateDirectory();
        var secretPath = Path.Combine(directory, "password");
        File.WriteAllText(secretPath, secret);

        var services =
            "{\"api\":{\"image\":\"" + CloudArtifactTestData.ImageDigest + "\",\"ports\":[\"8080:8080\"]," +
            "\"secrets\":{\"PASSWORD\":{\"source\":\"file\",\"id\":\"" + secretPath.Replace("\\", "/", StringComparison.Ordinal) + "\"}}}}";
        var runtime = new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);
        runtime.FailCreateWith = "podman rejected the container: token=" + secret;
        var channel = new FakeStatusChannel();

        var outcome = await RunAsync(
            directory,
            channel,
            CloudArtifactTestData.PayloadJson(generation: 1, services: services),
            fakeRuntime: runtime);

        Assert.Equal(ReconcileExitCode.Runtime, outcome.ExitCode);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
            Assert.DoesNotContain(secret, File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForgedRemoteStatusIsIgnoredWhenRestoringOrdering()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var forged = StatusTestData.Artifact(document: StatusTestData.Document(statusSeq: 90, epoch: 9, highestGeneration: 5));
        var text = System.Text.Encoding.UTF8.GetString(forged.Signature);
        var marker = "\"sig\":\"";
        var index = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var signature = CanonicalJson.Canonicalize(System.Text.Encoding.UTF8.GetBytes(
            text[..index] + (text[index] == 'A' ? 'B' : 'A') + text[(index + 1)..]));
        var manifest = StatusManifest.Build(
            CloudEncoding.Sha256Digest(forged.Payload),
            forged.Payload.Length,
            CloudEncoding.Sha256Digest(signature),
            signature.Length,
            forged.Document.StatusSeq,
            fallback: false);
        var tampered = new TestStatusArtifact
        {
            Document = forged.Document,
            Payload = forged.Payload,
            Signature = signature,
            Manifest = manifest,
            ManifestDigest = CloudEncoding.Sha256Digest(manifest),
        };

        var statusRegistry = new FakeStatusRegistry();
        statusRegistry.AddStatus(StatusTestData.Repository, StatusManifest.LatestTag, tampered);
        var channel = new StatusArtifactChannel(
            StatusTestData.Repository,
            statusRegistry,
            StatusTestData.Keys(),
            new TargetExpectation
            {
                Project = ReconcileTestData.Project,
                Provider = "aws",
                Region = "us-east-1",
                Arch = "amd64",
                Selector = new Dictionary<string, string>(StringComparer.Ordinal) { ["stack"] = "prod" },
            },
            signingKey: null,
            new StatusStore(directory),
            ReconcileTestData.Reporter());

        var outcome = await RunAsync(directory, channel);

        var state = new ReconcileStore(directory).LoadState();
        Assert.Equal(ReconcileExitCode.Success, outcome.ExitCode);
        Assert.Equal(1, state.StatusEpoch);
        Assert.Equal(4, state.StatusSeq);
        Assert.Equal(1, state.HighestGeneration);
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

    private static async Task<ReconcileOutcome> RunAsync(
        string directory,
        IStatusChannel channel,
        string? payloadJson = null,
        AgentConfig? config = null,
        FakeRuntime? fakeRuntime = null)
    {
        var artifact = ReconcileTestData.Artifact(payloadJson ?? CloudArtifactTestData.PayloadJson(generation: 1));
        var registry = new FakeRegistry();
        registry.Add(ReconcileTestData.Desired, "desired", artifact);
        var runtime = fakeRuntime ?? new FakeRuntime();
        runtime.AddImage(ReconcileTestData.Repository, CloudArtifactTestData.ImageDigest);

        var engine = new ReconcileEngine(
            config ?? ReconcileTestData.Config(directory),
            new ReconcileStore(directory),
            registry,
            runtime,
            new FileSecretResolver(),
            ReconcileTestData.Reporter(),
            (_, _) => Task.CompletedTask,
            statusChannel: channel,
            statusKeyId: CloudArtifactTestData.KeyId1);
        return await engine.RunOnceAsync(CancellationToken.None);
    }
}
