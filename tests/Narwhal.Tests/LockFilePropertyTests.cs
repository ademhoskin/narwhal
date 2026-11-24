using Narwhal.Config;
using Xunit;

namespace Narwhal.Tests;

/// <summary>Seeded lock-file properties plus the coverage matrix <c>Covers</c> promises.</summary>
public class LockFilePropertyTests
{
    private static readonly string[] ServiceNames = ["api", "cache", "db", "web", "worker"];
    private static readonly string[] FlakeRefs = [".", "github:acme/app", "path:../shared"];

    [Fact]
    public void ArbitraryPlatformSetsRoundTripDeterministically()
    {
        for (var seed = 1; seed <= 30; seed++)
        {
            var directory = NewDirectory();
            try
            {
                var path = Path.Combine(directory, "narwhal.lock.json");
                var model = RandomLock(new Random(seed * 3571));
                model.Path = path;
                model.Save();

                var first = File.ReadAllText(path);
                var loaded = LockFile.Load(path);

                AssertLockEqual(model, loaded, seed);
                Assert.Equal(LockFile.CurrentVersion, loaded.Version);
                Assert.False(loaded.NeedsMigration);
                Assert.Equal(model.Services.Keys, loaded.Services.Keys);

                loaded.Save();
                var second = File.ReadAllText(path);
                Assert.True(first == second,
                    $"seed {seed}: save/load/save was not idempotent\n--- first ---\n{first}\n--- second ---\n{second}");
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }
    }

    [Fact]
    public void AnEmptyLockRoundTripsWithoutInventingEntries()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "narwhal.lock.json");
            var model = new LockFile { Path = path, Narwhal = "0.1.0" };
            model.Save();

            var loaded = LockFile.Load(path);
            Assert.Empty(loaded.Services);
            Assert.Empty(loaded.Flakes);
            Assert.Equal("0.1.0", loaded.Narwhal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("x86_64-linux", "linux/amd64")]
    [InlineData("aarch64-linux", "linux/arm64")]
    [InlineData("i686-linux", "linux/386")]
    [InlineData("armv7l-linux", "linux/arm/v7")]
    [InlineData("riscv64-linux", "linux/riscv64")]
    [InlineData("ppc64le-linux", "linux/ppc64le")]
    [InlineData("s390x-linux", "linux/s390x")]
    public void MigratesVersionOneForEverySupportedSystem(string system, string ociPlatform)
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "narwhal.lock.json");
            File.WriteAllText(path, $$"""
                {
                  "version": 1,
                  "narwhal": "0.1.0",
                  "manifest_sha256": "sha256-manifest",
                  "flakes": {
                    ".": { "url": "git+file:///repo", "nar_hash": "sha256-flake" }
                  },
                  "services": {
                    "web": {
                      "attr": "narwhal.images.{{system}}.web",
                      "flake": ".",
                      "image": "shop/web",
                      "tag": "1.2.3",
                      "store_path": "/nix/store/web.tar.gz",
                      "nar_hash": "sha256-nar",
                      "image_id": "sha256:image"
                    }
                  }
                }
                """);

            var loaded = LockFile.Load(path);
            Assert.Equal(LockFile.CurrentVersion, loaded.Version);
            Assert.True(loaded.NeedsMigration);
            Assert.Equal("0.1.0", loaded.Narwhal);
            Assert.Equal("sha256-manifest", loaded.ManifestSha256);

            var service = Assert.IsType<LockedService>(loaded.Service("web"));
            Assert.Equal(".", service.Flake);
            Assert.Equal("shop/web", service.Image);
            Assert.Equal("1.2.3", service.Tag);

            var platform = Assert.IsType<LockedPlatform>(loaded.Platform("web", system));
            Assert.Equal(ociPlatform, platform.Platform);
            Assert.Equal($"narwhal.images.{system}.web", platform.Attr);
            Assert.Equal("/nix/store/web.tar.gz", platform.StorePath);
            Assert.Equal("sha256-nar", platform.NarHash);
            Assert.Equal("sha256:image", platform.ImageId);
            Assert.Single(service.Platforms);

            loaded.Save();
            Assert.False(loaded.NeedsMigration);

            var reloaded = LockFile.Load(path);
            Assert.False(reloaded.NeedsMigration);
            Assert.Equal(ociPlatform, reloaded.Platform("web", system)!.Platform);
            Assert.Contains("\"version\": 2", File.ReadAllText(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void VersionTwoDoesNotNeedMigration()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "narwhal.lock.json");
            var model = new LockFile { Path = path };
            model.Save();

            var loaded = LockFile.Load(path);
            Assert.False(loaded.NeedsMigration);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("full", true)]
    [InlineData("missing-service", false)]
    [InlineData("missing-platform", false)]
    [InlineData("wrong-attr", false)]
    [InlineData("empty-nar-hash", false)]
    [InlineData("wrong-oci-platform", false)]
    [InlineData("empty-image-id", false)]
    [InlineData("empty-lock", false)]
    [InlineData("prebuilt-service-absent", true)]
    [InlineData("extra-unconfigured-platform", true)]
    [InlineData("empty-store-path", true)]
    public void CoverageMatrixMatchesTheLockedEvidence(string mutation, bool expected)
    {
        var manifest = MultiSystemManifest();
        var lockFile = FullLock(manifest);

        switch (mutation)
        {
            case "missing-service":
                lockFile.Services.Remove("web");
                break;
            case "missing-platform":
                lockFile.Services["web"].Platforms.Remove("aarch64-linux");
                break;
            case "wrong-attr":
                lockFile.Services["web"].Platforms["x86_64-linux"].Attr = "narwhal.images.old.web";
                break;
            case "empty-nar-hash":
                lockFile.Services["web"].Platforms["aarch64-linux"].NarHash = "";
                break;
            case "wrong-oci-platform":
                lockFile.Services["web"].Platforms["x86_64-linux"].Platform = "linux/arm64";
                break;
            case "empty-image-id":
                lockFile.Services["web"].Platforms["x86_64-linux"].ImageId = "";
                break;
            case "empty-lock":
                lockFile.Services.Clear();
                break;
            case "extra-unconfigured-platform":
                lockFile.Services["web"].Platforms["i686-linux"] = new LockedPlatform
                {
                    Platform = "linux/386",
                    Attr = "narwhal.images.i686-linux.web",
                    StorePath = "/nix/store/i686-web.tar.gz",
                    NarHash = "sha256-i686",
                    ImageId = "sha256:i686",
                };
                break;
            case "empty-store-path":
                lockFile.Services["web"].Platforms["x86_64-linux"].StorePath = "";
                break;
        }

        Assert.True(expected == lockFile.Covers(manifest),
            $"mutation '{mutation}': expected Covers to be {expected}");
    }

    [Fact]
    public void CoverageUsesTheSubstitutedAttrForExplicitSystemAttrs()
    {
        var manifest = new Manifest
        {
            Name = "substitution",
            Directory = "/tmp/substitution",
            Path = "/tmp/substitution/narwhal.toml",
            RawText = "",
            Build = new BuildOptions { Systems = ["x86_64-linux", "aarch64-linux"] },
            Services = [new ServiceSpec { Name = "web", Attr = "images.{system}.web" }],
        };

        var lockFile = new LockFile();
        lockFile.Services["web"] = new LockedService
        {
            Flake = ".",
            Image = "substitution/web",
            Tag = "latest",
            Platforms =
            {
                ["x86_64-linux"] = new LockedPlatform
                {
                    Platform = "linux/amd64",
                    Attr = "images.x86_64-linux.web",
                    NarHash = "sha256-amd64",
                    ImageId = "sha256:amd64",
                },
                ["aarch64-linux"] = new LockedPlatform
                {
                    Platform = "linux/arm64",
                    Attr = "images.aarch64-linux.web",
                    NarHash = "sha256-arm64",
                    ImageId = "sha256:arm64",
                },
            },
        };

        Assert.True(lockFile.Covers(manifest));

        lockFile.Services["web"].Platforms["aarch64-linux"].Attr = "images.aarch64-linux.other";
        Assert.False(lockFile.Covers(manifest));
    }

    private static LockFile FullLock(Manifest manifest)
    {
        var lockFile = new LockFile
        {
            Narwhal = "0.1.0",
            ManifestSha256 = manifest.ContentHash,
        };
        lockFile.Flakes["."] = new LockedFlake
        {
            Url = "git+file:///repo",
            NarHash = "sha256-flake",
            Revision = "0123456789abcdef",
        };

        var web = new LockedService { Flake = ".", Image = "matrix/web", Tag = "latest" };
        foreach (var system in manifest.Build.Systems)
        {
            web.Platforms[system] = new LockedPlatform
            {
                Platform = NixPlatform.ToOciPlatform(system),
                Attr = $"narwhal.images.{system}.web",
                StorePath = $"/nix/store/{system}-web.tar.gz",
                NarHash = $"sha256-{system}",
                ImageId = $"sha256:image-{system}",
            };
        }
        lockFile.Services["web"] = web;
        return lockFile;
    }

    private static Manifest MultiSystemManifest() => new()
    {
        Name = "matrix",
        Directory = "/tmp/matrix",
        Path = "/tmp/matrix/narwhal.toml",
        RawText = "",
        Build = new BuildOptions { Systems = ["x86_64-linux", "aarch64-linux"] },
        Services =
        [
            new ServiceSpec { Name = "web" },
            new ServiceSpec { Name = "db", Prebuilt = true },
        ],
    };

    private static LockFile RandomLock(Random random)
    {
        var lockFile = new LockFile
        {
            Narwhal = "0.1.0",
            ManifestSha256 = random.Next(2) == 0 ? "" : Convert.ToHexStringLower(RandomBytes(random)),
        };

        var flakes = FlakeRefs.Where(_ => random.Next(3) != 0).ToList();
        foreach (var flake in flakes)
        {
            lockFile.Flakes[flake] = new LockedFlake
            {
                Url = "git+file:///repo?" + flake,
                NarHash = random.Next(2) == 0 ? null : $"sha256-{random.Next(1, int.MaxValue):x}",
                Revision = random.Next(2) == 0 ? null : Convert.ToHexStringLower(RandomBytes(random))[..40],
            };
        }

        var supported = NixPlatform.SupportedSystems.ToList();
        foreach (var name in ServiceNames.Where(_ => random.Next(2) == 0).DefaultIfEmpty("web"))
        {
            var service = new LockedService
            {
                Flake = FlakeRefs[random.Next(FlakeRefs.Length)],
                Image = $"shop/{name}",
                Tag = random.Next(2) == 0 ? "latest" : $"{random.Next(1, 10)}.{random.Next(0, 10)}",
            };

            var systems = supported.Where(_ => random.Next(3) == 0).ToList();
            if (systems.Count == 0)
                systems.Add(supported[random.Next(supported.Count)]);

            foreach (var system in systems)
            {
                service.Platforms[system] = new LockedPlatform
                {
                    Platform = NixPlatform.ToOciPlatform(system),
                    Attr = $"narwhal.images.{system}.{name}",
                    StorePath = $"/nix/store/{system}-{name}.tar.gz",
                    NarHash = $"sha256-{random.Next(1, int.MaxValue):x}",
                    ImageId = $"sha256:{random.Next(1, int.MaxValue):x}",
                };
            }

            lockFile.Services[name] = service;
        }

        return lockFile;
    }

    private static byte[] RandomBytes(Random random)
    {
        var bytes = new byte[32];
        random.NextBytes(bytes);
        return bytes;
    }

    private static void AssertLockEqual(LockFile expected, LockFile actual, int seed)
    {
        Assert.True(expected.Version == actual.Version,
            $"seed {seed}: version {expected.Version} != {actual.Version}");
        Assert.True(expected.Narwhal == actual.Narwhal,
            $"seed {seed}: narwhal {expected.Narwhal} != {actual.Narwhal}");
        Assert.True(expected.ManifestSha256 == actual.ManifestSha256,
            $"seed {seed}: manifest hash {expected.ManifestSha256} != {actual.ManifestSha256}");
        Assert.True(expected.Flakes.Keys.SequenceEqual(actual.Flakes.Keys),
            $"seed {seed}: flake keys differ");

        foreach (var (key, flake) in expected.Flakes)
        {
            var other = actual.Flakes[key];
            Assert.True(flake.Url == other.Url, $"seed {seed}: flake {key} url");
            Assert.True(flake.NarHash == other.NarHash, $"seed {seed}: flake {key} nar hash");
            Assert.True(flake.Revision == other.Revision, $"seed {seed}: flake {key} revision");
        }

        Assert.True(expected.Services.Keys.SequenceEqual(actual.Services.Keys),
            $"seed {seed}: service keys differ");

        foreach (var (name, service) in expected.Services)
        {
            var other = actual.Services[name];
            Assert.True(service.Flake == other.Flake, $"seed {seed}: {name} flake");
            Assert.True(service.Image == other.Image, $"seed {seed}: {name} image");
            Assert.True(service.Tag == other.Tag, $"seed {seed}: {name} tag");
            Assert.True(service.Platforms.Keys.SequenceEqual(other.Platforms.Keys),
                $"seed {seed}: {name} platform keys differ");

            foreach (var (system, platform) in service.Platforms)
            {
                var actualPlatform = other.Platforms[system];
                Assert.True(platform.Platform == actualPlatform.Platform, $"seed {seed}: {name}/{system} platform");
                Assert.True(platform.Attr == actualPlatform.Attr, $"seed {seed}: {name}/{system} attr");
                Assert.True(platform.StorePath == actualPlatform.StorePath, $"seed {seed}: {name}/{system} store path");
                Assert.True(platform.NarHash == actualPlatform.NarHash, $"seed {seed}: {name}/{system} nar hash");
                Assert.True(platform.ImageId == actualPlatform.ImageId, $"seed {seed}: {name}/{system} image id");
            }
        }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "narwhal-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must not fail the test run.
        }
    }
}
