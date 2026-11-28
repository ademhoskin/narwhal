#!/usr/bin/env bash
# Opt-in end-to-end check: run two `narwhal push` processes concurrently for the
# same project/service/reference against one real local registry, then prove the
# published index is complete, matches the lock, and equals a serialized push.
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

host_os="$(uname -s)"
if [ "$host_os" != Linux ] && [ -z "${NARWHAL_SYSTEM:-}" ]; then
  skip "unsupported host OS: $host_os (set NARWHAL_SYSTEM only for a Linux podman host)"
fi

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
  foreign_arch=arm64
else
  foreign_system=x86_64-linux
  foreign_arch=amd64
fi
export NARWHAL_SYSTEM="$host_system"
expected_arches="$(printf '%s\n' "$host_arch" "$foreign_arch" | sort | paste -sd, -)"

if [ -n "${NARWHAL_BIN:-}" ]; then
  NARWHAL_CMD=("$NARWHAL_BIN")
elif [ -n "${NARWHAL_DLL:-}" ]; then
  NARWHAL_CMD=(dotnet "$NARWHAL_DLL")
else
  command -v dotnet >/dev/null || skip "dotnet is not installed"
  NARWHAL_CMD=(dotnet run --project "$repo_root/src/Narwhal" --)
fi

# Preflight used by the xUnit attribute: all static skip conditions, but no
# registry or temporary resources to leak.
if [ "${1:-}" = "--check" ]; then
  "$PODMAN" image exists registry:2 >/dev/null 2>&1 \
    || skip "registry:2 is not available; the e2e needs the image or network access to pull it"
  exit 0
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

  local pid deadline alive ref
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

  "$PODMAN" rm -f "$registry_name" >/dev/null 2>&1 || true
  for ref in ${manifests_to_remove[@]+"${manifests_to_remove[@]}"}; do
    "$PODMAN" manifest rm "$ref" >/dev/null 2>&1 || true
  done
  for ref in ${images_to_remove[@]+"${images_to_remove[@]}"}; do
    "$PODMAN" rmi -f "$ref" >/dev/null 2>&1 || true
  done
  if [ "$pulled_registry" -eq 1 ]; then
    "$PODMAN" rmi -f registry:2 >/dev/null 2>&1 || true
    local query=0
    "$PODMAN" image exists registry:2 >/dev/null 2>&1 || query=$?
    case "$query" in
      1) ;;
      *) printf 'FAIL: registry:2 pulled by the e2e was not removed (query %s)\n' "$query" >&2
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

# Every stage runs as a background job in its own process group, so a signal
# reaches the trap while the stage is running and teardown can group-kill it
# before removing the registry, images, and manifest lists.
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
  check_signal
  untrack_pid "$pid"
  check_signal
  return "$status"
}

# The pull itself is bounded; a timeout is a skip, not a failed e2e.
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
# digest cannot accidentally match an index narwhal publishes.
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
seed_index="$seed_repo-index:latest"
images_to_remove+=("$reference")
manifests_to_remove+=("$seed_index" "$index_name")

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

# Publish a deliberately different index so the concurrent result cannot be a
# leftover from the serialized or seed push.
seed_remote_index() {
  run_tracked "$PODMAN" manifest create --amend "$seed_index" \
    "$seed_repo-amd64:v1" "$seed_repo-arm64:v1" >/dev/null \
    || fail "could not create the seed manifest list"
  run_tracked "$PODMAN" manifest push --all --rm "$seed_index" \
    "docker://$reference" >/dev/null \
    || fail "could not push the seed manifest list"
}

LAST_DIGEST=""
# Validate the remote index against the lock and, when given, against an
# expected digest. A partial, mixed, or duplicated platform set fails here.
validate_index() {
  local label="$1" expected_digest="${2:-}"
  local digest media count arches arch child config expected lock_system
  fetch_index "$label"
  digest="$FETCHED_DIGEST"
  [ -n "$digest" ] || fail "$label: registry returned no Docker-Content-Digest"
  [ -z "$expected_digest" ] || [ "$digest" = "$expected_digest" ] \
    || fail "$label: remote digest is $digest, expected $expected_digest"

  media="$(jq -r '.mediaType' "$work/$label.json")"
  count="$(jq '.manifests | length' "$work/$label.json")"
  arches="$(jq -r '[.manifests[].platform.architecture] | sort | join(",")' "$work/$label.json")"
  [ "$media" = "application/vnd.oci.image.index.v1+json" ] || fail "$label: mediaType is $media"
  [ "$count" -eq 2 ] || fail "$label: expected two index entries, got $count"
  [ "$arches" = "$expected_arches" ] || fail "$label: platforms are $arches, expected $expected_arches"
  printf 'E2E %s remote digest: %s\n' "$label" "$digest"
  printf 'E2E %s index entries: %s\n' "$label" "$arches"

  for arch in amd64 arm64; do
    if [ "$arch" = amd64 ]; then lock_system=x86_64-linux; else lock_system=aarch64-linux; fi
    expected="$(jq -r --arg system "$lock_system" '.services.web.platforms[$system].image_id' "$lock")"
    [ -n "$expected" ] && [ "$expected" != "null" ] || fail "$label: no lock entry for $lock_system"
    child="$(jq -r --arg arch "$arch" '.manifests[] | select(.platform.architecture == $arch) | .digest' "$work/$label.json")"
    [ -n "$child" ] || fail "$label: no $arch entry"
    if ! run_tracked curl -fsS --connect-timeout 2 --max-time 5 \
        -H 'Accept: application/vnd.oci.image.manifest.v1+json' \
        -o "$work/$label-$arch.manifest.json" \
        "http://localhost:$port/v2/e2e/web/manifests/$child"; then
      fail "$label: could not fetch the $arch child manifest"
    fi
    config="$(jq -r '.config.digest' "$work/$label-$arch.manifest.json")"
    [ "$config" = "$expected" ] || fail "$label: $arch config is $config, expected $expected"
    printf 'E2E %s %s manifest: %s config: %s\n' "$label" "$arch" "$child" "$config"
  done
  LAST_DIGEST="$digest"
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

# narwhal build creates the lock (and loads only the host image).
run_stage "narwhal build" "${NARWHAL_CMD[@]}" build -C "$work/project"

lock="$work/project/narwhal.lock.json"
host_id="$(jq -r --arg system "$host_system" '.services.web.platforms[$system].image_id' "$lock")"
foreign_id="$(jq -r --arg system "$foreign_system" '.services.web.platforms[$system].image_id' "$lock")"
[ -n "$host_id" ] && [ "$host_id" != "null" ] || fail "no lock entry for $host_system"
[ -n "$foreign_id" ] && [ "$foreign_id" != "null" ] || fail "no lock entry for $foreign_system"
[ "$host_id" != "$foreign_id" ] || fail "both platforms locked the same image id"
printf 'E2E lock ids: %s=%s %s=%s\n' "$host_system" "$host_id" "$foreign_system" "$foreign_id"

# Baseline: one serialized push of the same lock. The concurrent pushes must
# reproduce this exact remote index digest.
run_stage "serialized narwhal push" "${NARWHAL_CMD[@]}" push -C "$work/project"
fetch_index serialized
serialized_digest="$FETCHED_DIGEST"
validate_index serialized
printf 'E2E serialized remote digest: %s\n' "$serialized_digest"

seed_remote_index
fetch_index seed
seed_digest="$FETCHED_DIGEST"
[ "$seed_digest" != "$serialized_digest" ] \
  || fail "seed index reused the serialized digest; the concurrent comparison would be vacuous"
printf 'E2E seed remote digest: %s\n' "$seed_digest"

# Start both pushes from the same checkout and release them together so they
# actually contend for the local manifest list and the remote tag. Each child
# writes its own start/end/status, and job control puts it in its own process
# group so teardown can kill the whole tree, not just the timeout wrapper.
mkdir -p "$work/logs"
launch_concurrent() {
  local label="$1"
  (
    while [ ! -e "$work/go" ]; do sleep 0.01; done
    local start end status
    start="$(date +%s%N)"
    printf '%s\n' "$start" > "$work/logs/$label.start"
    status=0
    timeout --foreground --signal=TERM --kill-after=5 300 \
      "${NARWHAL_CMD[@]}" push -C "$work/project" || status=$?
    end="$(date +%s%N)"
    printf '%s\n' "$end" > "$work/logs/$label.end"
    printf '%s\n' "$status" > "$work/logs/$label.status"
    exit "$status"
  ) >"$work/logs/$label.log" 2>&1 &
  LAUNCHED_PID=$!
  active_pids+=("$LAUNCHED_PID")
}
launch_concurrent concurrent-a; pid_a="$LAUNCHED_PID"
launch_concurrent concurrent-b; pid_b="$LAUNCHED_PID"
: > "$work/go"

set +e
wait "$pid_a"; status_a=$?
wait "$pid_b"; status_b=$?
set -e
# Both children are reaped; do not let teardown signal reused pids.
active_pids=()

for label in concurrent-a concurrent-b; do
  [ -s "$work/logs/$label.status" ] \
    || fail "$label did not record its exit status (killed?)\n$(cat "$work/logs/$label.log")"
done
start_a="$(cat "$work/logs/concurrent-a.start")"
end_a="$(cat "$work/logs/concurrent-a.end")"
status_recorded_a="$(cat "$work/logs/concurrent-a.status")"
start_b="$(cat "$work/logs/concurrent-b.start")"
end_b="$(cat "$work/logs/concurrent-b.end")"
status_recorded_b="$(cat "$work/logs/concurrent-b.status")"
[ "$status_recorded_a" -eq "$status_a" ] || fail "concurrent-a recorded $status_recorded_a but waited $status_a"
[ "$status_recorded_b" -eq "$status_b" ] || fail "concurrent-b recorded $status_recorded_b but waited $status_b"

[ "$end_a" -gt "$start_a" ] || fail "concurrent-a recorded an empty execution interval"
[ "$end_b" -gt "$start_b" ] || fail "concurrent-b recorded an empty execution interval"
if ((start_a >= end_b || start_b >= end_a)); then
  fail "concurrent pushes did not overlap: a=$start_a..$end_a b=$start_b..$end_b"
fi
printf 'E2E concurrent window: a=%s..%s b=%s..%s (overlap)\n' \
  "$start_a" "$end_a" "$start_b" "$end_b"

require_match() {
  local description="$1" pattern="$2" file="$3" rc=0
  grep -qF -- "$pattern" "$file" || rc=$?
  case "$rc" in
    0) ;;
    1) fail "$description: '$pattern' missing from $file\n$(cat "$file")" ;;
    *) fail "$description: could not scan $file (grep exit $rc)" ;;
  esac
}
require_no_match() {
  local description="$1" pattern="$2" file="$3" rc=0
  grep -qF -- "$pattern" "$file" || rc=$?
  case "$rc" in
    0) fail "$description: found '$pattern' in $file\n$(cat "$file")" ;;
    1) ;;
    *) fail "$description: could not scan $file (grep exit $rc)" ;;
  esac
}

for label in concurrent-a concurrent-b; do
  log="$work/logs/$label.log"
  require_match "$label must push" 'creating multi-platform manifest' "$log"
  require_match "$label must complete the push" 'pushed 1 image(s)' "$log"
  require_no_match "$label must not hit the stale manifest race" 'image not known' "$log"
done
if [ "$status_a" -ne 0 ] || [ "$status_b" -ne 0 ]; then
  fail "concurrent pushes must both exit 0 (a=$status_a b=$status_b)\n--- concurrent-a ---\n$(cat "$work/logs/concurrent-a.log")\n--- concurrent-b ---\n$(cat "$work/logs/concurrent-b.log")"
fi
printf 'E2E concurrent-a: exit=0\n'
printf 'E2E concurrent-b: exit=0\n'

# Whatever the exit codes, the remote index must be the complete locked set and
# byte-for-byte the serialized result.
validate_index concurrent "$serialized_digest"
[ "$LAST_DIGEST" = "$serialized_digest" ] || fail "concurrent digest is $LAST_DIGEST"
printf 'E2E concurrent digest matches serialized push: %s\n' "$serialized_digest"

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

# The local manifest list is a scratch artifact; it must not survive either
# process. Older podman lacks `manifest ls`, so the known names are checked with
# the supported `manifest exists`; the broad scan runs where `manifest ls`
# exists and any failure other than an unknown command is fatal.
for ref in "$index_name" "$seed_index"; do
  require_absent "local manifest list $ref" "$PODMAN" manifest exists "$ref"
done
check_signal
manifest_ls_status=0
manifest_listing="$("$PODMAN" manifest ls 2>&1)" || manifest_ls_status=$?
manifest_ls_supported=0
if [ "$manifest_ls_status" -eq 0 ]; then
  manifest_ls_supported=1
else
  case "$manifest_listing" in
    *"unrecognized command"*|*"unknown command"*) ;;
    *) fail "podman manifest ls failed (exit $manifest_ls_status): $manifest_listing" ;;
  esac
fi
if [ "$manifest_ls_supported" -eq 1 ]; then
  grep_status=0
  leftover_manifests="$(grep -F "narwhal-index/e2e/web-$index_hash" <<<"$manifest_listing")" \
    || grep_status=$?
  case "$grep_status" in
    0) fail "leaked local manifest lists: $leftover_manifests" ;;
    1) ;;
    *) fail "could not scan the local manifest list output (grep exit $grep_status)" ;;
  esac
fi
check_signal
printf 'E2E local manifest lists cleaned up\n'

# Prove the same teardown the EXIT/TERM/INT trap uses leaves nothing behind.
teardown
[ ! -e "$work" ] || fail "leaked work directory: $work"
require_absent "registry container $registry_name" "$PODMAN" container exists "$registry_name"
check_signal
for ref in ${images_to_remove[@]+"${images_to_remove[@]}"}; do
  require_absent "local image $ref" "$PODMAN" image exists "$ref"
done
check_signal
if [ "$pulled_registry" -eq 1 ]; then
  require_absent "registry:2 pulled by the e2e" "$PODMAN" image exists registry:2
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
printf 'E2E cleanup: no leaked container, images, or manifest lists\n'
check_signal
printf 'E2E OK\n'
