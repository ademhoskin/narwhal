using Narwhal.Cli;
using Narwhal.Config;
using Narwhal.Runtime;
using Xunit;

namespace Narwhal.Tests;

public class DockerClientTests
{
    [Fact]
    public async Task EnsureNetworkInspectsThenCreatesWithTheProjectLabel()
    {
        var docker = FakeDocker.Create("""
            if [ "$1" = "network" ] && [ "$2" = "inspect" ]; then exit 1; fi
            exit 0
            """);

        await docker.Client.EnsureNetworkAsync("shop", "narwhal-shop", CancellationToken.None);

        Assert.Equal(
            [
                ["network", "inspect", "narwhal-shop"],
                ["network", "create", "--label", "io.narwhal.project=shop", "narwhal-shop"],
            ],
            docker.Invocations());
    }

    [Fact]
    public async Task EnsureVolumeInspectsThenCreatesWithTheProjectLabel()
    {
        var docker = FakeDocker.Create("""
            if [ "$1" = "volume" ] && [ "$2" = "inspect" ]; then exit 1; fi
            exit 0
            """);

        await docker.Client.EnsureVolumeAsync("shop", "shop-data", CancellationToken.None);

        Assert.Equal(
            [
                ["volume", "inspect", "shop-data"],
                ["volume", "create", "--label", "io.narwhal.project=shop", "shop-data"],
            ],
            docker.Invocations());
    }

    [Fact]
    public async Task RunPinsEveryDockerFlagFromTheManifest()
    {
        var docker = FakeDocker.Create("exit 0");
        var manifest = Manifest();
        var service = new ServiceSpec
        {
            Name = "api",
            Ports = ["8080:80"],
            Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["B"] = "2", ["A"] = "1" },
            Volumes = ["shop-data:/data"],
            User = "1000",
            Workdir = "/app",
            Entrypoint = "/bin/api",
            Healthcheck = "true",
            Command = ["serve", "--flag"],
        };

        await docker.Client.RunAsync(
            manifest, service, "narwhal-shop", "shop-api", "shop/api:latest", "sha256:abc", CancellationToken.None);

        Assert.Equal(
            [[
                "run", "--detach",
                "--name", "shop-api",
                "--network", "narwhal-shop",
                "--restart", "unless-stopped",
                "--label", "io.narwhal.project=shop",
                "--label", "io.narwhal.service=api",
                "--label", $"io.narwhal.manifest={manifest.ContentHash}",
                "--label", "io.narwhal.image-id=sha256:abc",
                "--publish", "8080:80",
                "--env", "A=1",
                "--env", "B=2",
                "--volume", "shop-data:/data",
                "--user", "1000",
                "--workdir", "/app",
                "--entrypoint", "/bin/api",
                "--health-cmd", "true",
                "--health-interval", "5s",
                "--health-retries", "24",
                "--health-timeout", "3s",
                "--health-start-period", "2s",
                "shop/api:latest",
                "serve", "--flag",
            ]],
            docker.Invocations());
    }

    [Fact]
    public async Task LoadParsesADockerImageNameAndRetagsIt()
    {
        var docker = FakeDocker.Create("""
            if [ "$1" = "load" ]; then echo "Loaded image: docker.io/library/foo:latest"; fi
            if [ "$1" = "image" ]; then echo "sha256:loaded"; fi
            exit 0
            """);

        var id = await docker.Client.LoadImageAsync("/tmp/image.tar", "shop/api:latest", CancellationToken.None);

        Assert.Equal("sha256:loaded", id);
        Assert.Equal(
            [
                ["load", "-i", "/tmp/image.tar"],
                ["tag", "docker.io/library/foo:latest", "shop/api:latest"],
                ["image", "inspect", "--format", "{{.Id}}", "shop/api:latest"],
            ],
            docker.Invocations());
    }

    [Fact]
    public async Task LoadParsesALoadedImageIdAndRetagsIt()
    {
        var docker = FakeDocker.Create("""
            if [ "$1" = "load" ]; then echo "Loaded image ID: sha256:loaded"; fi
            if [ "$1" = "image" ]; then echo "sha256:loaded"; fi
            exit 0
            """);

        var id = await docker.Client.LoadImageAsync("/tmp/image.tar", "shop/api:latest", CancellationToken.None);

        Assert.Equal("sha256:loaded", id);
        Assert.Equal(
            [
                ["load", "-i", "/tmp/image.tar"],
                ["tag", "sha256:loaded", "shop/api:latest"],
                ["image", "inspect", "--format", "{{.Id}}", "shop/api:latest"],
            ],
            docker.Invocations());
    }

    [Fact]
    public async Task ImageQueriesUseDockerInspect()
    {
        var docker = FakeDocker.Create("""
            if [ "$1" = "image" ] && [ "$2" = "inspect" ]; then
              if [ "$3" = "missing:tag" ] || [ "$5" = "missing:tag" ]; then exit 1; fi
            fi
            if [ "$1" = "image" ]; then echo "sha256:abc"; fi
            exit 0
            """);

        Assert.Equal("sha256:abc", await docker.Client.ImageIdAsync("shop/api:latest", CancellationToken.None));
        Assert.True(await docker.Client.ImageExistsAsync("shop/api:latest", CancellationToken.None));
        Assert.False(await docker.Client.ImageExistsAsync("missing:tag", CancellationToken.None));
    }

    [Fact]
    public async Task ListParsesDockerNdjsonAndHealthStatus()
    {
        var docker = FakeDocker.Create("""
            if [ "$1" = "ps" ]; then
              echo '{"ID":"abc","Names":"shop-api","State":"running","Status":"Up 2 minutes (healthy)","Image":"shop/api:latest","Ports":"0.0.0.0:8080->80/tcp","HealthStatus":"healthy"}'
              echo '{"ID":"def","Names":"shop-db","State":"exited","Status":"Exited (0) 1 minute ago","Image":"postgres:17","Ports":"","HealthStatus":"none"}'
            fi
            exit 0
            """);

        var containers = await docker.Client.ListAsync("shop", CancellationToken.None);

        Assert.Equal(
            [
                new ContainerInfo("shop-api", "running", "Up 2 minutes (healthy)", "shop/api:latest", "0.0.0.0:8080->80/tcp", "healthy", "abc"),
                new ContainerInfo("shop-db", "exited", "Exited (0) 1 minute ago", "postgres:17", "", "", "def"),
            ],
            containers);
        Assert.Equal(
            [["ps", "--all", "--filter", "label=io.narwhal.project=shop", "--format", "json"]],
            docker.Invocations());
    }

    [Fact]
    public async Task LogsRunsOncePerContainerWithTheRequestedFlags()
    {
        var docker = FakeDocker.Create("exit 0");

        await docker.Client.LogsAsync(["shop-api", "shop-db"], follow: true, tail: 50, timestamps: true,
            CancellationToken.None);

        Assert.Equal(
            [
                ["logs", "--follow", "--tail", "50", "--timestamps", "shop-api"],
                ["logs", "--follow", "--tail", "50", "--timestamps", "shop-db"],
            ],
            docker.Invocations().OrderBy(a => a[^1], StringComparer.Ordinal));
    }

    [Fact]
    public async Task PushTagsTheImageIdAndPushesTheDestination()
    {
        var docker = FakeDocker.Create("exit 0");

        await docker.Client.PushByIdAsync("sha256:abc", "registry.example.com/shop/api:latest", CancellationToken.None);

        Assert.Equal(
            [
                ["tag", "sha256:abc", "registry.example.com/shop/api:latest"],
                ["push", "registry.example.com/shop/api:latest"],
            ],
            docker.Invocations());
    }

    [Fact]
    public async Task ManifestCreateAndPushRebuildTheListAtTheDestination()
    {
        var docker = FakeDocker.Create("exit 0");

        await docker.Client.CreateManifestAsync("localhost/narwhal-index/shop/api-abc:latest",
            ["sha256:amd64", "sha256:arm64"], CancellationToken.None);
        await docker.Client.PushManifestAsync("localhost/narwhal-index/shop/api-abc:latest",
            "localhost:5000/shop/api:latest", CancellationToken.None);

        Assert.Equal(
            [
                ["manifest", "create", "--amend", "localhost/narwhal-index/shop/api-abc:latest", "sha256:amd64", "sha256:arm64"],
                ["manifest", "create", "--amend", "--insecure", "localhost:5000/shop/api:latest", "sha256:amd64", "sha256:arm64"],
                ["manifest", "push", "--purge", "--insecure", "localhost:5000/shop/api:latest"],
                ["manifest", "rm", "localhost/narwhal-index/shop/api-abc:latest"],
            ],
            docker.Invocations());
    }

    [Fact]
    public async Task ManifestPushOmitsInsecureForARemoteRegistry()
    {
        var docker = FakeDocker.Create("exit 0");

        await docker.Client.CreateManifestAsync("index:latest", ["sha256:amd64"], CancellationToken.None);
        await docker.Client.PushManifestAsync("index:latest", "registry.example.com/shop/api:latest", CancellationToken.None);

        Assert.Equal(
            [
                ["manifest", "create", "--amend", "index:latest", "sha256:amd64"],
                ["manifest", "create", "--amend", "registry.example.com/shop/api:latest", "sha256:amd64"],
                ["manifest", "push", "--purge", "registry.example.com/shop/api:latest"],
                ["manifest", "rm", "index:latest"],
            ],
            docker.Invocations());
    }

    [Fact]
    public async Task StateAndHealthUseTheDockerInspectFormats()
    {
        var docker = FakeDocker.Create("""
            if [ "$1" = "inspect" ] && [ "$3" = "{{.State.Status}}" ]; then
              echo running
            elif [ "$1" = "inspect" ]; then
              echo healthy
            fi
            exit 0
            """);

        Assert.Equal("running", await docker.Client.StateAsync("shop-api", CancellationToken.None));
        Assert.Equal("healthy", await docker.Client.HealthAsync("shop-api", CancellationToken.None));
        Assert.Equal(
            [
                ["inspect", "--format", "{{.State.Status}}", "shop-api"],
                ["inspect", "--format", "{{if .State.Health}}{{.State.Health.Status}}{{end}}", "shop-api"],
            ],
            docker.Invocations());
    }

    [Fact]
    public async Task ContainerInspectDrivesExistsAndForceRemoval()
    {
        var docker = FakeDocker.Create("""
            if [ "$1" = "container" ] && [ "$2" = "inspect" ] && [ "$3" = "missing" ]; then exit 1; fi
            exit 0
            """);

        Assert.True(await docker.Client.ContainerExistsAsync("shop-api", CancellationToken.None));
        Assert.False(await docker.Client.ContainerExistsAsync("missing", CancellationToken.None));
        await docker.Client.RemoveContainerAsync("shop-api", CancellationToken.None);
        await docker.Client.RemoveContainerAsync("missing", CancellationToken.None);

        Assert.Equal(
            [
                ["container", "inspect", "shop-api"],
                ["container", "inspect", "missing"],
                ["container", "inspect", "shop-api"],
                ["rm", "--force", "shop-api"],
                ["container", "inspect", "missing"],
            ],
            docker.Invocations());
    }

    [Fact]
    public async Task VolumeListAndRemovalUseDockerVolumeCommands()
    {
        var docker = FakeDocker.Create("""
            if [ "$1" = "volume" ] && [ "$2" = "ls" ]; then printf 'shop-data\nshop-logs\n'; fi
            exit 0
            """);

        Assert.Equal(
            ["shop-data", "shop-logs"],
            await docker.Client.ProjectVolumesAsync("shop", CancellationToken.None));
        await docker.Client.RemoveVolumeAsync("shop-data", CancellationToken.None);

        Assert.Equal(
            [
                ["volume", "ls", "--filter", "label=io.narwhal.project=shop", "--format", "{{.Name}}"],
                ["volume", "rm", "--force", "shop-data"],
            ],
            docker.Invocations());
    }

    private static Manifest Manifest() => new()
    {
        Name = "shop",
        Directory = "/tmp/shop",
        Path = "/tmp/shop/narwhal.toml",
        RawText = "",
    };

    private sealed class FakeDocker
    {
        private readonly string _capture;

        private FakeDocker(DockerClient client, string capture)
        {
            Client = client;
            _capture = capture;
        }

        public DockerClient Client { get; }

        public static FakeDocker Create(string body)
        {
            var directory = Path.Combine(Path.GetTempPath(), "narwhal-docker-tests", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(directory);
            var capture = Path.Combine(directory, "capture");
            var script = Path.Combine(directory, "docker");
            File.WriteAllText(script, $$"""
                #!/bin/sh
                printf '%s\n' "===" >> "{{capture}}"
                for arg in "$@"; do printf '%s\n' "$arg" >> "{{capture}}"; done
                {{body}}
                """);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var reporter = ReconcileTestData.Reporter();
            return new FakeDocker(new DockerClient(new ProcessRunner(reporter, false), reporter, script), capture);
        }

        public List<string[]> Invocations() =>
            File.ReadAllText(_capture)
                .Split("===\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(chunk => chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();
    }
}
