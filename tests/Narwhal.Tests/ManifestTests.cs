using Narwhal.Config;
using Xunit;

namespace Narwhal.Tests;

public class ManifestTests
{
    private static string WriteManifest(string text)
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "narwhal.toml");
        File.WriteAllText(path, text);
        return path;
    }

    private static Manifest Load(string text) => ManifestLoader.Load(WriteManifest(text));

    [Fact]
    public void LoadsAFullManifest()
    {
        var manifest = Load("""
            name = "acme"

            [build]
            attr_prefix = "images"
            system = "aarch64-linux"

            [deploy]
            network = "acme-net"
            restart = "always"

            [[service]]
            name = "api"
            image = "registry.example.com/acme/api"
            tag = "1.2.3"
            ports = ["8080:8080", "9090:9090/udp"]
            command = ["/bin/api", "serve"]
            env = { LOG_LEVEL = "info", WORKERS = 4 }
            depends_on = ["db"]

            [service.healthcheck]
            command = "curl -fsS localhost:8080/healthz"
            interval = 2
            retries = 30

            [[service]]
            name = "db"
            prebuilt = true
            image = "docker.io/library/postgres"
            tag = "17-alpine"
            volumes = ["acme-db:/var/lib/postgresql/data"]
            """);

        Assert.Equal("acme", manifest.Name);
        Assert.Equal("acme-net", manifest.Deploy.Network);
        Assert.Equal(2, manifest.Services.Count);

        var api = Assert.Single(manifest.Services, s => s.Name == "api");
        Assert.Equal("registry.example.com/acme/api:1.2.3", api.Reference(manifest));
        Assert.Equal("images.aarch64-linux.api", api.AttrPath(manifest));
        Assert.Equal(["8080:8080", "9090:9090/udp"], api.Ports);
        Assert.Equal(["db"], api.DependsOn);
        Assert.Equal("info", api.Env["LOG_LEVEL"]);
        Assert.Equal("4", api.Env["WORKERS"]);
        Assert.Equal(["db"], api.DependsOn);
        Assert.Equal("curl -fsS localhost:8080/healthz", api.Healthcheck);
        Assert.Equal(2, api.HealthInterval);
        Assert.Equal(30, api.HealthRetries);

        var db = Assert.Single(manifest.Services, s => s.Name == "db");
        Assert.True(db.Prebuilt);
        Assert.Equal("docker.io/library/postgres:17-alpine", db.Reference(manifest));
        Assert.Equal("always", api.RestartPolicy(manifest));
        Assert.Equal(["db", "api"], manifest.Ordered().Select(s => s.Name));
    }

    [Fact]
    public void DefaultsImageAndAttributeFromProjectName()
    {
        var manifest = Load("""
            name = "shop"

            [[service]]
            name = "api"
            """);

        var api = manifest.Services[0];
        Assert.Equal("shop/api:latest", api.Reference(manifest));
        Assert.Equal($"narwhal.images.{NixPlatform.ContainerSystem}.api", api.AttrPath(manifest));
        Assert.Equal("unless-stopped", api.RestartPolicy(manifest));
    }

    [Fact]
    public void DerivesProjectNameFromDirectory()
    {
        var path = WriteManifest("""
            [[service]]
            name = "api"
            """);
        var manifest = ManifestLoader.Load(path);
        Assert.Equal(Path.GetFileName(Path.GetDirectoryName(path)), manifest.Name);
    }

    [Fact]
    public void OrdersDependenciesBeforeDependents()
    {
        var manifest = Load("""
            name = "order"

            [[service]]
            name = "web"
            depends_on = ["api"]

            [[service]]
            name = "api"
            depends_on = ["db"]

            [[service]]
            name = "db"
            """);

        Assert.Equal(["db", "api", "web"], manifest.Ordered().Select(s => s.Name));
    }

    [Fact]
    public void RejectsDependencyCycles()
    {
        var error = Assert.Throws<ManifestException>(() => Load("""
            name = "cycle"

            [[service]]
            name = "a"
            depends_on = ["b"]

            [[service]]
            name = "b"
            depends_on = ["a"]
            """));

        Assert.Contains("dependency cycle", error.Message);
        Assert.Contains("a -> b", error.Message);
    }

    [Fact]
    public void RejectsUnknownKeys()
    {
        var error = Assert.Throws<ManifestException>(() => Load("""
            name = "typo"

            [[service]]
            name = "api"
            prot = ["8080:8080"]
            """));

        Assert.Contains("unknown key 'service[0].prot'", error.Message);
        Assert.Contains("ports", error.Message);
    }

    [Fact]
    public void RejectsUnknownDependencies()
    {
        var error = Assert.Throws<ManifestException>(() => Load("""
            name = "missing"

            [[service]]
            name = "api"
            depends_on = ["db"]
            """));

        Assert.Contains("unknown service 'db'", error.Message);
    }

    [Fact]
    public void RejectsInvalidPorts()
    {
        var error = Assert.Throws<ManifestException>(() => Load("""
            name = "ports"

            [[service]]
            name = "api"
            ports = ["8080:99999"]
            """));

        Assert.Contains("invalid port mapping", error.Message);
    }

    [Fact]
    public void RejectsDuplicateServiceNames()
    {
        var error = Assert.Throws<ManifestException>(() => Load("""
            name = "dup"

            [[service]]
            name = "api"

            [[service]]
            name = "api"
            """));

        Assert.Contains("duplicate service 'api'", error.Message);
    }

    [Fact]
    public void RejectsManifestWithoutServices()
    {
        var error = Assert.Throws<ManifestException>(() => Load("name = \"empty\"\n"));
        Assert.Contains("no services defined", error.Message);
    }

    [Fact]
    public void DiscoversManifestUpwards()
    {
        var path = WriteManifest("""
            name = "discover"

            [[service]]
            name = "api"
            """);

        var nested = Path.Combine(Path.GetDirectoryName(path)!, "a", "b", "c");
        Directory.CreateDirectory(nested);

        Assert.Equal(path, ManifestLoader.Discover(nested));
    }
}
