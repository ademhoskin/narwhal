using System.Text.Json;
using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class StatusChannelTests
{
    private static TargetExpectation Target() => new()
    {
        Project = ReconcileTestData.Project,
        Provider = "aws",
        Region = "us-east-1",
        Arch = "amd64",
        Selector = new Dictionary<string, string>(StringComparer.Ordinal) { ["stack"] = "prod" },
    };

    private static StatusArtifactChannel Channel(
        string directory,
        FakeStatusRegistry registry,
        StatusSigningKey? signer = null) =>
        new(
            StatusTestData.Repository,
            registry,
            StatusTestData.Keys(),
            Target(),
            signer ?? StatusSigningKey.FromSeed(CloudArtifactTestData.Seed1),
            new StatusStore(directory),
            ReconcileTestData.Reporter());

    [Fact]
    public async Task PublishesTheSignedStatusAndRetainsAHistorySnapshot()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var registry = new FakeStatusRegistry();
        var channel = Channel(directory, registry);
        var document = StatusTestData.Document(statusSeq: 7);

        var result = await channel.PublishAsync(document, CancellationToken.None);

        Assert.True(result.Published);
        Assert.False(result.Queued);
        var pushed = Assert.Single(registry.PushedManifests);
        Assert.Equal(StatusManifest.LatestTag, pushed.Reference);
        Assert.Equal(StatusManifest.ManifestMediaType, pushed.MediaType);
        Assert.Equal(3, registry.PushedBlobs.Count);
        var configDigest = CloudEncoding.Sha256Digest(StatusManifest.EmptyConfig.Span);
        Assert.Equal(
            "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
            configDigest);
        Assert.Contains(registry.PushedBlobs, blob => blob.Digest == configDigest && blob.Size == 2);
        Assert.Contains("blob:" + configDigest, registry.Events);
        Assert.True(
            registry.Events.IndexOf("blob:" + configDigest) <
            registry.Events.FindIndex(entry => entry.StartsWith("manifest:", StringComparison.Ordinal)));

        using var manifest = JsonDocument.Parse(pushed.Manifest);
        Assert.Equal(StatusManifest.ArtifactType, manifest.RootElement.GetProperty("artifactType").GetString());
        Assert.Equal("7", manifest.RootElement.GetProperty("annotations").GetProperty(StatusManifest.StatusSeqAnnotation).GetString());

        var store = new StatusStore(directory);
        Assert.Equal(1, store.HistoryCount);
        Assert.Equal(0, store.QueueCount);

        var verified = await channel.FetchLatestAsync(CancellationToken.None);
        Assert.Equal(7, verified.Document.StatusSeq);
        Assert.Equal(StatusTestData.HostId, verified.Document.Host.Id);
    }

    [Fact]
    public async Task FallsBackWhenTheRegistryRejectsArtifactType()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var registry = new FakeStatusRegistry { RejectArtifactType = true };
        var channel = Channel(directory, registry);

        var result = await channel.PublishAsync(StatusTestData.Document(), CancellationToken.None);

        Assert.True(result.Published);
        var pushed = Assert.Single(registry.PushedManifests);
        Assert.DoesNotContain("\"artifactType\"", System.Text.Encoding.UTF8.GetString(pushed.Manifest), StringComparison.Ordinal);
        using var fallback = JsonDocument.Parse(pushed.Manifest);
        Assert.Equal(
            StatusManifest.ImageConfigMediaType,
            fallback.RootElement.GetProperty("config").GetProperty("mediaType").GetString());

        var verified = await channel.FetchLatestAsync(CancellationToken.None);
        Assert.Equal(184, verified.Document.StatusSeq);
    }

    [Fact]
    public async Task QueuesNewestAndKeepsHistoryWhenPublishingFails()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var registry = new FakeStatusRegistry { Unavailable = true };
        var channel = Channel(directory, registry);
        var store = new StatusStore(directory);

        var result = await channel.PublishAsync(StatusTestData.Document(statusSeq: 9), CancellationToken.None);

        Assert.False(result.Published);
        Assert.True(result.Queued);
        Assert.Equal(1, store.QueueCount);
        Assert.Equal(1, store.HistoryCount);
        Assert.Equal(9, store.NewestQueued()?.StatusSeq);

        registry.Unavailable = false;
        var retry = await channel.PublishAsync(StatusTestData.Document(statusSeq: 10), CancellationToken.None);

        Assert.True(retry.Published);
        Assert.Equal(0, store.QueueCount);
        Assert.Equal(2, store.HistoryCount);
        Assert.Equal(2, registry.PushedManifests.Count);
        Assert.Equal("9", StatusSeq(registry.PushedManifests[0].Manifest));
        Assert.Equal("10", StatusSeq(registry.PushedManifests[1].Manifest));
    }

    [Fact]
    public async Task DefersPublishingWhenAQueuedStatusIsNewer()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var registry = new FakeStatusRegistry();
        var store = new StatusStore(directory);
        store.Enqueue(new StatusSnapshot
        {
            Epoch = 2,
            StatusSeq = 30,
            Payload = [1],
            Signature = [2],
            Manifest = [3],
            ManifestDigest = StatusTestData.Digest('a'),
            RecordedAt = "2026-11-24T18:32:10Z",
        });
        var channel = Channel(directory, registry);

        var result = await channel.PublishAsync(
            StatusTestData.Document(statusSeq: 20, epoch: 2),
            CancellationToken.None);

        Assert.False(result.Published);
        Assert.False(result.Queued);
        Assert.Empty(registry.PushedManifests);
        Assert.Equal(1, store.QueueCount);
    }

    [Fact]
    public async Task DropsADuplicateQueuedStatusAtTheSameCoordinate()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var registry = new FakeStatusRegistry();
        var store = new StatusStore(directory);
        store.Enqueue(new StatusSnapshot
        {
            Epoch = 2,
            StatusSeq = 30,
            Payload = [1],
            Signature = [2],
            Manifest = [3],
            ManifestDigest = StatusTestData.Digest('a'),
            RecordedAt = "2026-11-24T18:32:10Z",
        });
        var channel = Channel(directory, registry);

        var result = await channel.PublishAsync(
            StatusTestData.Document(statusSeq: 30, epoch: 2),
            CancellationToken.None);

        Assert.True(result.Published);
        Assert.Equal(0, store.QueueCount);
        var pushed = Assert.Single(registry.PushedManifests);
        Assert.Equal("30", StatusSeq(pushed.Manifest));
    }

    [Fact]
    public async Task KeepsAnOlderQueuedSnapshotWhenRepublishingFails()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var registry = new FakeStatusRegistry { Unavailable = true };
        var store = new StatusStore(directory);
        store.Enqueue(new StatusSnapshot
        {
            Epoch = 2,
            StatusSeq = 9,
            Payload = [1],
            Signature = [2],
            Manifest = [3],
            ManifestDigest = StatusTestData.Digest('a'),
            RecordedAt = "2026-11-24T18:32:10Z",
        });
        var channel = Channel(directory, registry);

        var result = await channel.PublishAsync(
            StatusTestData.Document(statusSeq: 10, epoch: 2),
            CancellationToken.None);

        Assert.False(result.Published);
        Assert.True(result.Queued);
        Assert.Equal(2, store.QueueCount);
        Assert.Equal(10, store.NewestQueued()?.StatusSeq);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(401)]
    public async Task DoesNotFallBackForAServerOrAuthFailure(int status)
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var registry = new FakeStatusRegistry
        {
            ManifestFailure = new RegistryException(
                "simulated manifest failure",
                auth: status == 401,
                statusCode: status),
        };
        var channel = Channel(directory, registry);

        var result = await channel.PublishAsync(StatusTestData.Document(), CancellationToken.None);

        Assert.False(result.Published);
        Assert.True(result.Queued);
        Assert.Equal(1, registry.ManifestAttempts);
        Assert.Empty(registry.PushedManifests);
        Assert.Equal(1, new StatusStore(directory).QueueCount);
    }

    [Fact]
    public async Task DoesNotFallBackForANetworkFailure()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var registry = new FakeStatusRegistry
        {
            ManifestFailure = new HttpRequestException("simulated network failure"),
        };
        var channel = Channel(directory, registry);

        var result = await channel.PublishAsync(StatusTestData.Document(), CancellationToken.None);

        Assert.False(result.Published);
        Assert.True(result.Queued);
        Assert.Equal(1, registry.ManifestAttempts);
    }

    [Fact]
    public void BoundsTheQueueAndHistoryTo64NewestSnapshots()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var store = new StatusStore(directory);

        for (var seq = 1; seq <= 70; seq++)
        {
            store.Enqueue(new StatusSnapshot
            {
                Epoch = 1,
                StatusSeq = seq,
                Payload = [1],
                Signature = [2],
                Manifest = [3],
                ManifestDigest = StatusTestData.Digest('a'),
                RecordedAt = "2026-11-24T18:32:10Z",
            });
            store.SaveHistory(new StatusSnapshot
            {
                Epoch = 1,
                StatusSeq = seq,
                Payload = [1],
                Signature = [2],
                Manifest = [3],
                ManifestDigest = StatusTestData.Digest('a'),
                RecordedAt = "2026-11-24T18:32:10Z",
            });
        }

        Assert.Equal(StatusStore.Limit, store.QueueCount);
        Assert.Equal(StatusStore.Limit, store.HistoryCount);
        Assert.Equal(70, store.NewestQueued()?.StatusSeq);
    }

    [Fact]
    public async Task RefusesToPublishWithAMismatchedSigningKeyId()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var channel = Channel(directory, new FakeStatusRegistry());
        var document = StatusTestData.Document(statusKeyId: CloudArtifactTestData.KeyId2);

        await Assert.ThrowsAsync<AgentConfigException>(() => channel.PublishAsync(document, CancellationToken.None));
    }

    [Fact]
    public void PublicationPolicyPublishesOnChangeOrHeartbeat()
    {
        var state = new ReconcileState { StatusEpoch = 1 };
        var now = new DateTimeOffset(2026, 11, 24, 18, 0, 0, TimeSpan.Zero);
        var document = StatusTestData.Document();

        Assert.True(StatusPublicationPolicy.ShouldPublish(state, document, now));

        state.StatusPublishPending = false;
        state.LastStatusFingerprint = StatusPublicationPolicy.Fingerprint(document);
        state.LastStatusPublishedAt = "2026-11-24T17:58:00Z";
        Assert.False(StatusPublicationPolicy.ShouldPublish(state, document, now));
        Assert.True(StatusPublicationPolicy.ShouldPublish(state, document, now.AddMinutes(5)));

        state.StatusPublishPending = true;
        Assert.True(StatusPublicationPolicy.ShouldPublish(state, document, now));

        state.StatusPublishPending = false;
        document.Phase = ReconcilePhases.Degraded;
        Assert.True(StatusPublicationPolicy.ShouldPublish(state, document, now));
    }

    private static string? StatusSeq(byte[] manifest)
    {
        using var document = JsonDocument.Parse(manifest);
        return document.RootElement.GetProperty("annotations").GetProperty(StatusManifest.StatusSeqAnnotation).GetString();
    }
}
