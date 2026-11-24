using System.Text.Json;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class ReconcileStoreTests
{
    [Fact]
    public void RoundTripsStateAndUsesSnakeCase()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var store = new ReconcileStore(directory);
        var state = new ReconcileState
        {
            HighestGeneration = 7,
            HighestPayloadDigest = CloudDigest('a'),
            ActiveGeneration = 6,
            ActivePayloadDigest = CloudDigest('b'),
            ActiveManifestDigest = CloudDigest('c'),
            BlockedGeneration = 5,
            Phase = ReconcilePhases.Degraded,
            StatusEpoch = 2,
            StatusSeq = 11,
        };

        store.SaveState(state);
        var loaded = store.LoadState();

        Assert.Equal(7, loaded.HighestGeneration);
        Assert.Equal(6, loaded.ActiveGeneration);
        Assert.Equal(5, loaded.BlockedGeneration);
        Assert.Equal(ReconcilePhases.Degraded, loaded.Phase);
        Assert.Equal(11, loaded.StatusSeq);
        Assert.False(File.Exists(store.StatePath + ".tmp"));

        using var document = JsonDocument.Parse(File.ReadAllBytes(store.StatePath));
        Assert.Equal("narwhal.state/v1", document.RootElement.GetProperty("schema").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("highest_generation").GetInt64());
    }

    [Fact]
    public void IgnoresACorruptJournalWithAWarning()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var store = new ReconcileStore(directory);
        File.WriteAllText(store.JournalPath, "{not json");

        var journal = store.LoadJournal(out var warning);

        Assert.Null(journal);
        Assert.NotNull(warning);
        Assert.Contains("journal", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTripsTheJournalAndClearsIt()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var store = new ReconcileStore(directory);
        var journal = new ReconcileJournal
        {
            Generation = 3,
            PayloadDigest = CloudDigest('a'),
            ManifestDigest = CloudDigest('b'),
            Phase = JournalPhases.Applying,
            HighestGeneration = 3,
            ActiveGeneration = 2,
            Actions = [new JournalAction { Service = "api", Action = ServiceActions.Recreate }],
            StartedAt = "2026-11-24T18:30:00Z",
        };

        store.SaveJournal(journal);
        var loaded = store.LoadJournal(out _);

        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Generation);
        Assert.Equal(ServiceActions.Recreate, Assert.Single(loaded.Actions).Action);

        store.ClearJournal();
        Assert.False(File.Exists(store.JournalPath));
    }

    [Fact]
    public void RoundTripsTheContentAddressedArtifactCache()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var store = new ReconcileStore(directory);
        var payload = "{\"a\":1}"u8.ToArray();
        var signature = "sig"u8.ToArray();
        var manifest = "manifest"u8.ToArray();
        var payloadDigest = Narwhal.Cloud.CloudEncoding.Sha256Digest(payload);
        var manifestDigest = Narwhal.Cloud.CloudEncoding.Sha256Digest(manifest);

        store.SaveArtifact(payloadDigest, payload, signature, manifestDigest, manifest);

        Assert.True(store.TryLoadArtifact(payloadDigest, out var cached));
        Assert.Equal(payload, cached.Payload);
        Assert.Equal(signature, cached.Signature);
        Assert.Equal(manifest, cached.Manifest);
        Assert.Equal(manifestDigest, cached.ManifestDigest);
    }

    [Fact]
    public async Task TheLockIsExclusiveAndReleasesOnDispose()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var store = new ReconcileStore(directory);

        using (var first = await store.TryAcquireLockAsync(0, CancellationToken.None))
        {
            Assert.NotNull(first);
            using var second = await store.TryAcquireLockAsync(0, CancellationToken.None);
            Assert.Null(second);
        }

        using var third = await store.TryAcquireLockAsync(0, CancellationToken.None);
        Assert.NotNull(third);
    }

    [Fact]
    public void FsyncsTheStateDirectoryAfterARename()
    {
        var directory = ReconcileTestData.CreateStateDirectory();
        var store = new ReconcileStore(directory);

        store.SaveState(new ReconcileState { HighestGeneration = 1 });
        ReconcileStore.SyncDirectory(directory);

        Assert.True(File.Exists(store.StatePath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData(null, RecoveryAction.Normal)]
    [InlineData(JournalPhases.Committed, RecoveryAction.ClearCommitted)]
    [InlineData(JournalPhases.RollingBack, RecoveryAction.FinishRollback)]
    [InlineData(JournalPhases.Applying, RecoveryAction.ResumeApplying)]
    public void DecidesRecoveryFromTheJournalPhase(string? phase, RecoveryAction expected)
    {
        var journal = phase is null ? null : new ReconcileJournal { Phase = phase, Generation = 4, BlockedGeneration = null };
        Assert.Equal(expected, ReconcileEngine.DecideRecovery(journal));
    }

    [Fact]
    public void AnApplyingJournalAtTheBlockedGenerationFinishesRollback()
    {
        var journal = new ReconcileJournal
        {
            Phase = JournalPhases.Applying,
            Generation = 4,
            BlockedGeneration = 4,
        };

        Assert.Equal(RecoveryAction.FinishRollback, ReconcileEngine.DecideRecovery(journal));
    }

    private static string CloudDigest(char value) => "sha256:" + new string(value, 64);
}
