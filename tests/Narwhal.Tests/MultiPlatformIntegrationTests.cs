using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Narwhal.Config;
using Xunit;

namespace Narwhal.Tests;

/// <summary>
/// End-to-end CLI coverage for multi-platform builds. A fake nix produces real
/// docker-archive tarballs and a fake podman keeps local tags and manifest
/// lists, so every orchestration path runs without emulation or a registry.
/// </summary>
[Collection(ProcessCollection.Name)]
public class MultiPlatformIntegrationTests
{
    [Fact]
    public void BuildsVerifiesPlansPushesAndRunsEveryConfiguredSystem()
    {
        using var box = new Sandbox([Foreign(), Host()]);

        var build = box.Run("build");
        Assert.Equal(0, build.Code);
        Assert.DoesNotContain("error:", build.Err);

        var platforms = box.LockPlatforms("web");
        Assert.Equal(
            new[] { Foreign(), Host() }.Order(StringComparer.Ordinal),
            platforms.Select(p => p.Name).Order(StringComparer.Ordinal));
        var hostId = platforms.Single(p => p.Name == Host()).ImageId;
        var foreignId = platforms.Single(p => p.Name == Foreign()).ImageId;
        Assert.StartsWith("sha256:", hostId);
        Assert.NotEqual(hostId, foreignId);

        // Only the host image is loaded during a plain build.
        var loadLog = File.ReadAllText(box.PodmanLog);
        Assert.Contains(box.ArtifactPath(Host(), "web"), loadLog);
        Assert.DoesNotContain(box.ArtifactPath(Foreign(), "web"), loadLog);

        var plan = box.Run("plan", "--json");
        Assert.Equal(0, plan.Code);
        using (var document = JsonDocument.Parse(plan.Out))
        {
            Assert.Equal("multi", document.RootElement.GetProperty("project").GetString());
            Assert.Equal(Path.Combine(box.DirectoryPath, "narwhal.toml"),
                document.RootElement.GetProperty("manifest").GetString());
            Assert.Equal(box.LockPath, document.RootElement.GetProperty("lock").GetString());
            Assert.False(document.RootElement.GetProperty("lock_stale").GetBoolean());

            var systems = document.RootElement.GetProperty("systems")
                .EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal([Foreign(), Host()], systems);

            var service = document.RootElement.GetProperty("services").EnumerateArray().Single();
            Assert.Equal("web", service.GetProperty("name").GetString());
            var locked = service.GetProperty("platforms");
            Assert.True(locked.TryGetProperty(Host(), out _), $"plan JSON is missing platform '{Host()}'");
            Assert.True(locked.TryGetProperty(Foreign(), out _), $"plan JSON is missing platform '{Foreign()}'");

            foreach (var system in new[] { Host(), Foreign() })
            {
                var entry = locked.GetProperty(system);
                Assert.Equal(NixPlatform.ToOciPlatform(system), entry.GetProperty("platform").GetString());
                Assert.Equal($"narwhal.images.{system}.web", entry.GetProperty("attr").GetString());
                Assert.Equal("locked", entry.GetProperty("status").GetString());
                var digest = entry.GetProperty("digest").GetString();
                Assert.False(string.IsNullOrWhiteSpace(digest), $"plan JSON has no digest for '{system}'");
                Assert.NotEqual("-", digest);
            }
        }

        var verify = box.Run("verify", "--no-load");
        Assert.True(verify.Code == 0, verify.Err + verify.Out);
        Assert.Contains($"every service reproduced", verify.Err);

        // Given two platforms, a multi-platform push builds a local index under
        // its own name and pushes every image in it.
        var push = box.Run("push");
        Assert.True(push.Code == 0, push.Err + push.Out);
        var index = box.IndexName("example/web:latest");
        var podman = File.ReadAllText(box.PodmanLog);
        Assert.Contains($"manifest rm {index}", podman);
        Assert.Contains($"manifest create --amend {index}", podman);
        Assert.DoesNotContain("manifest create --amend example/web:latest", podman);
        Assert.Contains($"manifest add {index} {foreignId} {hostId}", podman);
        Assert.Contains($"manifest push --all --rm {index} docker://example/web:latest", podman);

        var manifest = box.PushedManifestEntries(index);
        Assert.Equal(2, manifest.Count);
        Assert.Contains(manifest, entry => entry.Id == foreignId && entry.Arch == Arch(Foreign()));
        Assert.Contains(manifest, entry => entry.Id == hostId && entry.Arch == Arch(Host()));
        Assert.Equal(hostId, box.TaggedId("example/web:latest"));
        Assert.Equal(Arch(Host()), box.TaggedArch("example/web:latest"));

        var up = box.Run("up", "--no-build");
        Assert.Equal(0, up.Code);
        Assert.Contains("run --detach", File.ReadAllText(box.PodmanLog));
    }

    [Fact]
    public void SelectingOneSystemBuildsAndLocksOnlyThatSystem()
    {
        using var box = new Sandbox([Foreign(), Host()]);

        var build = box.Run("build", "--system", Foreign());
        Assert.Equal(0, build.Code);

        var names = box.LockPlatforms("web").Select(p => p.Name).ToList();
        Assert.Equal(new[] { Foreign() }, names);
        Assert.DoesNotContain($"narwhal.images.{Host()}.web", File.ReadAllText(box.NixLog));

        var locked = box.Run("build", "--locked", "--system", Host());
        Assert.Equal(4, locked.Code);
        Assert.Contains($"{Host()}) is missing", locked.Err);

        var unknown = box.Run("build", "--system", "mips-linux");
        Assert.Equal(1, unknown.Code);
        Assert.Contains("not configured", unknown.Err);
    }

    [Fact]
    public void PushingOneSelectedSystemPushesThatPlatformsImageId()
    {
        using var box = new Sandbox([Foreign(), Host()]);
        Assert.Equal(0, box.Run("build").Code);

        // Given the tag already points at the host image, a foreign-only push
        // loads that archive and pushes its id, not whatever owns the tag.
        var platforms = box.LockPlatforms("web").ToDictionary(p => p.Name, p => p.ImageId);
        Assert.Equal(platforms[Host()], box.TaggedId("example/web:latest"));

        var push = box.Run("push", "--system", Foreign());
        Assert.True(push.Code == 0, push.Err + push.Out);
        Assert.Contains($"push {platforms[Foreign()]} docker://example/web:latest", File.ReadAllText(box.PodmanLog));
        Assert.Equal(platforms[Host()], box.TaggedId("example/web:latest"));
        Assert.Equal(Arch(Host()), box.TaggedArch("example/web:latest"));
        Assert.Equal(platforms[Foreign()], box.LockPlatforms("web").Single(p => p.Name == Foreign()).ImageId);
    }

    [Fact]
    public void PushesWhenTheLockedStorePathIsADirectory()
    {
        using var box = new Sandbox([Host()], directoryStorePaths: true);
        Assert.Equal(0, box.Run("build").Code);

        // Given nix reports a directory containing the archive, push resolves it.
        Assert.True(Directory.Exists(box.StoreDirectory(Host(), "web")));
        Assert.True(File.Exists(box.ArtifactPath(Host(), "web")));

        var push = box.Run("push");
        Assert.True(push.Code == 0, push.Err + push.Out);
        var id = box.LockPlatforms("web").Single().ImageId;
        Assert.Contains($"push {id} docker://example/web:latest", File.ReadAllText(box.PodmanLog));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PushLeavesTheTagOnTheHostForEitherSystemOrder(bool hostFirst)
    {
        string[] systems = hostFirst ? [Host(), Foreign()] : [Foreign(), Host()];
        using var box = new Sandbox(systems);
        Assert.Equal(0, box.Run("build").Code);

        // Given either build/push order, pushing must leave the tag on the host.
        var push = box.Run("push");
        Assert.True(push.Code == 0, push.Err + push.Out);

        var hostId = box.LockPlatforms("web").Single(p => p.Name == Host()).ImageId;
        Assert.Equal(hostId, box.TaggedId("example/web:latest"));
        Assert.Equal(Arch(Host()), box.TaggedArch("example/web:latest"));
    }

    [Fact]
    public void DriftedLockImageIdBlocksPushBeforeAnyPush()
    {
        using var box = new Sandbox([Foreign(), Host()]);
        Assert.Equal(0, box.Run("build").Code);

        var node = JsonNode.Parse(File.ReadAllText(box.LockPath))!;
        node["services"]!["web"]!["platforms"]![Foreign()]!["image_id"] = "sha256:deadbeef";
        File.WriteAllText(box.LockPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        // Given a lock whose image id no longer matches the archive, push fails.
        var push = box.Run("push", "--system", Foreign());
        Assert.Equal(3, push.Code);
        Assert.Contains("run 'narwhal build'", push.Err);

        var log = File.ReadAllText(box.PodmanLog);
        Assert.DoesNotContain("push ", log);
    }

    [Fact]
    public void StaleLockAttrBlocksPush()
    {
        using var box = new Sandbox([Host()]);
        Assert.Equal(0, box.Run("build").Code);

        var node = JsonNode.Parse(File.ReadAllText(box.LockPath))!;
        node["services"]!["web"]!["platforms"]![Host()]!["attr"] = "narwhal.images.old.web";
        File.WriteAllText(box.LockPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        // Given a lock built for a different attr, push fails before pushing.
        var push = box.Run("push");
        Assert.Equal(3, push.Code);
        Assert.Contains("run 'narwhal build'", push.Err);
        Assert.DoesNotContain("push ", File.ReadAllText(box.PodmanLog));
    }

    [Fact]
    public void PushClearsStaleManifestEntries()
    {
        using var box = new Sandbox([Foreign(), Host()]);
        Assert.Equal(0, box.Run("build").Code);

        var index = box.IndexName("example/web:latest");
        box.SeedManifest(index, "sha256:deadbeef amd64 linux");

        // Given a leftover index with a stale platform, push starts from empty.
        var push = box.Run("push");
        Assert.True(push.Code == 0, push.Err + push.Out);

        var manifest = box.PushedManifestEntries(index);
        Assert.Equal(2, manifest.Count);
        Assert.DoesNotContain(manifest, entry => entry.Id == "sha256:deadbeef");
    }

    [Fact]
    public void PushesLoadedImageWhenTheArchiveWasCollected()
    {
        using var box = new Sandbox([Host()]);
        Assert.Equal(0, box.Run("build").Code);
        File.Delete(box.StorePath(Host(), "web"));

        // Given the archive is gone but the tag still holds the locked image.
        var push = box.Run("push");
        Assert.True(push.Code == 0, push.Err + push.Out);
        var id = box.LockPlatforms("web").Single().ImageId;
        Assert.Contains($"push {id} docker://example/web:latest", File.ReadAllText(box.PodmanLog));
    }

    [Fact]
    public void MultiPlatformPushFallsBackToLoadedLockedIds()
    {
        using var box = new Sandbox([Foreign(), Host()]);
        Assert.Equal(0, box.Run("build").Code);
        Assert.Equal(0, box.Run("push").Code);

        // Given the archives are gone but both locked images are loaded, push still works.
        File.Delete(box.StorePath(Foreign(), "web"));
        File.Delete(box.StorePath(Host(), "web"));

        var push = box.Run("push");
        Assert.True(push.Code == 0, push.Err + push.Out);
        var manifest = box.PushedManifestEntries(box.IndexName("example/web:latest"));
        Assert.Equal(2, manifest.Count);
    }

    [Fact]
    public void RestoreFailureDoesNotMaskTheOriginalPushFailure()
    {
        using var box = new Sandbox([Foreign()]);
        Assert.Equal(0, box.Run("build").Code);
        box.FailPush();
        box.FailTag();
        box.SeedTag("example/web:latest", "sha256:previous", "amd64");

        // Given push and the host-tag restore both fail, the push error survives.
        var push = box.Run("push");
        Assert.Equal(3, push.Code);
        Assert.Contains("original push failure", push.Err);
        Assert.Contains("podman push", push.Err);
        Assert.Contains("could not restore", push.Err);
    }

    [Fact]
    public void StaleHostAttrFallsBackToThePreviousTag()
    {
        using var box = new Sandbox([Foreign(), Host()]);
        Assert.Equal(0, box.Run("build").Code);
        var hostId = box.LockPlatforms("web").Single(p => p.Name == Host()).ImageId;

        var node = JsonNode.Parse(File.ReadAllText(box.LockPath))!;
        node["services"]!["web"]!["platforms"]![Host()]!["attr"] = "narwhal.images.old.web";
        File.WriteAllText(box.LockPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        // Given a stale host entry, a foreign push restores the prior tag.
        var push = box.Run("push", "--system", Foreign());
        Assert.True(push.Code == 0, push.Err + push.Out);
        Assert.Contains("host lock entry", push.Err);
        Assert.Equal(hostId, box.TaggedId("example/web:latest"));
        Assert.Equal(Arch(Host()), box.TaggedArch("example/web:latest"));
    }

    [Fact]
    public void HostLoadMismatchWarnsAndFallsBackToThePreviousTag()
    {
        using var box = new Sandbox([Foreign(), Host()]);
        Assert.Equal(0, box.Run("build").Code);
        var hostId = box.LockPlatforms("web").Single(p => p.Name == Host()).ImageId;
        box.MismatchHostLoad();

        // Given the host reload returns a different id, the prior tag is restored.
        var push = box.Run("push", "--system", Foreign());
        Assert.True(push.Code == 0, push.Err + push.Out);
        Assert.Contains("loaded as", push.Err);
        Assert.Equal(hostId, box.TaggedId("example/web:latest"));
        Assert.Equal(Arch(Host()), box.TaggedArch("example/web:latest"));
    }

    [Fact]
    public void IncompleteCoverageClearsTheManifestHash()
    {
        using var box = new Sandbox([Foreign(), Host()]);
        Assert.Equal(0, box.Run("build").Code);

        var node = JsonNode.Parse(File.ReadAllText(box.LockPath))!.AsObject();
        node["manifest_sha256"] = "stale";
        node["services"]!["web"]!["platforms"]!.AsObject().Remove(Foreign());
        File.WriteAllText(box.LockPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        // Given one configured system is unlocked, the stale manifest hash is cleared.
        var build = box.Run("build", "--system", Host());
        Assert.True(build.Code == 0, build.Err + build.Out);

        using var document = JsonDocument.Parse(File.ReadAllText(box.LockPath));
        Assert.Equal("", document.RootElement.GetProperty("manifest_sha256").GetString());
    }

    [Fact]
    public void ReusableUpPersistsAVersionOneMigration()
    {
        using var box = new Sandbox([Host()]);
        Assert.Equal(0, box.Run("build").Code);
        box.DowngradeLockToVersionOne();
        Assert.Contains("\"version\": 1", File.ReadAllText(box.LockPath));

        // Given a v1 lock whose host entry is reusable, up must write v2 back.
        var up = box.Run("up");
        Assert.True(up.Code == 0, up.Err + up.Out);
        Assert.Contains("\"version\": 2", File.ReadAllText(box.LockPath));
        Assert.Contains("\"platforms\"", File.ReadAllText(box.LockPath));
    }

    [Fact]
    public void LockedBuildReportsEveryMissingAndDriftingPlatform()
    {
        using var box = new Sandbox([Foreign(), Host()]);
        Assert.Equal(0, box.Run("build").Code);

        var node = JsonNode.Parse(File.ReadAllText(box.LockPath))!;
        var platforms = node["services"]!["web"]!["platforms"]!.AsObject();
        platforms.Remove(Foreign());
        platforms[Host()]!["nar_hash"] = "sha256-drifted";
        File.WriteAllText(box.LockPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        // Given one platform vanished and another drifted, --locked lists both.
        var locked = box.Run("build", "--locked", "--no-load");
        Assert.Equal(4, locked.Code);
        Assert.Contains($"{Foreign()}) is missing", locked.Err);
        Assert.Contains($"{Host()}) did not reproduce", locked.Err);
    }

    [Fact]
    public void VerifyReportsDriftForOnePlatform()
    {
        using var box = new Sandbox([Foreign(), Host()]);
        Assert.Equal(0, box.Run("build").Code);

        var path = box.LockPath;
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["services"]!["web"]!["platforms"]![Foreign()]!["nar_hash"] = "sha256-drifted";
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        var verify = box.Run("verify", "--no-load");
        Assert.Equal(4, verify.Code);
        Assert.Contains($"web/{Foreign()}", verify.Err);
        Assert.DoesNotContain($"web/{Host()}", verify.Err);
    }

    [Fact]
    public void UpFailsWhenTheHostIsNotConfigured()
    {
        using var box = new Sandbox([Foreign()]);

        var up = box.Run("up");
        Assert.Equal(1, up.Code);
        Assert.Contains("host system", up.Err);
        Assert.Contains(Host(), up.Err);
    }

    private static string Host() => NixPlatform.ContainerSystem;

    private static string Foreign() =>
        NixPlatform.ContainerSystem == "x86_64-linux" ? "aarch64-linux" : "x86_64-linux";

    private static string Arch(string system) =>
        NixPlatform.ToOciPlatform(system).Split('/')[1];

    private sealed record Locked(string Name, string ImageId);

    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class ProcessCollection
    {
        public const string Name = "cli-process";
    }

    /// <summary>A temp project with fake nix and podman executables on the narwhal env vars.</summary>
    private sealed class Sandbox : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

        public Sandbox(IReadOnlyList<string> systems, bool directoryStorePaths = false)
        {
            DirectoryStorePaths = directoryStorePaths;
            DirectoryPath = Path.Combine(Path.GetTempPath(), "narwhal-tests", Guid.NewGuid().ToString("n"));
            State = Path.Combine(DirectoryPath, "state");
            Directory.CreateDirectory(State);

            NixPath = WriteTool("nix");
            PodmanPath = WriteTool("podman");

            File.WriteAllText(Path.Combine(DirectoryPath, "narwhal.toml"), $$"""
                name = "multi"
                flake = "."

                [build]
                systems = [{{string.Join(", ", systems.Select(s => $"\"{s}\""))}}]

                [[service]]
                name = "web"
                image = "example/web"
                tag = "latest"
                """);

            Set("NARWHAL_NIX", NixPath);
            Set("NARWHAL_PODMAN", PodmanPath);
            Set("FAKE_STATE", State);
            Set("FAKE_IMAGE_NAME", "example/web:latest");
            Set("FAKE_HOST_SYSTEM", Host());
            if (directoryStorePaths)
                Set("FAKE_DIRECTORY_STORE", "1");
        }

        public bool DirectoryStorePaths { get; }
        public string DirectoryPath { get; }
        public string State { get; }
        public string NixPath { get; }
        public string PodmanPath { get; }
        public string NixLog => Path.Combine(State, "nix.log");
        public string PodmanLog => Path.Combine(State, "podman.log");
        public string LockPath => Path.Combine(DirectoryPath, "narwhal.lock.json");

        public (int Code, string Out, string Err) Run(params string[] args)
        {
            var previousOut = Console.Out;
            var previousError = Console.Error;
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            Console.SetOut(stdout);
            Console.SetError(stderr);
            try
            {
                var code = App.Main([.. args, "-C", DirectoryPath]).GetAwaiter().GetResult();
                return (code, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
            }
        }

        public string StorePath(string system, string service) =>
            Path.Combine(State, "store", DirectoryStorePaths ? $"{system}-{service}" : $"{system}-{service}.tar.gz");

        public string StoreDirectory(string system, string service) =>
            Path.Combine(State, "store", $"{system}-{service}");

        public string ArtifactPath(string system, string service) =>
            DirectoryStorePaths ? Path.Combine(StoreDirectory(system, service), "tarball") : StorePath(system, service);

        public void FailPush() => Set("FAKE_FAIL_PUSH", "1");

        public void FailTag() => Set("FAKE_FAIL_TAG", "1");

        public void MismatchHostLoad() => Set("FAKE_MISMATCH_HOST_LOAD", "1");

        public void SeedTag(string reference, string id, string arch)
        {
            var idsDirectory = Path.Combine(State, "podman", "ids");
            Directory.CreateDirectory(idsDirectory);
            File.WriteAllText(Path.Combine(idsDirectory, id), arch + "\n");

            var tags = Path.Combine(State, "podman", "tags");
            Directory.CreateDirectory(Path.GetDirectoryName(tags)!);
            File.WriteAllText(tags, $"{reference}\t{id}\n");
        }

        public List<Locked> LockPlatforms(string service)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(LockPath));
            var platforms = document.RootElement
                .GetProperty("services").GetProperty(service).GetProperty("platforms");
            return platforms.EnumerateObject()
                .Select(entry => new Locked(entry.Name, entry.Value.GetProperty("image_id").GetString()!))
                .ToList();
        }

        public string? TaggedId(string reference)
        {
            var path = Path.Combine(State, "podman", "tags");
            if (!File.Exists(path))
                return null;
            return File.ReadAllLines(path)
                .Where(line => line.Length > 0)
                .Select(line => line.Split('\t'))
                .Where(parts => parts.Length == 2 && parts[0] == reference)
                .Select(parts => parts[1])
                .LastOrDefault();
        }

        public string? TaggedArch(string reference)
        {
            var id = TaggedId(reference);
            if (id is null)
                return null;
            var path = Path.Combine(State, "podman", "ids", id);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        public string IndexName(string reference)
        {
            var hash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(reference)))[..12];
            return $"localhost/narwhal-index/multi/web-{hash}:latest";
        }

        public void SeedManifest(string index, params string[] entries)
        {
            var encoded = index.Replace('/', '_').Replace(':', '_');
            var path = Path.Combine(State, "podman", "manifests", encoded);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, entries);
        }

        public void DowngradeLockToVersionOne()
        {
            var node = JsonNode.Parse(File.ReadAllText(LockPath))!.AsObject();
            var services = new JsonObject();
            foreach (var (name, service) in node["services"]!.AsObject())
            {
                var source = service!.AsObject();
                var platform = source["platforms"]![Host()]!.AsObject();
                services[name] = new JsonObject
                {
                    ["attr"] = platform["attr"]!.GetValue<string>(),
                    ["flake"] = source["flake"]!.GetValue<string>(),
                    ["image"] = source["image"]!.GetValue<string>(),
                    ["tag"] = source["tag"]!.GetValue<string>(),
                    ["store_path"] = platform["store_path"]!.GetValue<string>(),
                    ["nar_hash"] = platform["nar_hash"]!.GetValue<string>(),
                    ["image_id"] = platform["image_id"]!.GetValue<string>(),
                };
            }

            var v1 = new JsonObject
            {
                ["version"] = 1,
                ["narwhal"] = node["narwhal"]!.GetValue<string>(),
                ["manifest_sha256"] = node["manifest_sha256"]!.GetValue<string>(),
                ["flakes"] = node["flakes"]!.DeepClone(),
                ["services"] = services,
            };
            File.WriteAllText(LockPath, v1.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        public List<(string Id, string Arch)> PushedManifestEntries(string reference)
        {
            var encoded = reference.Replace('/', '_').Replace(':', '_');
            var path = Path.Combine(State, "podman", "pushed", encoded);
            Assert.True(File.Exists(path), $"no pushed manifest list at {path}");
            return File.ReadAllLines(path)
                .Where(line => line.Length > 0)
                .Select(line => line.Split(' '))
                .Select(parts => (parts[0], parts[1]))
                .ToList();
        }

        private string WriteTool(string name)
        {
            var path = Path.Combine(DirectoryPath, name);
            File.WriteAllText(path, ToolScript);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            return path;
        }

        private void Set(string name, string value)
        {
            if (!_previous.ContainsKey(name))
                _previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
                Environment.SetEnvironmentVariable(name, value);
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory must not fail the test run.
            }
        }

        private const string ToolScript = """
            #!/usr/bin/env bash
            set -euo pipefail
            state="${FAKE_STATE:?FAKE_STATE is required}"
            tool="$(basename "$0")"
            printf '%s\n' "$*" >> "$state/$tool.log"

            settag() {
              local ref="$1" id="$2" tags="$state/podman/tags" tmp="$state/podman/tags.tmp"
              mkdir -p "$state/podman"
              touch "$tags"
              awk -F '\t' -v r="$ref" '$1 != r' "$tags" > "$tmp"
              printf '%s\t%s\n' "$ref" "$id" >> "$tmp"
              mv "$tmp" "$tags"
            }

            resolve() {
              local ref="$1" id=""
              if [ -f "$state/podman/tags" ]; then
                id=$(awk -F '\t' -v r="$ref" '$1 == r { v = $2 } END { if (v != "") print v }' "$state/podman/tags")
              fi
              if [ -n "$id" ]; then printf '%s' "$id"; return 0; fi
              if [ -f "$state/podman/ids/$ref" ]; then printf '%s' "$ref"; return 0; fi
              return 1
            }

            arch_of() {
              case "$1" in
                x86_64-linux) echo amd64 ;;
                aarch64-linux) echo arm64 ;;
                i686-linux) echo 386 ;;
                armv7l-linux) echo armv7 ;;
                *) echo unknown ;;
              esac
            }

            if [ "$tool" = "nix" ]; then
              cmd="${1:-}"; shift || true
              case "$cmd" in
                --version) echo "nix (Nix) 2.34.8" ;;
                build)
                  installable=""
                  for arg in "$@"; do case "$arg" in *"#"*) installable="$arg" ;; esac; done
                  attr="${installable#*#}"
                  system=""; service=""
                  IFS='.' read -r -a parts <<< "$attr"
                  for part in "${parts[@]}"; do case "$part" in *-linux) system="$part" ;; esac; done
                  service="${parts[${#parts[@]}-1]}"
                  hex=$(printf '%s' "$system/$service" | sha256sum | cut -d' ' -f1)
                  tmp=$(mktemp -d "$state/tmp.XXXXXX")
                  printf '[{"Config":"%s.json","RepoTags":["%s"],"Layers":[]}]\n' "$hex" "${FAKE_IMAGE_NAME:-fake/image:latest}" > "$tmp/manifest.json"
                  : > "$tmp/$hex.json"
                  mkdir -p "$state/store"
                  if [ "${FAKE_DIRECTORY_STORE:-0}" = "1" ]; then
                    out="$state/store/${system}-${service}"
                    mkdir -p "$out"
                    artifact="$out/tarball"
                  else
                    out="$state/store/${system}-${service}.tar.gz"
                    artifact="$out"
                  fi
                  tar -C "$tmp" --mtime='@0' --owner=0 --group=0 --numeric-owner -czf "$artifact" manifest.json "$hex.json"
                  rm -rf "$tmp"
                  printf '%s\n' "$system" > "$artifact.system"
                  printf '%s\n' "$service" > "$artifact.service"
                  printf '[{"outputs":{"out":"%s"}}]\n' "$out"
                  ;;
                path-info)
                  path=""
                  for arg in "$@"; do path="$arg"; done
                  hex=$(printf '%s' "$path" | sha256sum | cut -d' ' -f1)
                  printf '[{"narHash":"sha256-%s"}]\n' "$hex"
                  ;;
                flake)
                  printf '{"resolvedUrl":"git+file:///fake?dir=app","narHash":"sha256-fake","revision":"0123456789abcdef"}\n'
                  ;;
              esac
            else
              cmd="${1:-}"; shift || true
              case "$cmd" in
                load)
                  artifact=""
                  while [ $# -gt 0 ]; do case "$1" in -i) artifact="$2"; shift 2 ;; *) shift ;; esac; done
                  system=$(cat "$artifact.system"); service=$(cat "$artifact.service")
                  hex=$(printf '%s' "$system/$service" | sha256sum | cut -d' ' -f1)
                  id="sha256:$hex"
                  if [ "${FAKE_MISMATCH_HOST_LOAD:-0}" = "1" ] && [ "$system" = "${FAKE_HOST_SYSTEM:-}" ]; then
                    id="sha256:deadbeef"
                  fi
                  mkdir -p "$state/podman/ids"
                  printf '%s\n' "$(arch_of "$system")" > "$state/podman/ids/$id"
                  settag "${FAKE_IMAGE_NAME:-fake/image:latest}" "$id"
                  echo "Loaded image: ${FAKE_IMAGE_NAME:-fake/image:latest}"
                  ;;
                tag)
                  if [ "${FAKE_FAIL_TAG:-0}" = "1" ]; then
                    echo "restore tag failure" >&2
                    exit 125
                  fi
                  id=$(resolve "$1")
                  settag "$2" "$id"
                  ;;
                image)
                  sub="$1"; shift || true
                  case "$sub" in
                    exists)
                      resolve "$1" > /dev/null
                      ;;
                    inspect)
                      fmt=""; ref=""
                      while [ $# -gt 0 ]; do case "$1" in --format) fmt="$2"; shift 2 ;; *) ref="$1"; shift ;; esac; done
                      id=$(resolve "$ref")
                      case "$fmt" in
                        *".Id"*) printf '%s\n' "$id" ;;
                        *".Architecture"*) cat "$state/podman/ids/$id" ;;
                        *) printf '%s\n' "$id" ;;
                      esac
                      ;;
                  esac
                  ;;
                manifest)
                  sub="$1"; shift || true
                  case "$sub" in
                    create)
                      amend=0; ref=""
                      for arg in "$@"; do case "$arg" in --amend) amend=1 ;; *) ref="$arg" ;; esac; done
                      if [ -f "$state/podman/tags" ] && \
                         awk -F '\t' -v r="$ref" '$1 == r { found = 1 } END { if (found) exit 0; exit 1 }' "$state/podman/tags"; then
                        echo "Error: image name \"$ref\" is already associated with an image; use --replace" >&2
                        exit 125
                      fi
                      enc=$(printf '%s' "$ref" | tr '/:' '__')
                      mkdir -p "$state/podman/manifests"
                      if [ ! -f "$state/podman/manifests/$enc" ]; then
                        : > "$state/podman/manifests/$enc"
                      elif [ "$amend" -ne 1 ]; then
                        echo "Error: manifest list \"$ref\" already exists; use --amend" >&2
                        exit 125
                      fi
                      ;;
                    add)
                      ref="$1"; shift
                      enc=$(printf '%s' "$ref" | tr '/:' '__')
                      for id in "$@"; do
                        arch=$(cat "$state/podman/ids/$id" 2>/dev/null || echo unknown)
                        printf '%s %s linux\n' "$id" "$arch" >> "$state/podman/manifests/$enc"
                      done
                      ;;
                    push)
                      rmflag=0; index=""; dest=""
                      for arg in "$@"; do
                        case "$arg" in
                          --all) ;;
                          --rm) rmflag=1 ;;
                          docker://*) dest="$arg" ;;
                          *) index="$arg" ;;
                        esac
                      done
                      enc=$(printf '%s' "$index" | tr '/:' '__')
                      mkdir -p "$state/podman/pushed"
                      cp "$state/podman/manifests/$enc" "$state/podman/pushed/$enc"
                      if [ "$rmflag" -eq 1 ]; then rm -f "$state/podman/manifests/$enc"; fi
                      ;;
                    rm)
                      ref="$1"
                      enc=$(printf '%s' "$ref" | tr '/:' '__')
                      rm -f "$state/podman/manifests/$enc"
                      ;;
                  esac
                  ;;
                push)
                  if [ "${FAKE_FAIL_PUSH:-0}" = "1" ]; then
                    echo "original push failure" >&2
                    exit 125
                  fi
                  ;;
                pull|run|rm|logs)
                  ;;
                network)
                  ;;
                volume)
                  ;;
                ps)
                  echo '[]'
                  ;;
                container)
                  exit 1
                  ;;
                inspect)
                  fmt=""
                  while [ $# -gt 0 ]; do case "$1" in --format) fmt="$2"; shift 2 ;; *) shift ;; esac; done
                  case "$fmt" in
                    *".State.Health"*) printf '\n' ;;
                    *".State.Status"*) printf 'running\n' ;;
                    *) printf '\n' ;;
                  esac
                  ;;
              esac
            fi
            """;
    }
}
