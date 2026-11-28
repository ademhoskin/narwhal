using Narwhal.Config;

namespace Narwhal.Runtime;

public sealed record ContainerInfo(
    string Name,
    string State,
    string Status,
    string Image,
    string Ports,
    string Health,
    string Id);

public static class ContainerNaming
{
    public const string ProjectLabel = "io.narwhal.project";
    public const string ServiceLabel = "io.narwhal.service";
    public const string ManifestLabel = "io.narwhal.manifest";
    public const string ImageIdLabel = "io.narwhal.image-id";

    public static string ContainerName(string project, string service) => $"{project}-{service}";

    public static string DefaultNetwork(string project) => $"narwhal-{project}";
}

/// <summary>Container operations the CLI needs, independent of the container runtime.</summary>
public interface IContainerClient
{
    Task EnsureNetworkAsync(string project, string network, CancellationToken cancellationToken);
    Task EnsureVolumeAsync(string project, string name, CancellationToken cancellationToken);
    Task<string> LoadImageAsync(string artifactPath, string reference, CancellationToken cancellationToken);
    Task<string?> ImageIdAsync(string reference, CancellationToken cancellationToken);
    Task<bool> ImageExistsAsync(string reference, CancellationToken cancellationToken);
    Task PullAsync(string reference, CancellationToken cancellationToken);
    Task<bool> ContainerExistsAsync(string name, CancellationToken cancellationToken);
    Task RemoveContainerAsync(string name, CancellationToken cancellationToken);

    Task RunAsync(Manifest manifest, ServiceSpec service, string network, string containerName,
        string imageReference, string imageId, CancellationToken cancellationToken);

    Task<string?> HealthAsync(string containerName, CancellationToken cancellationToken);
    Task<string?> StateAsync(string containerName, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContainerInfo>> ListAsync(string project, CancellationToken cancellationToken);

    Task LogsAsync(IReadOnlyList<string> containers, bool follow, int? tail, bool timestamps,
        CancellationToken cancellationToken);

    Task PushByIdAsync(string imageId, string destination, CancellationToken cancellationToken);
    Task TagAsync(string imageId, string reference, CancellationToken cancellationToken);
    Task CreateManifestAsync(string reference, IReadOnlyList<string> imageIds, CancellationToken cancellationToken);
    Task PushManifestAsync(string index, string destination, CancellationToken cancellationToken);
    Task RemoveManifestAsync(string reference, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ProjectVolumesAsync(string project, CancellationToken cancellationToken);
    Task RemoveVolumeAsync(string name, CancellationToken cancellationToken);
}
