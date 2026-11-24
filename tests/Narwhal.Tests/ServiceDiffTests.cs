using Narwhal.Cloud;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class ServiceDiffTests
{
    [Fact]
    public void OrderIsDependencyFirstAndDeterministic()
    {
        var services = new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
        {
            ["api"] = Spec("api", dependsOn: ["db"]),
            ["db"] = Spec("db"),
            ["cache"] = Spec("cache", dependsOn: ["db"]),
        };

        Assert.Equal(["db", "api", "cache"], ServicePlanner.Order(services));
    }

    [Fact]
    public void MissingContainerIsCreate()
    {
        var plan = ReconcileTestData.Plan();
        var diff = ServiceDiffer.Diff([plan], [], new Dictionary<string, bool>());

        Assert.Equal(ServiceActions.Create, Assert.Single(diff.Changes).Action);
        Assert.Empty(diff.Orphans);
    }

    [Fact]
    public void MatchingContainerIsNone()
    {
        var plan = ReconcileTestData.Plan();
        var observation = ReconcileTestData.Observation(plan);
        var diff = ServiceDiffer.Diff([plan], [observation], new Dictionary<string, bool> { [observation.Name] = true });

        Assert.Equal(ServiceActions.None, Assert.Single(diff.Changes).Action);
        Assert.False(diff.HasMutations);
    }

    [Fact]
    public void StoppedMatchingContainerIsStart()
    {
        var plan = ReconcileTestData.Plan();
        var observation = ReconcileTestData.Observation(plan, state: "exited");
        var diff = ServiceDiffer.Diff([plan], [observation], new Dictionary<string, bool> { [observation.Name] = true });

        Assert.Equal(ServiceActions.Start, Assert.Single(diff.Changes).Action);
    }

    [Fact]
    public void AnUnprovenRepositoryDigestForcesRecreate()
    {
        var plan = ReconcileTestData.Plan();
        var observation = ReconcileTestData.Observation(plan);
        var diff = ServiceDiffer.Diff([plan], [observation], new Dictionary<string, bool> { [observation.Name] = false });

        Assert.Equal(ServiceActions.Recreate, Assert.Single(diff.Changes).Action);
    }

    [Fact]
    public void AChangedConfigHashForcesRecreate()
    {
        var plan = ReconcileTestData.Plan();
        var observation = ReconcileTestData.Observation(plan, configHash: "sha256:old");
        var diff = ServiceDiffer.Diff([plan], [observation], new Dictionary<string, bool> { [observation.Name] = true });

        Assert.Equal(ServiceActions.Recreate, Assert.Single(diff.Changes).Action);
    }

    [Fact]
    public void ChangedMountsAndPortsForceRecreate()
    {
        var plan = ReconcileTestData.Plan(volumes: ["data:/var/lib/data"], ports: ["8080:8080"]);
        var observation = ReconcileTestData.Observation(
            plan,
            mounts: [new MountObservation("volume", "other", "/var/lib/data", false)]);
        var diff = ServiceDiffer.Diff([plan], [observation], new Dictionary<string, bool> { [observation.Name] = true });

        Assert.Equal(ServiceActions.Recreate, Assert.Single(diff.Changes).Action);
    }

    [Fact]
    public void DuplicatesAndUnknownServicesBecomeOrphans()
    {
        var plan = ReconcileTestData.Plan();
        var canonical = ReconcileTestData.Observation(plan);
        var duplicate = ReconcileTestData.Observation(plan, name: "narwhal-shop-api-old");
        var unknown = ReconcileTestData.Observation(
            ReconcileTestData.Plan(name: "web"),
            name: "narwhal-shop-web");
        var unlabeled = ReconcileTestData.Observation(
            ReconcileTestData.Plan(name: "ghost"),
            name: "narwhal-shop-ghost",
            project: "",
            service: "");

        var diff = ServiceDiffer.Diff(
            [plan],
            [canonical, duplicate, unknown, unlabeled],
            new Dictionary<string, bool> { [canonical.Name] = true });

        Assert.Equal(ServiceActions.None, Assert.Single(diff.Changes).Action);
        Assert.Equal(
            ["narwhal-shop-api-old", "narwhal-shop-ghost", "narwhal-shop-web"],
            diff.Orphans.Select(o => o.Name));
    }

    [Fact]
    public void ACanonicalNameWithAMissingContainerIsCreatedAndTheDuplicateIsAnOrphan()
    {
        var plan = ReconcileTestData.Plan();
        var duplicate = ReconcileTestData.Observation(plan, name: "narwhal-shop-api-old");

        var diff = ServiceDiffer.Diff([plan], [duplicate], new Dictionary<string, bool> { [duplicate.Name] = true });

        Assert.Equal(ServiceActions.Create, Assert.Single(diff.Changes).Action);
        Assert.Equal(duplicate.Name, Assert.Single(diff.Orphans).Name);
    }

    [Fact]
    public void AnEphemeralHostPortMatchesAnyObservedHostPort()
    {
        var plan = ReconcileTestData.Plan(ports: ["8080"]);
        var observation = ReconcileTestData.Observation(plan, ports: ["8080/tcp:49152"]);

        var diff = ServiceDiffer.Diff([plan], [observation], new Dictionary<string, bool> { [observation.Name] = true });

        Assert.Equal(ServiceActions.None, Assert.Single(diff.Changes).Action);
    }

    [Fact]
    public void AnExplicitHostPortStillRequiresTheExactBinding()
    {
        var plan = ReconcileTestData.Plan(ports: ["8080:8080"]);
        var observation = ReconcileTestData.Observation(plan, ports: ["8080/tcp:49152"]);

        var diff = ServiceDiffer.Diff([plan], [observation], new Dictionary<string, bool> { [observation.Name] = true });

        Assert.Equal(ServiceActions.Recreate, Assert.Single(diff.Changes).Action);
    }

    [Fact]
    public void AnExistingBindMountFileIsAccepted()
    {
        var plan = ReconcileTestData.Plan(volumes: ["/srv/narwhal/config.json:/etc/config.json:ro"]);

        ReconcileEngine.EnsureBindMounts([plan], path => path == "/srv/narwhal/config.json");

        var error = Assert.Throws<ReconcileException>(() =>
            ReconcileEngine.EnsureBindMounts([plan], _ => false));
        Assert.Equal("bind_mount_missing", error.Code);
    }

    [Fact]
    public void TheConfigHashIncludesTheSecretVersionButNotTheSecretValue()
    {
        var service = Spec("api");
        var image = new ImageSpec { Repository = ReconcileTestData.Repository, Platform = "linux/amd64", Size = 1 };
        var first = new ResolvedSecret("PASSWORD", "shop-api-PASSWORD", "file:v1", "one");
        var second = new ResolvedSecret("PASSWORD", "shop-api-PASSWORD", "file:v1", "two");
        var third = new ResolvedSecret("PASSWORD", "shop-api-PASSWORD", "file:v2", "one");

        var firstHash = ServicePlanner.ConfigHash(service, image, [first]);
        var secondHash = ServicePlanner.ConfigHash(service, image, [second]);
        var thirdHash = ServicePlanner.ConfigHash(service, image, [third]);

        Assert.Equal(firstHash, secondHash);
        Assert.NotEqual(firstHash, thirdHash);
        Assert.DoesNotContain("one", thirdHash, StringComparison.Ordinal);
    }

    private static ServiceSpec Spec(string name, IReadOnlyList<string>? dependsOn = null) => new()
    {
        Image = CloudArtifactTestData.ImageDigest,
        Ports = [],
        Volumes = [],
        DependsOn = dependsOn ?? [],
        Restart = name == "cache" ? "always" : null,
    };
}
