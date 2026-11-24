#!/usr/bin/env bash
# Opt-in end-to-end check: build two fake-platform archives, push them through
# `narwhal push` into a real local registry, and inspect the published index.
#
# Exit 0 on success, 77 when podman/registry/curl/jq cannot run (skip).
# Everything temporary is removed on success, failure, and SIGTERM/SIGINT.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PODMAN="${NARWHAL_PODMAN:-podman}"

skip() { printf 'SKIP: %s\n' "$*" >&2; exit 77; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

command -v "$PODMAN" >/dev/null || skip "podman is not installed"
command -v curl >/dev/null || skip "curl is not installed"
command -v jq >/dev/null || skip "jq is not installed"

case "${NARWHAL_SYSTEM:-$(uname -m)}" in
  x86_64|x86_64-linux|amd64)
    host_system=x86_64-linux
    host_arch=amd64
    ;;
  aarch64|arm64|aarch64-linux)
    host_system=aarch64-linux
    host_arch=arm64
    ;;
  *)
    skip "unsupported host architecture: ${NARWHAL_SYSTEM:-$(uname -m)}"
    ;;
esac
if [ "$host_system" = x86_64-linux ]; then
  foreign_system=aarch64-linux
else
  foreign_system=x86_64-linux
fi
export NARWHAL_SYSTEM="$host_system"

if [ -n "${NARWHAL_DLL:-}" ]; then
  narwhal() { dotnet "$NARWHAL_DLL" "$@"; }
else
  command -v dotnet >/dev/null || skip "dotnet is not installed"
  narwhal() { dotnet run --project "$repo_root/src/Narwhal" -- "$@"; }
fi

pulled_registry=0
if ! "$PODMAN" image exists registry:2; then
  "$PODMAN" pull -q docker.io/library/registry:2 >/dev/null 2>&1 \
    || skip "cannot pull registry:2 (no network or no podman storage)"
  pulled_registry=1
fi

run_id="$$-$RANDOM"
work="$(mktemp -d "${TMPDIR:-/tmp}/narwhal-e2e.XXXXXX")"
registry_name="narwhal-e2e-registry-$run_id"
build_repo="localhost/narwhal-e2e-$run_id/build"
seed_repo="localhost/narwhal-e2e-$run_id/seed"
images_to_remove=()
manifests_to_remove=()

cleanup() {
  local status=$?
  "$PODMAN" rm -f "$registry_name" >/dev/null 2>&1 || true
  for ref in "${manifests_to_remove[@]}"; do
    "$PODMAN" manifest rm "$ref" >/dev/null 2>&1 || true
  done
  for ref in "${images_to_remove[@]}"; do
    "$PODMAN" rmi -f "$ref" >/dev/null 2>&1 || true
  done
  if [ "$pulled_registry" -eq 1 ]; then
    "$PODMAN" rmi -f registry:2 >/dev/null 2>&1 || true
  fi
  rm -rf "$work" || true
  exit "$status"
}
trap cleanup EXIT
trap 'exit 143' TERM INT

# Build archives and seed images carry different content, so the seed index
# digest cannot accidentally match the index narwhal publishes.
mkdir -p "$work/archives"
for arch in amd64 arm64; do
  mkdir -p "$work/build-rootfs-$arch"
  printf 'build-%s\n' "$arch" > "$work/build-rootfs-$arch/content"
  tar -C "$work/build-rootfs-$arch" -cf "$work/build-rootfs-$arch.tar" .
  "$PODMAN" import --arch "$arch" --os linux -q "$work/build-rootfs-$arch.tar" "$build_repo-$arch:v1" >/dev/null
  images_to_remove+=("$build_repo-$arch:v1")
  "$PODMAN" save --format docker-archive -o "$work/archives/$arch.tar" "$build_repo-$arch:v1" >/dev/null
done

for arch in amd64 arm64; do
  mkdir -p "$work/seed-rootfs-$arch"
  printf 'seed-%s\n' "$arch" > "$work/seed-rootfs-$arch/content"
  tar -C "$work/seed-rootfs-$arch" -cf "$work/seed-rootfs-$arch.tar" .
  "$PODMAN" import --arch "$arch" --os linux -q "$work/seed-rootfs-$arch.tar" "$seed_repo-$arch:v1" >/dev/null
  images_to_remove+=("$seed_repo-$arch:v1")
done

"$PODMAN" run -d --rm --name "$registry_name" -p 127.0.0.1::5000 registry:2 >/dev/null
port="$("$PODMAN" port "$registry_name" 5000/tcp | awk -F: 'END { print $NF }')"
[ -n "$port" ] || fail "registry did not publish a port"
printf 'E2E registry port: %s\n' "$port"

target="localhost:$port/e2e/web"
reference="$target:latest"
index_hash="$(printf '%s' "$reference" | sha256sum | cut -c1-12)"
index_name="localhost/narwhal-index/e2e/web-$index_hash:latest"
images_to_remove+=("$reference")
manifests_to_remove+=("$seed_repo-index" "$index_name")

ready=0
for _ in $(seq 1 100); do
  if curl -fsS "http://localhost:$port/v2/" >/dev/null 2>&1; then ready=1; break; fi
  sleep 0.1
done
[ "$ready" -eq 1 ] || fail "registry did not answer on localhost:$port"

# Localhost registries are HTTP; point podman at a throwaway insecure config
# instead of changing the machine's registries.conf.
cat > "$work/registries.conf" <<EOF
[registries.search]
registries = []

[registries.insecure]
registries = ["localhost:$port", "127.0.0.1:$port"]
EOF
export CONTAINERS_REGISTRIES_CONF="$work/registries.conf"

# The fake nix returns the real podman archives so the real podman can load,
# tag and push them; no emulation or network builder is involved.
mkdir -p "$work/bin" "$work/store"
cat > "$work/bin/nix" <<'EOS'
#!/usr/bin/env bash
set -euo pipefail
work="${E2E_WORK:?E2E_WORK is required}"
step="${1:-}"
shift || true
case "$step" in
  --version)
    echo "nix (Nix) e2e-fake"
    ;;
  build)
    installable=""
    for arg in "$@"; do case "$arg" in *"#"*) installable="$arg" ;; esac; done
    attr="${installable#*#}"
    IFS='.' read -r -a parts <<< "$attr"
    system=""
    for part in "${parts[@]}"; do case "$part" in *-linux) system="$part" ;; esac; done
    service="${parts[${#parts[@]}-1]}"
    case "$system" in
      x86_64-linux) arch=amd64 ;;
      aarch64-linux) arch=arm64 ;;
      *) echo "fake nix: unsupported system $system" >&2; exit 1 ;;
    esac
    out="$work/store/$service-$system.tar"
    cp "$work/archives/$arch.tar" "$out"
    printf '[{"outputs":{"out":"%s"}}]\n' "$out"
    ;;
  path-info)
    path="${!#}"
    printf '[{"narHash":"sha256-%s"}]\n' "$(sha256sum "$path" | cut -d' ' -f1)"
    ;;
  flake)
    printf '{"resolvedUrl":"git+file:///fake?dir=.","narHash":"sha256-fake","revision":"0123456789abcdef"}\n'
    ;;
  *)
    echo "fake nix: unsupported command $step" >&2
    exit 1
    ;;
esac
EOS
chmod +x "$work/bin/nix"
export E2E_WORK="$work"

mkdir -p "$work/project"
cat > "$work/project/narwhal.toml" <<EOF
name = "e2e"
flake = "."

[build]
systems = ["x86_64-linux", "aarch64-linux"]

[[service]]
name = "web"
image = "$target"
tag = "latest"
EOF

export NARWHAL_NIX="$work/bin/nix"

# A pre-existing remote manifest list built from different images must be
# replaced by the freshly built index. Record its digest so the replacement is
# proven, not inferred from an index that could be the untouched seed.
"$PODMAN" manifest create --amend "$seed_repo-index" "$seed_repo-amd64:v1" "$seed_repo-arm64:v1" >/dev/null
"$PODMAN" manifest push --all --rm "$seed_repo-index" "docker://$reference" >/dev/null

fetch_index() {
  local name="$1"
  curl -fsS -D "$work/$name.headers" -o "$work/$name.json" \
    -H 'Accept: application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json' \
    "http://localhost:$port/v2/e2e/web/manifests/latest"
  awk 'tolower($1) == "docker-content-digest:" { gsub("\r", "", $2); print $2 }' \
    "$work/$name.headers" | tail -1
}

seed_digest="$(fetch_index seed)"
[ -n "$seed_digest" ] || fail "registry returned no Docker-Content-Digest for the seed index"
[ "$(jq '.manifests | length' "$work/seed.json")" -eq 2 ] || fail "pre-existing remote index was not seeded"
printf 'E2E seed remote digest: %s\n' "$seed_digest"

narwhal build -C "$work/project"
narwhal push -C "$work/project"

lock="$work/project/narwhal.lock.json"
host_id="$(jq -r --arg system "$host_system" '.services.web.platforms[$system].image_id' "$lock")"
foreign_id="$(jq -r --arg system "$foreign_system" '.services.web.platforms[$system].image_id' "$lock")"
[ -n "$host_id" ] && [ "$host_id" != "null" ] || fail "no lock entry for $host_system"
[ -n "$foreign_id" ] && [ "$foreign_id" != "null" ] || fail "no lock entry for $foreign_system"
[ "$host_id" != "$foreign_id" ] || fail "both platforms locked the same image id"
printf 'E2E lock ids: %s=%s %s=%s\n' "$host_system" "$host_id" "$foreign_system" "$foreign_id"

inspect_index() {
  local label="$1"
  local digest index media count arches
  digest="$(fetch_index "$label")"
  [ -n "$digest" ] || fail "$label: registry returned no Docker-Content-Digest"
  [ "$digest" != "$seed_digest" ] || fail "$label: remote digest is still the seed index"
  printf 'E2E %s remote digest: %s\n' "$label" "$digest"

  index="$(cat "$work/$label.json")"
  media="$(jq -r '.mediaType' <<< "$index")"
  count="$(jq '.manifests | length' <<< "$index")"
  arches="$(jq -r '[.manifests[].platform.architecture] | sort | join(",")' <<< "$index")"
  [ "$media" = "application/vnd.oci.image.index.v1+json" ] || fail "$label: mediaType is $media"
  [ "$count" -eq 2 ] || fail "$label: expected two index entries, got $count"
  [ "$arches" = "amd64,arm64" ] || fail "$label: platforms are $arches"
  printf 'E2E %s mediaType: %s\n' "$label" "$media"
  printf 'E2E %s entries: %s\n' "$label" "$arches"

  local arch digest_of_child manifest config expected
  for arch in amd64 arm64; do
    if [ "$arch" = amd64 ]; then expected="$(jq -r --arg system x86_64-linux '.services.web.platforms[$system].image_id' "$lock")"
    else expected="$(jq -r --arg system aarch64-linux '.services.web.platforms[$system].image_id' "$lock")"
    fi
    digest_of_child="$(jq -r --arg arch "$arch" '.manifests[] | select(.platform.architecture == $arch) | .digest' <<< "$index")"
    [ -n "$digest_of_child" ] || fail "$label: no $arch entry"
    manifest="$(curl -fsS -H 'Accept: application/vnd.oci.image.manifest.v1+json' \
      "http://localhost:$port/v2/e2e/web/manifests/$digest_of_child")"
    config="$(jq -r '.config.digest' <<< "$manifest")"
    [ "$config" = "$expected" ] || fail "$label: $arch config is $config, expected $expected"
    printf 'E2E %s %s manifest: %s config: %s\n' "$label" "$arch" "$digest_of_child" "$config"
  done
}

inspect_index "push-1"

host_tag="$("$PODMAN" image inspect --format '{{.Id}}|{{.Architecture}}' "$reference")"
[ "$host_tag" = "${host_id#sha256:}|$host_arch" ] || fail "local tag is $host_tag, expected ${host_id#sha256:}|$host_arch"
printf 'E2E local tag: %s\n' "$host_tag"

# The remote tag must still be the index, not the host image, after the local
# tag restore; pushing again must keep it an index and keep replacing the seed.
narwhal push -C "$work/project"
inspect_index "push-2"
host_tag="$("$PODMAN" image inspect --format '{{.Id}}|{{.Architecture}}' "$reference")"
[ "$host_tag" = "${host_id#sha256:}|$host_arch" ] || fail "local tag after re-push is $host_tag"
printf 'E2E local tag after re-push: %s\n' "$host_tag"

printf 'E2E OK\n'
