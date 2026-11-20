using System.Text.RegularExpressions;

namespace Narwhal.Config;

/// <summary>A user-defined service container: a project name plus its services.</summary>
public sealed class Manifest
{
    public required string Name { get; init; }
    public required string Directory { get; init; }
    public required string Path { get; init; }
    public string Flake { get; init; } = ".";
    public BuildOptions Build { get; init; } = new();
    public DeployOptions Deploy { get; init; } = new();
    public List<ServiceSpec> Services { get; init; } = [];
    public required string RawText { get; init; }

    public string LockPath { get; set; } = "";
    public string ContentHash => Hashing.Sha256Hex(RawText);

    public ServiceSpec? Find(string name) => Services.FirstOrDefault(s => s.Name == name);

    /// <summary>Topologically ordered services, dependencies first.</summary>
    public IReadOnlyList<ServiceSpec> Ordered()
    {
        var byName = Services.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var ordered = new List<ServiceSpec>(Services.Count);
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = visiting, 2 = done

        void Visit(ServiceSpec service, Stack<string> path)
        {
            if (state.TryGetValue(service.Name, out var current))
            {
                if (current == 2)
                    return;
                var cycle = string.Join(" -> ", path.Reverse().Append(service.Name));
                throw new ManifestException($"dependency cycle detected: {cycle}");
            }

            state[service.Name] = 1;
            path.Push(service.Name);
            foreach (var dependency in service.DependsOn)
                Visit(byName[dependency], path);
            path.Pop();
            state[service.Name] = 2;
            ordered.Add(service);
        }

        foreach (var service in Services)
            Visit(service, new Stack<string>());

        return ordered;
    }
}

public sealed class BuildOptions
{
    /// <summary>Flake attribute prefix that holds the image set. Default: narwhal.images.</summary>
    public string AttrPrefix { get; init; } = "narwhal.images";

    /// <summary>
    /// System whose images to build. Defaults to the current architecture with a
    /// linux OS, since that is what containers run.
    /// </summary>
    public string System { get; init; } = NixPlatform.ContainerSystem;

    /// <summary>Extra arguments appended to every <c>nix build</c> invocation.</summary>
    public List<string> ExtraArgs { get; init; } = [];

    /// <summary>Pass <c>--impure</c> to nix (needed by flakes that read the environment).</summary>
    public bool Impure { get; init; }
}

public sealed class DeployOptions
{
    /// <summary>Podman network for the project. Default: narwhal-&lt;project&gt;.</summary>
    public string? Network { get; init; }

    /// <summary>Default restart policy for services. Default: unless-stopped.</summary>
    public string Restart { get; init; } = "unless-stopped";

    /// <summary>How long to wait for a dependency to become ready, in seconds.</summary>
    public int WaitSeconds { get; init; } = 60;
}

public sealed class ServiceSpec
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Flake { get; init; }

    /// <summary>Flake attribute path for the image, e.g. narwhal.images.api.</summary>
    public string? Attr { get; init; }

    /// <summary>Image repository without tag. Default: &lt;project&gt;/&lt;service&gt;.</summary>
    public string? Image { get; init; }

    public string Tag { get; init; } = "latest";

    /// <summary>Skip nix builds; pull the image from a registry instead.</summary>
    public bool Prebuilt { get; init; }

    public List<string> Ports { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.Ordinal);
    public List<string> Volumes { get; init; } = [];
    public List<string> Command { get; init; } = [];
    public string? Entrypoint { get; init; }
    public string? User { get; init; }
    public string? Workdir { get; init; }
    public string? Restart { get; init; }
    public List<string> DependsOn { get; init; } = [];
    public string? Healthcheck { get; init; }
    public int HealthInterval { get; init; } = 5;
    public int HealthRetries { get; init; } = 24;
    public int HealthTimeout { get; init; } = 3;
    public int StopTimeout { get; init; } = 10;

    public string Repository(Manifest manifest) => Image ?? $"{manifest.Name}/{Name}";
    public string Reference(Manifest manifest) => $"{Repository(manifest)}:{Tag}";
    public string AttrPath(Manifest manifest) =>
        Attr ?? $"{manifest.Build.AttrPrefix}.{manifest.Build.System}.{Name}";
    public string FlakeRef(Manifest manifest) => Flake ?? manifest.Flake;
    public string RestartPolicy(Manifest manifest) => Restart ?? manifest.Deploy.Restart;
}

public sealed class ManifestException(string message) : Exception(message);

/// <summary>Nix system strings for the machine narwhal runs on.</summary>
public static class NixPlatform
{
    public static string ContainerSystem
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("NARWHAL_SYSTEM");
            if (!string.IsNullOrWhiteSpace(overridden))
                return overridden;

            var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
            var arch = architecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => "x86_64",
                System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
                System.Runtime.InteropServices.Architecture.Arm => "armv7l",
                System.Runtime.InteropServices.Architecture.X86 => "i686",
                _ => "x86_64",
            };

            // Containers always run on linux, even when driven from macOS.
            return $"{arch}-linux";
        }
    }
}

internal static partial class Hashing
{
    public static string Sha256Hex(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9._-]{0,61}[a-z0-9])?$")]
    public static partial Regex ServiceName();

    public static bool IsValidServiceName(string name) => ServiceName().IsMatch(name);
}
