using System.Net;
using System.Text;
using Narwhal.Cli;
using Narwhal.Cloud;
using Narwhal.Reconcile;

namespace Narwhal.Tests;

internal sealed class TestArtifact
{
    public required byte[] Payload { get; init; }
    public required byte[] Signature { get; init; }
    public required RegistryManifest Manifest { get; init; }
    public required string PayloadDigest { get; init; }
}

internal static class ReconcileTestData
{
    public const string Repository = "registry.example.com/shop/api";
    public const string Project = "shop";
    public const string Desired = "shop/state/aws-us-east-1-amd64-1a2b3c4d/desired";

    public static TestArtifact Artifact(string payloadJson, params byte[][] seeds)
    {
        if (seeds.Length == 0)
            seeds = [CloudArtifactTestData.Seed1];

        var payload = CloudArtifactTestData.Canonicalize(payloadJson);
        var signature = CloudArtifactTestData.Envelope(payload, seeds);
        var payloadDigest = CloudEncoding.Sha256Digest(payload);
        var signatureDigest = CloudEncoding.Sha256Digest(signature);

        var manifestJson =
            "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\"," +
            "\"config\":{\"mediaType\":\"application/vnd.oci.empty.v1+json\",\"digest\":\"" +
            CloudEncoding.Sha256Digest(Encoding.UTF8.GetBytes("{}")) + "\",\"size\":2}," +
            "\"layers\":[" +
            $"{{\"mediaType\":\"{DesiredArtifactLayout.PayloadMediaType}\",\"digest\":\"{payloadDigest}\",\"size\":{payload.Length}}}," +
            $"{{\"mediaType\":\"{DesiredArtifactLayout.SignatureMediaType}\",\"digest\":\"{signatureDigest}\",\"size\":{signature.Length}}}" +
            "]}";
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        var layers = OciManifestParser.Parse(manifestBytes, Repository, "test");
        var digest = CloudEncoding.Sha256Digest(manifestBytes);

        return new TestArtifact
        {
            Payload = payload,
            Signature = signature,
            PayloadDigest = payloadDigest,
            Manifest = new RegistryManifest(digest, manifestBytes, layers, DesiredArtifactLayout.Select(layers)),
        };
    }

    public static AgentConfig Config(
        string stateDirectory,
        string? desiredRepository = null,
        string project = Project) => new()
    {
        Project = project,
        ImageRegistry = "registry.example.com",
        StateRegistry = "state.example.net",
        DesiredRepository = desiredRepository ?? Desired,
        Target = new TargetConfig
        {
            Provider = "aws",
            Region = "us-east-1",
            Arch = "amd64",
            Selector = new Dictionary<string, string>(StringComparer.Ordinal) { ["stack"] = "prod" },
        },
        StateDirectory = stateDirectory,
        TrustDirectory = Path.Combine(stateDirectory, "boot-trust"),
        StatusKeyPath = Path.Combine(stateDirectory, "status-key"),
        LockTimeoutSeconds = 0,
    };

    public static string CreateStateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-reconcile", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "trust.json"),
            CloudArtifactTestData.TrustJson(CloudArtifactTestData.DefaultTrustEntry(CloudArtifactTestData.Seed1)));
        return directory;
    }

    public static string CreateBootPins(string parent)
    {
        var directory = Path.Combine(parent, "boot-trust");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, CloudArtifactTestData.KeyId1 + ".pub"),
            $"narwhal-ed25519-v1 {CloudArtifactTestData.PublicKey1Base64}\nnot_after=2027-11-24T00:00:00Z\n");
        return directory;
    }

    public static Reporter Reporter() =>
        new(false, false, false, new StringWriter(), new StringWriter());

    public static ServicePlan Plan(
        string name = "api",
        string generationHash = "",
        string imageDigest = CloudArtifactTestData.ImageDigest,
        string repository = Repository,
        IReadOnlyList<string>? ports = null,
        IReadOnlyList<string>? volumes = null,
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyList<ResolvedSecret>? secrets = null,
        IReadOnlyList<string>? dependsOn = null)
    {
        var spec = new ServiceSpec
        {
            Image = imageDigest,
            Ports = ports ?? ["8080:8080"],
            Volumes = volumes ?? [],
            DependsOn = dependsOn ?? [],
            Env = env,
        };
        var image = new ImageSpec { Repository = repository, Platform = "linux/amd64", Size = 1 };
        var resolved = secrets ?? [];
        return new ServicePlan
        {
            Name = name,
            Spec = spec,
            Image = image,
            ImageReference = $"{repository}@{imageDigest}",
            ConfigHash = generationHash.Length == 0
                ? ServicePlanner.ConfigHash(spec, image, resolved)
                : generationHash,
            Secrets = resolved,
        };
    }

    public static ContainerObservation Observation(
        ServicePlan plan,
        long generation = 1,
        string? name = null,
        string state = "running",
        string restart = "unless-stopped",
        IReadOnlyList<string>? env = null,
        IReadOnlyList<string>? ports = null,
        IReadOnlyList<MountObservation>? mounts = null,
        string? project = Project,
        string? configHash = null,
        string? imageDigest = null,
        string? imageReference = null,
        string? service = null)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ReconcileLabels.Project] = project ?? "",
            [ReconcileLabels.Service] = service ?? plan.Name,
            [ReconcileLabels.Generation] = generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [ReconcileLabels.ConfigHash] = configHash ?? plan.ConfigHash,
            [ReconcileLabels.Image] = imageDigest ?? plan.Spec.Image,
        };

        return new ContainerObservation
        {
            Name = name ?? ReconcileNames.Container(project ?? Project, plan.Name),
            Labels = labels,
            State = state,
            ImageReference = imageReference ?? plan.ImageReference,
            RestartPolicy = restart,
            Environment = env ?? (plan.Spec.Env?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? []),
            Ports = ports ?? ServiceDiffer.DesiredPorts(plan.Spec).ToList(),
            Mounts = mounts ?? [],
            Health = state == "running" ? "healthy" : null,
        };
    }
}

internal sealed class FakeRegistry : IRegistryClient
{
    private readonly Dictionary<string, RegistryManifest> _manifests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    public bool Unavailable { get; set; }
    public bool AuthFailed { get; set; }
    public int ManifestRequests { get; private set; }
    public int BlobRequests { get; private set; }

    public void Add(string repository, string reference, TestArtifact artifact)
    {
        _manifests[Key(repository, reference)] = artifact.Manifest;
        _blobs[Key(repository, artifact.Manifest.Selection.Payload.Digest)] = artifact.Payload;
        _blobs[Key(repository, artifact.Manifest.Selection.Signature.Digest)] = artifact.Signature;
    }

    public Task<RegistryManifest> GetManifestAsync(string repository, string reference, CancellationToken cancellationToken)
    {
        ManifestRequests++;
        if (Unavailable)
            throw new RegistryException("simulated registry outage");
        if (AuthFailed)
            throw new RegistryException("simulated auth failure", auth: true);
        if (!_manifests.TryGetValue(Key(repository, reference), out var manifest))
            throw new RegistryException($"manifest not found: {repository}:{reference}");
        return Task.FromResult(manifest);
    }

    public Task<byte[]> GetBlobAsync(string repository, string digest, CancellationToken cancellationToken)
    {
        BlobRequests++;
        if (Unavailable)
            throw new RegistryException("simulated registry outage");
        if (!_blobs.TryGetValue(Key(repository, digest), out var bytes))
            throw new RegistryException($"blob not found: {repository}@{digest}");
        return Task.FromResult(bytes);
    }

    private static string Key(string repository, string reference) => repository + "|" + reference;
}

internal sealed class FakeRuntime : IContainerRuntime
{
    private readonly Dictionary<string, ContainerObservation> _containers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<string>> _health = new(StringComparer.Ordinal);
    private readonly HashSet<string> _images = new(StringComparer.Ordinal);

    public List<(string Project, ServicePlan Plan, long Generation)> Created { get; } = [];
    public List<string> Removed { get; } = [];
    public List<string> Stopped { get; } = [];
    public List<string> Pulled { get; } = [];
    public List<string> EnsuredVolumes { get; } = [];
    public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);
    public HashSet<string> BadImageDigests { get; } = new(StringComparer.Ordinal);
    public bool PullMakesImageAvailable { get; set; } = true;
    public Action? BeforeCreate { get; set; }

    public IReadOnlyCollection<ContainerObservation> Containers => _containers.Values;

    public void AddImage(string repository, string digest) => _images.Add(repository + "@" + digest);

    public void AddContainer(ContainerObservation observation) => _containers[observation.Name] = observation;

    public void SetHealth(string service, params string[] statuses)
    {
        var queue = new Queue<string>(statuses);
        _health[service] = queue;
    }

    public Task EnsureNetworkAsync(string project, string network, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EnsureVolumeAsync(string project, string name, CancellationToken cancellationToken)
    {
        EnsuredVolumes.Add(name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ContainerObservation>> ListAsync(string project, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ContainerObservation>>(_containers.Values.ToList());

    public Task<bool> ImageMatchesDigestAsync(string imageReference, string repository, string digest, CancellationToken cancellationToken) =>
        Task.FromResult(!BadImageDigests.Contains(digest) && _images.Contains(repository + "@" + digest));

    public Task PullAsync(string reference, CancellationToken cancellationToken)
    {
        Pulled.Add(reference);
        if (PullMakesImageAvailable)
            _images.Add(reference);
        return Task.CompletedTask;
    }

    public Task CreateAsync(string project, ServicePlan plan, string containerName, string network, long generation, CancellationToken cancellationToken)
    {
        BeforeCreate?.Invoke();
        Created.Add((project, plan, generation));
        _images.Add(plan.ImageReference);

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ReconcileLabels.Project] = project,
            [ReconcileLabels.Service] = plan.Name,
            [ReconcileLabels.Generation] = generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [ReconcileLabels.ConfigHash] = plan.ConfigHash,
            [ReconcileLabels.Image] = plan.Spec.Image,
        };
        _containers[containerName] = new ContainerObservation
        {
            Name = containerName,
            Labels = labels,
            State = "running",
            ImageReference = plan.ImageReference,
            RestartPolicy = plan.Spec.Restart ?? "unless-stopped",
            Environment = plan.Spec.Env?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? [],
            Ports = ServiceDiffer.DesiredPorts(plan.Spec).ToList(),
            Mounts = plan.Spec.Volumes.Select(volume =>
            {
                var parts = volume.Split(':');
                return new MountObservation("volume", parts[0], parts.Length > 1 ? parts[1] : "", false);
            }).ToList(),
            Health = plan.Spec.Health is null ? null : "healthy",
        };
        return Task.CompletedTask;
    }

    public Task StartAsync(string containerName, CancellationToken cancellationToken)
    {
        if (_containers.TryGetValue(containerName, out var observation))
            _containers[containerName] = Copy(observation, "running");
        return Task.CompletedTask;
    }

    public Task StopAsync(string containerName, CancellationToken cancellationToken)
    {
        Stopped.Add(containerName);
        if (_containers.TryGetValue(containerName, out var observation))
            _containers[containerName] = Copy(observation, "exited");
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string containerName, CancellationToken cancellationToken)
    {
        Removed.Add(containerName);
        _containers.Remove(containerName);
        return Task.CompletedTask;
    }

    public Task EnsureSecretAsync(string name, string value, CancellationToken cancellationToken)
    {
        Secrets[name] = value;
        return Task.CompletedTask;
    }

    public Task<string?> StateAsync(string containerName, CancellationToken cancellationToken) =>
        Task.FromResult(_containers.TryGetValue(containerName, out var observation) ? observation.State : null);

    public Task<string?> HealthAsync(string containerName, CancellationToken cancellationToken)
    {
        if (!_containers.TryGetValue(containerName, out var observation) || observation.Health is null)
            return Task.FromResult<string?>(null);
        if (observation.Labels.TryGetValue(ReconcileLabels.Service, out var service) &&
            _health.TryGetValue(service, out var queue) && queue.Count > 0)
        {
            var status = queue.Dequeue();
            _containers[containerName] = Copy(observation, observation.State, status);
            return Task.FromResult<string?>(status);
        }

        return Task.FromResult<string?>("healthy");
    }

    private static ContainerObservation Copy(ContainerObservation observation, string state, string? health = null) => new()
    {
        Name = observation.Name,
        Labels = observation.Labels,
        State = state,
        ImageReference = observation.ImageReference,
        RestartPolicy = observation.RestartPolicy,
        Environment = observation.Environment,
        Ports = observation.Ports,
        Mounts = observation.Mounts,
        Health = health ?? observation.Health,
    };
}

internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}

internal static class ReconcileAssert
{
    public static void NotContainsSecret(string text, string secretValue)
    {
        Xunit.Assert.DoesNotContain(secretValue, text, StringComparison.Ordinal);
    }
}
