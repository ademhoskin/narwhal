using Narwhal.Cli;
using Narwhal.Commands;
using Narwhal.Config;
using Narwhal.Podman;
using Narwhal.Runtime;
using Xunit;

namespace Narwhal.Tests;

public class ManifestRuntimeTests
{
    [Fact]
    public void DefaultsToPodman()
    {
        var manifest = Load("""
            [[service]]
            name = "api"
            """);

        Assert.Equal("podman", manifest.Runtime);
    }

    [Fact]
    public void LoadsDockerRuntime()
    {
        var manifest = Load("""
            runtime = "docker"

            [[service]]
            name = "api"
            """);

        Assert.Equal("docker", manifest.Runtime);
    }

    [Fact]
    public void RejectsAnUnknownRuntime()
    {
        var error = Assert.Throws<ManifestException>(() => Load("""
            runtime = "containerd"

            [[service]]
            name = "api"
            """));

        Assert.Contains("runtime", error.Message, StringComparison.Ordinal);
        Assert.Contains("containerd", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsANonStringRuntime()
    {
        var error = Assert.Throws<ManifestException>(() => Load("""
            runtime = 1

            [[service]]
            name = "api"
            """));

        Assert.Contains("runtime", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextBuildsTheClientNamedByTheManifest()
    {
        var directory = WriteManifestDirectory("""
            runtime = "docker"

            [[service]]
            name = "api"
            """);
        var docker = CommandContext.Create(new CliOptions { Directory = directory });
        Assert.IsType<DockerClient>(docker.Containers);

        directory = WriteManifestDirectory("""
            [[service]]
            name = "api"
            """);
        var podman = CommandContext.Create(new CliOptions { Directory = directory });
        Assert.IsType<PodmanClient>(podman.Containers);
    }

    private static Manifest Load(string text) => ManifestLoader.Load(WriteManifestDirectory(text) + "/narwhal.toml");

    private static string WriteManifestDirectory(string text)
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "narwhal.toml"), text);
        return directory;
    }
}
