#!/usr/bin/env bash
# Opt-in end-to-end check: build two fake-platform archives, push them through
# `narwhal push` into a real local registry, and inspect the published index.
#
# Exit 0 on success, 77 when podman/registry/curl/jq cannot run (skip).
# Everything temporary is removed on success, failure, and SIGTERM/SIGINT.
set -euo pipefail

skip() { printf 'SKIP: %s\n' "$*" >&2; exit 77; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

if ((BASH_VERSINFO[0] < 4 || (BASH_VERSINFO[0] == 4 && BASH_VERSINFO[1] < 4))); then
  skip "bash >= 4.4 is required (found ${BASH_VERSION:-unknown})"
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PODMAN="${NARWHAL_PODMAN:-podman}"

for tool in "$PODMAN" curl jq timeout sha256sum seq date awk tar mktemp; do
  command -v "$tool" >/dev/null || skip "$tool is not installed"
done

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

if [ -n "${NARWHAL_BIN:-}" ]; then
  NARWHAL_CMD=("$NARWHAL_BIN")
elif [ -n "${NARWHAL_DLL:-}" ]; then
  NARWHAL_CMD=(dotnet "$NARWHAL_DLL")
else
  command -v dotnet >/dev/null || skip "dotnet is not installed"
  NARWHAL_CMD=(dotnet run --project "$repo_root/src/Narwhal" --)
fi

run_id="$$-$RANDOM"
work=""
registry_name="narwhal-e2e-registry-$run_id"
build_repo="localhost/narwhal-e2e-$run_id/build"
seed_repo="localhost/narwhal-e2e-$run_id/seed"
images_to_remove=()
manifests_to_remove=()
active_pids=()
pulled_registry="${NARWHAL_E2E_REGISTRY_PULLED_BY_ATTRIBUTE:-0}"
teardown_done=0
teardown_failed=0
signal_pending=0
on_signal() { signal_pending=1; }
check_signal() { [ "$signal_pending" -eq 0 ] || exit 143; }

# Idempotent: the explicit final check and the EXIT trap both call it.
teardown() {
  [ "$teardown_done" -eq 0 ] || return 0
  teardown_done=1

  local pid deadline alive ref query
  for pid in ${active_pids[@]+"${active_pids[@]}"}; do
    if jobs -pr | grep -qx "$pid"; then
      kill -TERM -- "-$pid" 2>/dev/null || kill -TERM "$pid" 2>/dev/null || true
    fi
  done
  deadline=$((SECONDS + 8))
  while [ "$SECONDS" -lt "$deadline" ]; do
    alive=0
    for pid in ${active_pids[@]+"${active_pids[@]}"}; do
      if kill -0 "$pid" 2>/dev/null; then alive=1; fi
    done
    [ "$alive" -eq 0 ] && break
    sleep 0.2
  done
  for pid in ${active_pids[@]+"${active_pids[@]}"}; do
    if jobs -pr | grep -qx "$pid"; then
      kill -KILL -- "-$pid" 2>/dev/null || kill -KILL "$pid" 2>/dev/null || true
    fi
  done
  for pid in ${active_pids[@]+"${active_pids[@]}"}; do
    wait "$pid" 2>/dev/null || true
  done
  active_pids=()

  query=0
  "$PODMAN" container exists "$registry_name" >/dev/null 2>&1 || query=$?
  case "$query" in
    0) "$PODMAN" rm -f "$registry_name" >/dev/null 2>&1 \
         || { printf 'FAIL: could not remove registry container %s\n' "$registry_name" >&2
              teardown_failed=1; } ;;
    1) ;;
    *) printf 'FAIL: could not query registry container %s (exit %s)\n' "$registry_name" "$query" >&2
       teardown_failed=1 ;;
  esac
  for ref in ${manifests_to_remove[@]+"${manifests_to_remove[@]}"}; do
    query=0
    "$PODMAN" manifest exists "$ref" >/dev/null 2>&1 || query=$?
    case "$query" in
      0) "$PODMAN" manifest rm "$ref" >/dev/null 2>&1 \
           || { printf 'FAIL: could not remove manifest list %s\n' "$ref" >&2
                teardown_failed=1; } ;;
      1) ;;
      *) printf 'FAIL: could not query manifest list %s (exit %s)\n' "$ref" "$query" >&2
         teardown_failed=1 ;;
    esac
  done
  for ref in ${images_to_remove[@]+"${images_to_remove[@]}"}; do
    query=0
    "$PODMAN" image exists "$ref" >/dev/null 2>&1 || query=$?
    case "$query" in
      0) "$PODMAN" rmi -f "$ref" >/dev/null 2>&1 \
           || { printf 'FAIL: could not remove image %s\n' "$ref" >&2
                teardown_failed=1; } ;;
      1) ;;
      *) printf 'FAIL: could not query image %s (exit %s)\n' "$ref" "$query" >&2
         teardown_failed=1 ;;
    esac
  done
  if [ "$pulled_registry" -eq 1 ]; then
    query=0
    "$PODMAN" image exists registry:2 >/dev/null 2>&1 || query=$?
    case "$query" in
      0) "$PODMAN" rmi -f registry:2 >/dev/null 2>&1 \
           || { printf 'FAIL: could not remove registry:2\n' >&2
                teardown_failed=1; } ;;
      1) ;;
      *) printf 'FAIL: could not query registry:2 (exit %s)\n' "$query" >&2
         teardown_failed=1 ;;
    esac
  fi
  if [ -n "$work" ]; then
    rm -rf "$work" || { printf 'FAIL: could not remove work directory %s\n' "$work" >&2
                          teardown_failed=1; }
  fi
  return 0
}
cleanup() {
  local status=$?
  teardown
  [ "$teardown_failed" -eq 0 ] || status=1
  exit "$status"
}
trap cleanup EXIT
trap on_signal TERM INT

work="$(mktemp -d "${TMPDIR:-/tmp}/narwhal-e2e.XXXXXX")"
check_signal

# Every stage runs as a background job in its own process group so a signal
# reaches the trap while the stage is running and teardown can group-kill it.
set -m
track_pid() { active_pids+=("$1"); }
untrack_pid() {
  local target="$1" pid remaining=()
  for pid in ${active_pids[@]+"${active_pids[@]}"}; do
    [ "$pid" = "$target" ] || remaining+=("$pid")
  done
  active_pids=(${remaining[@]+"${remaining[@]}"})
}
run_tracked() {
  local status=0 pid
  "$@" &
  pid=$!
  track_pid "$pid"
  check_signal
  wait "$pid" || status=$?
  # On a signal wait can return while the child is still alive; keep it
  # tracked so teardown can group-kill it.
  check_signal
  untrack_pid "$pid"
  check_signal
  return "$status"
}
run_stage() {
  local what="$1"
  shift
  local status=0
  run_tracked timeout --foreground --signal=TERM --kill-after=5 300 "$@" || status=$?
  case "$status" in
    0) ;;
    124|137|143) fail "$what hung and was terminated (exit $status)" ;;
    *) fail "$what exited $status" ;;
  esac
}

# The pull is bounded and tracked; a timeout is a skip, not a failed e2e.
pull_registry() {
  run_tracked "$PODMAN" image exists registry:2 >/dev/null 2>&1 && return 0
  # Record intent before starting: a TERM during the pull must still remove
  # any partial registry:2 image in teardown.
  pulled_registry=1
  local status=0
  run_tracked timeout --foreground --signal=TERM --kill-after=10 120 \
    "$PODMAN" pull -q docker.io/library/registry:2 >/dev/null 2>&1 || status=$?
  case "$status" in
    0) ;;
    124|137|143) skip "timed out pulling registry:2" ;;
    *) skip "cannot pull registry:2 (no network or no podman storage)" ;;
  esac
}
pull_registry

# Build archives and seed images carry different content, so the seed index
# digest cannot accidentally match the index narwhal publishes.
mkdir -p "$work/archives"
for arch in amd64 arm64; do
  mkdir -p "$work/build-rootfs-$arch"
  printf 'build-%s\n' "$arch" > "$work/build-rootfs-$arch/content"
  tar -C "$work/build-rootfs-$arch" -cf "$work/build-rootfs-$arch.tar" .
  run_tracked "$PODMAN" import --arch "$arch" --os linux -q \
    "$work/build-rootfs-$arch.tar" "$build_repo-$arch:v1" >/dev/null \
    || fail "podman import failed for $build_repo-$arch:v1"
  images_to_remove+=("$build_repo-$arch:v1")
  run_tracked "$PODMAN" save --format docker-archive \
    -o "$work/archives/$arch.tar" "$build_repo-$arch:v1" >/dev/null \
    || fail "podman save failed for $build_repo-$arch:v1"
done

for arch in amd64 arm64; do
  mkdir -p "$work/seed-rootfs-$arch"
  printf 'seed-%s\n' "$arch" > "$work/seed-rootfs-$arch/content"
  tar -C "$work/seed-rootfs-$arch" -cf "$work/seed-rootfs-$arch.tar" .
  run_tracked "$PODMAN" import --arch "$arch" --os linux -q \
    "$work/seed-rootfs-$arch.tar" "$seed_repo-$arch:v1" >/dev/null \
    || fail "podman import failed for $seed_repo-$arch:v1"
  images_to_remove+=("$seed_repo-$arch:v1")
done

run_tracked "$PODMAN" run -d --rm --name "$registry_name" \
  -p 127.0.0.1::5000 registry:2 >/dev/null \
  || fail "could not start the registry container"
port_raw_file="$work/registry.port"
run_tracked "$PODMAN" port "$registry_name" 5000/tcp >"$port_raw_file" \
  || fail "registry did not publish a port"
port="$(awk -F: 'END { print $NF }' "$port_raw_file")"
[ -n "$port" ] || fail "registry did not publish a port"
printf 'E2E registry port: %s\n' "$port"

target="localhost:$port/e2e/web"
reference="$target:latest"
index_hash="$(printf '%s' "$reference" | sha256sum | cut -c1-12)"
index_name="localhost/narwhal-index/e2e/web-$index_hash:latest"
images_to_remove+=("$reference")
manifests_to_remove+=("$seed_repo-index" "$index_name")

deadline=$((SECONDS + 60))
ready=0
while [ "$SECONDS" -lt "$deadline" ]; do
  if run_tracked curl -fsS --connect-timeout 2 --max-time 5 \
      "http://localhost:$port/v2/" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 0.1
done
[ "$ready" -eq 1 ] || fail "registry did not answer on localhost:$port within 60s"

# Localhost registries are HTTP; point podman at a throwaway insecure config
# instead of changing the machine's registries.conf.
cat > "$work/registries.conf" <<EOF
unqualified-search-registries = []

[[registry]]
location = "localhost:$port"
insecure = true

[[registry]]
location = "127.0.0.1:$port"
insecure = true
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
run_tracked "$PODMAN" manifest create --amend "$seed_repo-index" \
  "$seed_repo-amd64:v1" "$seed_repo-arm64:v1" >/dev/null \
  || fail "could not create the seed manifest list"
run_tracked "$PODMAN" manifest push --all --rm "$seed_repo-index" \
  "docker://$reference" >/dev/null \
  || fail "could not push the seed manifest list"

FETCHED_DIGEST=""
fetch_index() {
  local name="$1"
  run_tracked curl -fsS --connect-timeout 2 --max-time 5 \
    -D "$work/$name.headers" -o "$work/$name.json" \
    -H 'Accept: application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json' \
    "http://localhost:$port/v2/e2e/web/manifests/latest" \
    || fail "$name: could not fetch the remote index"
  FETCHED_DIGEST="$(awk 'tolower($1) == "docker-content-digest:" { gsub("\r", "", $2); print $2 }' \
    "$work/$name.headers" | tail -1)"
}

fetch_index seed
seed_digest="$FETCHED_DIGEST"
[ -n "$seed_digest" ] || fail "registry returned no Docker-Content-Digest for the seed index"
[ "$(jq '.manifests | length' "$work/seed.json")" -eq 2 ] || fail "pre-existing remote index was not seeded"
printf 'E2E seed remote digest: %s\n' "$seed_digest"

run_stage "narwhal build" "${NARWHAL_CMD[@]}" build -C "$work/project"
run_stage "narwhal push" "${NARWHAL_CMD[@]}" push -C "$work/project"

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
  fetch_index "$label"
  digest="$FETCHED_DIGEST"
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
    if ! run_tracked curl -fsS --connect-timeout 2 --max-time 5 \
        -H 'Accept: application/vnd.oci.image.manifest.v1+json' \
        -o "$work/$label-$arch.manifest.json" \
        "http://localhost:$port/v2/e2e/web/manifests/$digest_of_child"; then
      fail "$label: could not fetch the $arch child manifest"
    fi
    manifest="$(cat "$work/$label-$arch.manifest.json")"
    config="$(jq -r '.config.digest' <<< "$manifest")"
    [ "$config" = "$expected" ] || fail "$label: $arch config is $config, expected $expected"
    printf 'E2E %s %s manifest: %s config: %s\n' "$label" "$arch" "$digest_of_child" "$config"
  done
}

inspect_index "push-1"

run_tracked "$PODMAN" image inspect --format '{{.Id}}|{{.Architecture}}' "$reference" \
  >"$work/host.tag" || fail "could not inspect the local host tag"
host_tag="$(cat "$work/host.tag")"
[ "$host_tag" = "${host_id#sha256:}|$host_arch" ] || fail "local tag is $host_tag, expected ${host_id#sha256:}|$host_arch"
printf 'E2E local tag: %s\n' "$host_tag"

# The remote tag must still be the index, not the host image, after the local
# tag restore; pushing again must keep it an index and keep replacing the seed.
run_stage "narwhal re-push" "${NARWHAL_CMD[@]}" push -C "$work/project"
inspect_index "push-2"
run_tracked "$PODMAN" image inspect --format '{{.Id}}|{{.Architecture}}' "$reference" \
  >"$work/host.tag" || fail "could not inspect the local host tag"
host_tag="$(cat "$work/host.tag")"
[ "$host_tag" = "${host_id#sha256:}|$host_arch" ] || fail "local tag after re-push is $host_tag"
printf 'E2E local tag after re-push: %s\n' "$host_tag"

# Fail closed: 0 means present (leak), 1 means absent, anything else means the
# question could not be answered and is a failure too.
require_absent() {
  local description="$1"
  shift
  local status=0
  "$@" >/dev/null 2>&1 || status=$?
  case "$status" in
    0) fail "leaked $description" ;;
    1) ;;
    *) fail "could not verify $description: query exited $status" ;;
  esac
}

# Prove teardown removes what this run created.
teardown
[ ! -e "$work" ] || fail "leaked work directory: $work"
[ "$teardown_failed" -eq 0 ] || fail "cleanup left resources behind"
require_absent "registry container $registry_name" "$PODMAN" container exists "$registry_name"
check_signal
for ref in ${manifests_to_remove[@]+"${manifests_to_remove[@]}"}; do
  require_absent "local manifest list $ref" "$PODMAN" manifest exists "$ref"
done
check_signal
for ref in ${images_to_remove[@]+"${images_to_remove[@]}"}; do
  require_absent "local image $ref" "$PODMAN" image exists "$ref"
done
check_signal
if [ "$pulled_registry" -eq 1 ]; then
  require_absent "registry:2 pulled by the e2e" "$PODMAN" image exists registry:2
  printf 'E2E registry:2 removed\n'
fi
check_signal

set +e
image_listing="$("$PODMAN" image ls --format '{{.Repository}}:{{.Tag}}')"
image_status=$?
set -e
[ "$image_status" -eq 0 ] || fail "could not list local images (exit $image_status)"
grep_status=0
leftover_images="$(grep -F "narwhal-e2e-$run_id/" <<<"$image_listing")" || grep_status=$?
case "$grep_status" in
  0) fail "leaked local images: $leftover_images" ;;
  1) ;;
  *) fail "could not scan the local image list (grep exit $grep_status)" ;;
esac
check_signal

manifest_ls_status=0
manifest_listing="$("$PODMAN" manifest ls 2>&1)" || manifest_ls_status=$?
if [ "$manifest_ls_status" -eq 0 ]; then
  grep_status=0
  leftover_manifests="$(grep -F "narwhal-index/e2e/web-$index_hash" <<<"$manifest_listing")" \
    || grep_status=$?
  case "$grep_status" in
    0) fail "leaked local manifest lists: $leftover_manifests" ;;
    1) ;;
    *) fail "could not scan the local manifest list output (grep exit $grep_status)" ;;
  esac
else
  case "$manifest_listing" in
    *"unrecognized command"*|*"unknown command"*) ;;
    *) fail "podman manifest ls failed (exit $manifest_ls_status): $manifest_listing" ;;
  esac
fi
printf 'E2E cleanup: no leaked container, images, or manifest lists\n'
check_signal
printf 'E2E OK\n'
