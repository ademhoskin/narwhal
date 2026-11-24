using Narwhal.Config;
using Xunit;

namespace Narwhal.Tests;

public class LockFileTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "narwhal-tests", Guid.NewGuid().ToString("n"), "narwhal.lock.json");

    [Fact]
    public void SavesAndLoadsVersionTwoDeterministically()
    {
        var path = TempPath();
        var lockFile = new LockFile
        {
            Narwhal = "0.1.0",
            ManifestSha256 = "abc",
        };
        var web = new LockedService { Flake = ".", Image = "shop/web", Tag = "latest" };
        web.Platforms["x86_64-linux"] = new LockedPlatform
        {
            Platform = "linux/amd64",
            Attr = "narwhal.images.x86_64-linux.web",
            StorePath = "/nix/store/amd-web.tar.gz",
            NarHash = "sha256-amd",
            ImageId = "sha256:amd",
        };
        web.Platforms["i686-linux"] = new LockedPlatform
        {
            Platform = "linux/386",
            Attr = "narwhal.images.i686-linux.web",
            StorePath = "/nix/store/i686-web.tar.gz",
            NarHash = "sha256-i686",
            ImageId = "sha256:i686",
        };
        lockFile.Services["web"] = web;
        lockFile.Path = path;
        lockFile.Save();

        var text = File.ReadAllText(path);
        Assert.Contains("\"version\": 2", text);
        Assert.True(text.IndexOf("\"i686-linux\"", StringComparison.Ordinal)
            < text.IndexOf("\"x86_64-linux\"", StringComparison.Ordinal));

        var loaded = LockFile.Load(path);
        Assert.Equal(2, loaded.Version);
        var platform = loaded.Platform("web", "i686-linux");
        Assert.NotNull(platform);
        Assert.Equal("linux/386", platform!.Platform);
        Assert.Equal("sha256-i686", platform.NarHash);
        Assert.Equal(["i686-linux", "x86_64-linux"], loaded.Service("web")!.Platforms.Keys);
    }

    [Fact]
    public void MigratesVersionOneUsingTheAttrSystem()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "version": 1,
              "narwhal": "0.1.0",
              "manifest_sha256": "abc",
              "services": {
                "web": {
                  "attr": "narwhal.images.aarch64-linux.web",
                  "flake": ".",
                  "image": "shop/web",
                  "tag": "latest",
                  "store_path": "/nix/store/arm-web.tar.gz",
                  "nar_hash": "sha256-arm",
                  "image_id": "sha256:arm"
                }
              }
            }
            """);

        var loaded = LockFile.Load(path);

        Assert.Equal(2, loaded.Version);
        Assert.True(loaded.NeedsMigration);
        var platform = loaded.Platform("web", "aarch64-linux");
        Assert.NotNull(platform);
        Assert.Equal("linux/arm64", platform!.Platform);
        Assert.Equal("narwhal.images.aarch64-linux.web", platform.Attr);
        Assert.Equal("sha256-arm", platform.NarHash);

        loaded.Save();
        Assert.False(loaded.NeedsMigration);
        Assert.Contains("\"version\": 2", File.ReadAllText(path));
        Assert.Contains("\"aarch64-linux\"", File.ReadAllText(path));
    }

    [Fact]
    public void MigratesVersionOneAttrsWithoutASystemToTheHost()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "version": 1,
              "services": {
                "web": {
                  "attr": "images.web",
                  "store_path": "/nix/store/web.tar.gz",
                  "nar_hash": "sha256-web",
                  "image_id": "sha256:web"
                }
              }
            }
            """);

        var loaded = LockFile.Load(path);

        var platform = loaded.Platform("web", NixPlatform.ContainerSystem);
        Assert.NotNull(platform);
        Assert.Equal(NixPlatform.ToOciPlatform(NixPlatform.ContainerSystem), platform!.Platform);
    }

    [Fact]
    public void RejectsUnknownVersions()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "version": 3, "services": {} }""");

        var error = Assert.Throws<ManifestException>(() => LockFile.Load(path));
        Assert.Contains("version 3 is not supported", error.Message);
    }

    [Fact]
    public void MissingLockStartsEmptyAtTheCurrentVersion()
    {
        var loaded = LockFile.Load(TempPath());
        Assert.Equal(LockFile.CurrentVersion, loaded.Version);
        Assert.Empty(loaded.Services);
    }

    [Fact]
    public void CoverageRequiresPlatformAndImageIdForEverySystem()
    {
        var manifest = ManifestLoader.Load(WriteManifest("""
            name = "coverage"

            [build]
            systems = ["x86_64-linux"]

            [[service]]
            name = "web"
            """));

        var lockFile = new LockFile { Path = TempPath() };
        var service = new LockedService { Flake = ".", Image = "coverage/web", Tag = "latest" };
        var platform = new LockedPlatform
        {
            Platform = "linux/amd64",
            Attr = "narwhal.images.x86_64-linux.web",
            StorePath = "/nix/store/web.tar.gz",
            NarHash = "sha256-web",
            ImageId = "sha256:web",
        };
        service.Platforms["x86_64-linux"] = platform;
        lockFile.Services["web"] = service;

        Assert.True(lockFile.Covers(manifest));

        platform.Platform = "linux/arm64";
        Assert.False(lockFile.Covers(manifest));

        platform.Platform = "linux/amd64";
        platform.ImageId = "";
        Assert.False(lockFile.Covers(manifest));

        platform.ImageId = "sha256:web";
        platform.Attr = "narwhal.images.old.web";
        Assert.False(lockFile.Covers(manifest));
    }

    private static string WriteManifest(string text)
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "narwhal.toml");
        File.WriteAllText(path, text);
        return path;
    }
}
