using System.Text.Json;
using Narwhal.Config;
using Xunit;

namespace Narwhal.Tests;

/// <summary>
/// CLI coverage for the docker runtime against a fake daemon that reproduces the
/// containerd image store: the archive records a config digest, but the daemon
/// reports (and can only tag) the manifest digest.
/// </summary>
[Collection(MultiPlatformIntegrationTests.CliProcessSerial.Name)]
public class DockerRuntimeIntegrationTests
{
    [Fact]
    public void PushTagsAndPushesTheDockerResolvableManifestId()
    {
        using var box = new DockerSandbox([Host()]);
        var build = box.Run("build");
        Assert.True(build.Code == 0, build.Err + build.Out);
        Assert.DoesNotContain("loaded as", build.Err);

        var configId = box.LockPlatforms("web").Single().ImageId;

        var push = box.Run("push");
        Assert.True(push.Code == 0, push.Err + push.Out);

        Assert.Contains("push example/web:latest", File.ReadAllText(box.DockerLogPath));
        var tagged = box.TaggedId("example/web:latest");
        Assert.NotNull(tagged);
        Assert.NotEqual(configId, tagged);
    }

    [Fact]
    public void MultiPlatformPushBuildsTheIndexFromLoadedManifestIds()
    {
        using var box = new DockerSandbox([Host(), Foreign()]);
        Assert.Equal(0, box.Run("build").Code);

        var configIds = box.LockPlatforms("web").ToDictionary(p => p.Name, p => p.ImageId);

        var push = box.Run("push");
        Assert.True(push.Code == 0, push.Err + push.Out);

        var entries = box.PushedManifestEntries("example/web:latest");
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Arch == Arch(Host()));
        Assert.Contains(entries, entry => entry.Arch == Arch(Foreign()));
        foreach (var (id, _) in entries)
            Assert.DoesNotContain(id, configIds.Values);
    }

    [Fact]
    public void VerifyReportsAFailedRowWhenDockerLoadFails()
    {
        using var box = new DockerSandbox([Host()]);
        Assert.Equal(0, box.Run("build").Code);
        box.FailLoad();

        var verify = box.Run("verify");

        Assert.Equal(4, verify.Code);
        var output = verify.Out + verify.Err;
        Assert.Contains("build failed", output);
        Assert.Contains($"web/{Host()}", output);
    }

    private static string Host() => NixPlatform.ContainerSystem;

    private static string Foreign() =>
        NixPlatform.ContainerSystem == "x86_64-linux" ? "aarch64-linux" : "x86_64-linux";

    private static string Arch(string system) =>
        NixPlatform.ToOciPlatform(system).Split('/')[1];

    private sealed record Locked(string Name, string ImageId);

    private sealed class DockerSandbox : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

        public DockerSandbox(IReadOnlyList<string> systems)
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "narwhal-docker-tests", Guid.NewGuid().ToString("n"));
            State = Path.Combine(DirectoryPath, "state");
            Directory.CreateDirectory(State);

            NixPath = WriteTool("nix");
            DockerPath = WriteTool("docker");

            File.WriteAllText(Path.Combine(DirectoryPath, "narwhal.toml"), $$"""
                name = "multi"
                runtime = "docker"
                flake = "."

                [build]
                systems = [{{string.Join(", ", systems.Select(s => $"\"{s}\""))}}]

                [[service]]
                name = "web"
                image = "example/web"
                tag = "latest"
                """);

            Set("NARWHAL_NIX", NixPath);
            Set("NARWHAL_DOCKER", DockerPath);
            Set("FAKE_STATE", State);
            Set("FAKE_IMAGE_NAME", "example/web:latest");
            Set("FAKE_HOST_SYSTEM", Host());
        }

        public string DirectoryPath { get; }
        public string State { get; }
        public string NixPath { get; }
        public string DockerPath { get; }
        public string DockerLogPath => Path.Combine(State, "docker.log");
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

        public void FailLoad() => Set("FAKE_FAIL_LOAD", "1");

        public List<Locked> LockPlatforms(string service)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(LockPath));
            return document.RootElement
                .GetProperty("services").GetProperty(service).GetProperty("platforms")
                .EnumerateObject()
                .Select(entry => new Locked(entry.Name, entry.Value.GetProperty("image_id").GetString()!))
                .ToList();
        }

        public string? TaggedId(string reference)
        {
            var path = Path.Combine(State, "docker", "tags");
            if (!File.Exists(path))
                return null;
            return File.ReadAllLines(path)
                .Where(line => line.Length > 0)
                .Select(line => line.Split('\t'))
                .Where(parts => parts.Length == 2 && parts[0] == reference)
                .Select(parts => parts[1])
                .LastOrDefault();
        }

        public List<(string Id, string Arch)> PushedManifestEntries(string reference)
        {
            var encoded = reference.Replace('/', '_').Replace(':', '_');
            var path = Path.Combine(State, "docker", "pushed", encoded);
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
            File.WriteAllText(path, name == "nix" ? NixScript : DockerScript);
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
            }
        }

        private const string NixScript = """
            #!/usr/bin/env bash
            set -euo pipefail
            state="${FAKE_STATE:?}"
            printf '%s\n' "$*" >> "$state/nix.log"
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
                printf '[{"Config":"%s.json","RepoTags":["%s"],"Layers":[]}]\n' "$hex" "${FAKE_IMAGE_NAME}" > "$tmp/manifest.json"
                : > "$tmp/$hex.json"
                mkdir -p "$state/store"
                out="$state/store/${system}-${service}.tar.gz"
                tar -C "$tmp" --mtime='@0' --owner=0 --group=0 --numeric-owner -czf "$out" manifest.json "$hex.json"
                rm -rf "$tmp"
                printf '%s\n' "$system" > "$out.system"
                printf '%s\n' "$service" > "$out.service"
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
            """;

        private const string DockerScript = """
            #!/usr/bin/env bash
            set -euo pipefail
            state="${FAKE_STATE:?}"
            mkdir -p "$state/docker"
            printf '%s\n' "$*" >> "$state/docker.log"

            settag() {
              local ref="$1" id="$2" tags="$state/docker/tags" tmp="$state/docker/tags.tmp"
              touch "$tags"
              awk -F '\t' -v r="$ref" '$1 != r' "$tags" > "$tmp"
              printf '%s\t%s\n' "$ref" "$id" >> "$tmp"
              mv "$tmp" "$tags"
            }

            tagged() {
              local ref="$1"
              [ -f "$state/docker/tags" ] || return 1
              awk -F '\t' -v r="$ref" '$1 == r { v = $2 } END { if (v != "") print v; else exit 1 }' "$state/docker/tags"
            }

            resolvable() {
              case "$1" in
                sha256:*) [ -f "$state/docker/ids/${1#sha256:}" ] ;;
                *) return 1 ;;
              esac
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

            cmd="${1:-}"; shift || true
            case "$cmd" in
              load)
                artifact=""
                while [ $# -gt 0 ]; do case "$1" in -i) artifact="$2"; shift 2 ;; *) shift ;; esac; done
                if [ "${FAKE_FAIL_LOAD:-0}" = "1" ]; then
                  echo "failed to load image" >&2
                  exit 125
                fi
                system=$(cat "$artifact.system"); service=$(cat "$artifact.service")
                configfile=$(tar -xOf "$artifact" manifest.json)
                configname=$(printf '%s' "$configfile" | grep -o '"Config":"[^"]*"' | head -1 | cut -d'"' -f4)
                confighex=${configname%.json}
                manifesthex=$(printf 'manifest:%s' "$confighex" | sha256sum | cut -d' ' -f1)
                mkdir -p "$state/docker/ids"
                printf '%s\n' "$(arch_of "$system")" > "$state/docker/ids/$manifesthex"
                settag "$FAKE_IMAGE_NAME" "sha256:$manifesthex"
                echo "Loaded image: $FAKE_IMAGE_NAME"
                ;;
              tag)
                src="$1"; ref="$2"
                if resolvable "$src"; then id="$src"
                elif id=$(tagged "$src"); then :
                else echo "Error response from daemon: No such image: $src" >&2; exit 1; fi
                settag "$ref" "$id"
                ;;
              image)
                sub="$1"; shift || true
                case "$sub" in
                  inspect)
                    fmt=""; ref=""
                    while [ $# -gt 0 ]; do case "$1" in --format) fmt="$2"; shift 2 ;; *) ref="$1"; shift ;; esac; done
                    if [ -n "$fmt" ]; then
                      if id=$(tagged "$ref"); then printf '%s\n' "$id"
                      elif resolvable "$ref"; then printf '%s\n' "$ref"
                      else echo "Error: No such image: $ref" >&2; exit 1; fi
                    else
                      tagged "$ref" > /dev/null || resolvable "$ref"
                    fi
                    ;;
                esac
                ;;
              push)
                if [ "${FAKE_FAIL_PUSH:-0}" = "1" ]; then
                  echo "original push failure" >&2
                  exit 125
                fi
                tagged "$1" > /dev/null
                ;;
              manifest)
                sub="$1"; shift || true
                case "$sub" in
                  create)
                    ref=""; ids=()
                    while [ $# -gt 0 ]; do
                      case "$1" in
                        --amend|--insecure) shift ;;
                        *) if [ -z "$ref" ]; then ref="$1"; else ids+=("$1"); fi; shift ;;
                      esac
                    done
                    enc=$(printf '%s' "$ref" | tr '/:' '__')
                    mkdir -p "$state/docker/manifests"
                    : > "$state/docker/manifests/$enc"
                    for id in "${ids[@]}"; do
                      resolvable "$id" || { echo "Error: No such image: $id" >&2; exit 1; }
                      arch=$(cat "$state/docker/ids/${id#sha256:}")
                      printf '%s %s linux\n' "$id" "$arch" >> "$state/docker/manifests/$enc"
                    done
                    ;;
                  push)
                    ref=""
                    while [ $# -gt 0 ]; do case "$1" in --purge|--insecure) shift ;; *) ref="$1"; shift ;; esac; done
                    enc=$(printf '%s' "$ref" | tr '/:' '__')
                    mkdir -p "$state/docker/pushed"
                    cp "$state/docker/manifests/$enc" "$state/docker/pushed/$enc"
                    rm -f "$state/docker/manifests/$enc"
                    ;;
                  rm)
                    ref="$1"; enc=$(printf '%s' "$ref" | tr '/:' '__')
                    rm -f "$state/docker/manifests/$enc"
                    ;;
                esac
                ;;
              network|volume) ;;
              ps) echo '[]' ;;
              container) exit 1 ;;
              inspect) printf '\n' ;;
              run|rm|logs) ;;
            esac
            """;
    }
}
