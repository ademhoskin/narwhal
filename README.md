# narwhal

Reproducible containerized builds for simplified deployment, using **Nix flakes**
to build OCI images and **podman** to run them.

`narwhal` is a single native binary. You describe a **service container** in
`narwhal.toml`, the images come from a Nix flake, and narwhal handles the rest:
build, pin, load, start, health-check, verify, push, tear down.

- **Reproducible by construction.** Images are Nix derivations, so the same
  inputs give the same bytes. `narwhal build` records the Nix content hash and
  the resulting image id in `narwhal.lock.json`; `narwhal verify` rebuilds and
  proves they still match.
- **Fast feedback.** The CLI is .NET 11 compiled ahead-of-time: one ~4 MB
  binary, no runtime, no NuGet at run time.
- **Podman-native.** Rootless containers, named networks, labelled volumes and
  health checks; no compose file, no daemon.

```
narwhal.toml + flake.nix  ->  nix build  ->  podman load  ->  podman run
```

## Requirements

- Nix with flakes enabled (`experimental-features = nix-command flakes`)
- podman 5+

## Install

```sh
nix profile install github:ademhoskin/narwhal     # or:
nix run github:ademhoskin/narwhal -- --help
```

The flake builds `narwhal` as a native AOT binary (~4 MB) with no .NET runtime
dependency. `nix build .#default` in a checkout works too.

## Quickstart

```sh
mkdir hello && cd hello
narwhal init                      # narwhal.toml + flake.nix

nix flake lock                    # pin nixpkgs and narwhal
narwhal build                     # build images, load into podman, write the lock
narwhal up                        # create network, start services, wait for health
curl localhost:8080               # hello from hello
narwhal ps
narwhal logs hello --tail 20
narwhal down
```

`narwhal init` writes a manifest with one service and a flake that builds it with
`narwhal.lib.<system>.mkHttpImage`.

## The service container

```toml
name = "shop"            # project name; prefixes container and network names
flake = "."              # flake that provides the images

[build]
attr_prefix = "narwhal.images"   # image set lives at <prefix>.<system>.<service>
system = "x86_64-linux"          # optional; defaults to the current arch + linux

[deploy]
network = "shop"                 # optional; default narwhal-<project>
restart = "unless-stopped"       # default for every service
wait_seconds = 60                # how long to wait for a dependency

[[service]]
name = "api"                     # required, unique
image = "registry.example.com/shop/api"   # default <project>/<service>
tag = "latest"
attr = "narwhal.images.x86_64-linux.api"  # optional; overrides attr_prefix
flake = "."                      # optional per-service flake (multi-repo)
ports = ["8080:8080", "9090:9090/udp"]
env = { LOG_LEVEL = "info" }
volumes = ["shop-data:/var/lib/api"]
command = ["/bin/api", "serve"]
entrypoint = "/bin/api"
user = "1000:1000"
workdir = "/app"
restart = "always"
depends_on = ["db"]
stop_timeout = 10

[service.healthcheck]
command = "wget -q -O /dev/null http://127.0.0.1:8080/healthz"
interval = 2       # seconds
retries = 20
timeout = 3
```

Set `prebuilt = true` to use an image from a registry instead of building one:

```toml
[[service]]
name = "db"
prebuilt = true
image = "docker.io/library/postgres"
tag = "17-alpine"
volumes = ["shop-db:/var/lib/postgresql/data"]
env = { POSTGRES_DB = "app", POSTGRES_USER = "app", POSTGRES_PASSWORD = "app" }
```

`[[service]]` ordering does not matter: `depends_on` is a DAG, checked for
cycles, and cycles are reported with the path that closes them.

## Images from the flake

Every service with `prebuilt = false` resolves to a flake attribute,
`narwhal.images.<system>.<service>` by default, that must produce a
docker-archive tarball.

```nix
{
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    narwhal.url = "github:ademhoskin/narwhal";
  };

  outputs = { self, nixpkgs, narwhal }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
      forAll = f: nixpkgs.lib.genAttrs systems
        (system: f nixpkgs.legacyPackages.${system} narwhal.lib.${system});
    in {
      narwhal.images = forAll (pkgs: nlib: {
        api = nlib.mkImage {
          inherit pkgs;                  # always pass your own pkgs
          name = "shop/api";
          tag = "latest";
          contents = [ pkgs.busybox ];
          config.Cmd = [ "sh" "-c" "echo api; exec sleep infinity" ];
        };
      });
    };
}
```

Helpers: `mkImage` (layered image from store paths), `mkPackageImage` (single
package payload), `mkHelloImage`, `mkHttpImage` (tiny busybox HTTP server, used
by the example).

> `pkgs` is explicit on purpose: it must be the same nixpkgs as your `contents`,
> otherwise paths in `config.Cmd` would point into a different store path.

## Locking and verification

`narwhal build` writes `narwhal.lock.json`:

```json
{
  "version": 1,
  "manifest_sha256": "…",
  "flakes": {
    ".": { "url": "git+file:///…", "nar_hash": "sha256-…", "revision": "…" }
  },
  "services": {
    "api": {
      "attr": "narwhal.images.x86_64-linux.api",
      "store_path": "/nix/store/…-api.tar.gz",
      "nar_hash": "sha256-…",
      "image_id": "84b6d144…"
    }
  }
}
```

Commit `narwhal.lock.json` **and** `flake.lock`. Then:

```sh
narwhal build --locked   # fail instead of accepting a different build
narwhal verify           # rebuild and compare both fingerprints; exit 4 on drift
```

`verify` is a CI gate: it rebuilds every image, loads it, and fails if the Nix
content hash (`nar_hash`) or podman image id (`image_id`) moved. A changed
`narwhal.toml` is reported as a warning; a changed image is a failure.

## Commands

| command | what it does |
| --- | --- |
| `narwhal init [dir]` | scaffold a manifest and flake |
| `narwhal plan` | services, attrs, images, lock status (`--refresh` queries flakes) |
| `narwhal build [svc…]` | `nix build` each image, `podman load`, write the lock |
| `narwhal up [svc…]` | ensure images, network, volumes, start in dependency order, wait for health |
| `narwhal down [--volumes]` | stop and remove this project's containers (and volumes) |
| `narwhal ps` | container, state, health, ports per service |
| `narwhal logs [svc…] [-f] [--tail n] [--timestamps]` | container logs |
| `narwhal push [svc…]` | push built images to their registries |
| `narwhal verify [svc…]` | prove images reproduce from the lock |
| `narwhal version` | print the version |

Global options: `--file <path>`, `--lock <path>`, `-C/--dir <path>`, `--json`,
`-v/--verbose`, `--no-color`, `-h/--help`.

Exit codes: `0` success, `1` usage/manifest, `2` build failure, `3` podman/tool
failure, `4` reproducibility mismatch, `130` interrupted.

## Development

```sh
nix develop              # .NET 11 SDK, clang, linker
dotnet test tests/Narwhal.Tests
dotnet publish src/Narwhal -c Release -r linux-x64 -o artifacts/aot
```

The AOT publish produces `artifacts/aot/narwhal`, a self-contained binary.

Design notes:

- The TOML parser and lock serializer are hand-written / source-generated so
  trimming and AOT need no reflection.
- Manifests are searched upward from the working directory, like `cargo`.
- Container and volume names carry `io.narwhal.project` / `io.narwhal.service`
  labels, so narwhal only ever touches its own resources.
- `up` reuses the lock when it matches the manifest, and rebuilds the moment the
  manifest or the store path changes.

## License

MIT
