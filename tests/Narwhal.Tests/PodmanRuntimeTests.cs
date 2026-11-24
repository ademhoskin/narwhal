using Narwhal.Cli;
using Narwhal.Reconcile;
using Xunit;

namespace Narwhal.Tests;

public class PodmanRuntimeTests
{
    [Fact]
    public async Task AcceptsAMatchingRepoDigest()
    {
        var runtime = RuntimeWithImageJson(
            """{"Id":"sha256:local","Digest":"sha256:other","RepoDigests":["registry.example.com/shop/api@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]}""");

        Assert.True(await runtime.ImageMatchesDigestAsync(
            "registry.example.com/shop/api@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "registry.example.com/shop/api",
            Digest('a'),
            CancellationToken.None));
    }

    [Fact]
    public async Task AcceptsAMatchingDigestField()
    {
        var runtime = RuntimeWithImageJson(
            """{"Id":"sha256:local","Digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","RepoDigests":[]}""");

        Assert.True(await runtime.ImageMatchesDigestAsync(
            "registry.example.com/shop/api@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "registry.example.com/shop/api",
            Digest('a'),
            CancellationToken.None));
    }

    [Fact]
    public async Task NeverComparesTheLocalImageId()
    {
        var runtime = RuntimeWithImageJson(
            """{"Id":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Digest":"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","RepoDigests":[]}""");

        Assert.False(await runtime.ImageMatchesDigestAsync(
            "registry.example.com/shop/api@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "registry.example.com/shop/api",
            Digest('a'),
            CancellationToken.None));
    }

    [Fact]
    public async Task PullWritesAndRemovesA0600AuthFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-reconcile", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var capture = Path.Combine(directory, "capture");
        var script = Path.Combine(directory, "podman");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            echo "$@" > "{{capture}}.args"
            while [ $# -gt 0 ]; do
              if [ "$1" = "--authfile" ]; then
                cp "$2" "{{capture}}.auth"
                stat -c '%a' "$2" > "{{capture}}.mode"
                exit 0
              fi
              shift
            done
            exit 0
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var authDirectory = Path.Combine(directory, "auth");
        Directory.CreateDirectory(authDirectory);
        var reporter = ReconcileTestData.Reporter();
        var runtime = new PodmanRuntime(
            new ProcessRunner(reporter, false),
            reporter,
            script,
            new ImagePullAuth("registry.example.com", new RegistryAuthConfig
            {
                Username = "image-user",
                Password = "image-password",
            }),
            authDirectory);

        await runtime.PullAsync("registry.example.com/shop/api@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", CancellationToken.None);

        var arguments = File.ReadAllText(capture + ".args");
        Assert.Contains("--authfile", arguments, StringComparison.Ordinal);
        Assert.Contains("registry.example.com/shop/api@", arguments, StringComparison.Ordinal);
        Assert.Equal("600", File.ReadAllText(capture + ".mode").Trim());
        Assert.Empty(Directory.GetFiles(authDirectory));

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(capture + ".auth"));
        var encoded = document.RootElement
            .GetProperty("auths")
            .GetProperty("registry.example.com")
            .GetProperty("auth")
            .GetString();
        Assert.Equal(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("image-user:image-password")),
            encoded);
    }

    [Fact]
    public async Task SweepsStaleAuthFilesBeforePulling()
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-reconcile", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "podman");
        File.WriteAllText(script, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var authDirectory = Path.Combine(directory, "auth");
        Directory.CreateDirectory(authDirectory);
        var stale = Path.Combine(authDirectory, "auth-2147483647-deadbeef.json");
        File.WriteAllText(stale, """{"auths":{}}""");

        var reporter = ReconcileTestData.Reporter();
        var runtime = new PodmanRuntime(
            new ProcessRunner(reporter, false),
            reporter,
            script,
            authFileDirectory: authDirectory);

        await runtime.PullAsync("registry.example.com/shop/api@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", CancellationToken.None);

        Assert.False(File.Exists(stale));
        Assert.Empty(Directory.GetFiles(authDirectory));
    }

    [Fact]
    public async Task AlwaysPassesAControlledAuthFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-reconcile", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var capture = Path.Combine(directory, "capture");
        var script = Path.Combine(directory, "podman");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            echo "$@" > "{{capture}}.args"
            while [ $# -gt 0 ]; do
              if [ "$1" = "--authfile" ]; then
                cp "$2" "{{capture}}.auth"
                exit 0
              fi
              shift
            done
            exit 1
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var authDirectory = Path.Combine(directory, "auth");
        Directory.CreateDirectory(authDirectory);
        var reporter = ReconcileTestData.Reporter();
        var runtime = new PodmanRuntime(
            new ProcessRunner(reporter, false),
            reporter,
            script,
            authFileDirectory: authDirectory);

        await runtime.PullAsync("registry.example.com/shop/api@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", CancellationToken.None);

        Assert.Contains("--authfile", File.ReadAllText(capture + ".args"), StringComparison.Ordinal);
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(capture + ".auth"));
        Assert.Empty(document.RootElement.GetProperty("auths").EnumerateObject());
        Assert.Empty(Directory.GetFiles(authDirectory));
    }

    private static PodmanRuntime RuntimeWithImageJson(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-reconcile", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "podman");
        File.WriteAllText(script, "#!/bin/sh\ncat <<'JSON'\n" + json + "\nJSON\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var reporter = ReconcileTestData.Reporter();
        return new PodmanRuntime(new ProcessRunner(reporter, false), reporter, script);
    }

    private static string Digest(char value) => "sha256:" + new string(value, 64);
}
