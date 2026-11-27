# narwhal cloud provisioning (OpenTofu)

OpenTofu modules that provision one narwhal target on AWS, DigitalOcean, or
Google Cloud, implementing `docs/cloud.md` section 4. Azure is out of scope.
Nothing in these modules touches `src/` or the provider-neutral runtime.

## Layout

```text
deploy/opentofu/
  modules/
    host/              shared idempotent host preparation and bootstrap delivery
    aws/               VPC, EIP, EC2, IAM profile, ECR, empty-ingress SG, optional SSM/CloudWatch
    digitalocean/      Droplet, VPC, DOCR, egress-only firewall, optional reserved IP/monitor alert
    gcp/               VPC, instance, service account, Artifact Registry, egress-only firewall, optional IP/sink
  roots/
    aws/               runnable root for module/aws
    digitalocean/      runnable root for module/digitalocean
    gcp/               runnable root for module/gcp
```

Every module is standalone and calls `modules/host` to render the first-boot
script. There is no provider-specific code outside the three provider modules.

## Module contract

All three modules accept this shared surface:

| Input | Meaning |
| --- | --- |
| `project` | narwhal project name, used in registry paths; matches the manifest rule `^[a-z0-9]([a-z0-9._-]{0,61}[a-z0-9])?$` |
| `target_name` | target name, exposed as `target.selector.stack` |
| `region` | provider region |
| `arch` | must be `amd64` |
| `trust_keys` | boot pins, map of 16-hex key id to `{ public_key = base64(32-byte Ed25519 key), not_after = RFC3339 Z }` |
| `status_keys` | registered per-host status keys, same map shape |
| `status_key_secret` / `status_key_secret_name` / `status_key_secret_id` | provider secret-service reference to the private status key |
| `agent_image`, `agent_digest`, `agent_binary_sha256` | digest-pinned agent boot pins |
| `agent_mode` | `daemon` or `timer` |
| `service_images` | service image names that need registry repositories |
| `image_registry`, `state_registry` | optional registry endpoint overrides |
| `bootstrap_payload_ref` | optional opaque reference to an external bootstrap payload |
| `allow_docr_state_registry` | DigitalOcean only; permits a DOCR state registry in a separate DigitalOcean account when true |

Each module exposes the same outputs:

| Output | Contents |
| --- | --- |
| `target` | `{ provider, region, arch, selector = { stack }, id }`, where `id` is `<provider>-<region>-<arch>-<selector-hash>` per 2.1 and `selector-hash` is the first 8 lowercase hex characters of SHA-256(JCS(selector)) |
| `public_address` | EIP, reserved IP, or static address; otherwise the VM public address |
| `firewall` | `{ id, kind }` for the managed firewall |
| `image_registry` | image registry endpoint |
| `state_registry` | state registry endpoint |
| `registry_repositories` | `{ images, desired, status }`; image values are full references, state values are `registry/relative-path` |
| `status_key_secret` | non-secret reference to the private status key |
| `bootstrap` | the payload described below |

`bootstrap` mirrors `docs/cloud.md` 4.5:

```hcl
{
  provider            = "aws" | "digitalocean" | "gcp"
  project, target_id, desired_repo, status_repo
  trust_keys, status_keys
  mode, image_registry, state_registry
  agent_image, agent_digest, agent_binary_sha256
  payload_ref         = optional, default ""
  status_key_secret   = non-secret secret-service reference
  state_registry_credential_secret = DigitalOcean only, non-secret reference
}
```

`target_id` follows `docs/cloud.md` 2.1:
`<provider>-<region>-<arch>-<selector-hash>`, lowercased with every character
outside `[a-z0-9-]` replaced by `-`. The selector is `{ stack = target_name }`,
so for `target_name = "prod"` the hash is the first 8 hex characters of
SHA-256 of `{"stack":"prod"}`. The same `target_id` is used for desired and
status repository paths, bootstrap metadata, and resource names.

Boot pins are written as root-owned files in the design's two-line format:

```text
narwhal-ed25519-v1 <base64(32-byte public key)>
not_after=2027-11-24T00:00:00Z
```

Key IDs are the first 8 bytes of SHA-256 of the 32-byte public key, encoded as
16 lowercase hex characters. The tfvars examples contain generated test public
keys and IDs derived that way; no private key is committed. The same
registration shape is used for `status_keys` in `bootstrap.json`.

No output contains a secret value. The DigitalOcean bootstrap token is written
only to `/etc/narwhal/do-token` in the Droplet `user_data`; it is not in
`bootstrap`, not in `status_key_secret`, and not in `state_registry_credential_secret`.
That token is necessarily present in OpenTofu state through `user_data`, so the
state backend must be protected as a secret store and never committed.

## Security invariants

- No module has an ingress variable. There is no way to declare data-plane or
  control-plane ingress through OpenTofu. `narwhal deploy` reconciles ingress
  after bootstrap using `firewall.id`.
- AWS creates an `aws_security_group` with no ingress rules and separate egress
  rules for TCP 443/80, TCP+UDP 53, and UDP 123. No `key_name` is set.
- DigitalOcean creates a `digitalocean_firewall` with no inbound rule blocks and
  egress rules for TCP 443/80, TCP+UDP 53, and UDP 123. `ssh_keys = []`.
- GCP creates a custom VPC with no default rules, no ingress rule, and an
  explicit egress allow at priority 1000 for TCP 443/80, TCP/UDP 53, and UDP
  123 plus a deny-all egress rule at priority 65000. That deny closes the
  implied allow-all at 65535. `block-project-ssh-keys=TRUE`, no SSH keys, no
  port 22.
- AWS requires IMDSv2 (`http_tokens = "required"`). GCP and DigitalOcean rely on
  provider metadata; the container-network metadata block and probe are runtime
  concerns and are not implemented here.
- IAM is least privilege: ECR read for images and desired state, write for
  status, `ecr:GetAuthorizationToken` on `*`, `ssm:GetParameter` on the status
  and workload parameters, scoped `kms:Decrypt`, and an explicit
  `Deny ssm:SendCommand on *`. GCP grants repository-scoped Artifact Registry
  reader/writer and `secretmanager.secretAccessor` on named secrets.
- The private status key is accepted as an existing secret-service reference and
  is never read by OpenTofu. This is the documented fallback in 4.5: generating
  it in OpenTofu would leave the private key in state. Store it with a pinned
  operator tool before applying.
- Registries: AWS creates mutable-tag ECR repositories for `narwhal`, every
  `service_images` entry, desired state, and status. GCP creates three Artifact
  Registry repositories (images, desired, status) so status writes cannot touch
  desired state. DigitalOcean creates DOCR for images and requires a
  `state_registry` that differs from `image_registry` plus a non-empty
  `state_registry_credential_secret_name`. DOCR creates repositories on first
  push.
- DigitalOcean state registry defaults to a non-DOCR registry. A DOCR state
  registry is accepted only with `allow_docr_state_registry = true`, which
  asserts that it belongs to a separate DigitalOcean account with its own
  credential; same-account DOCR remains invalid because DOCR credentials are
  account-wide.
- DigitalOcean canonicalizes both registry endpoints before comparing them or
  testing for DOCR: the host is lowercased and stripped of trailing dots, the
  repository path is preserved, and schemes, explicit ports, trailing slashes,
  and empty path segments are rejected.

## GCP repository path extension

The 4.5 example uses `desired_repo = "${project}/state/${target_id}/desired"`.
Artifact Registry IAM is scoped per repository, and a repository ID cannot
contain `/`. To keep the status repository separate from the desired
repository, GCP returns `desired_repo` and `status_repo` prefixed with the
repository ID (for example `narwhal-desired/<project>/state/<target_id>/desired`)
and `state_registry` as `<region>-docker.pkg.dev/<gcp_project>`. AWS and
DigitalOcean use the literal 4.5 form. The `bootstrap` keys and semantics are
unchanged.

## Bootstrap boundary

`modules/host` renders an idempotent root shell script used as cloud-init
user-data on AWS and DigitalOcean and as the GCP startup script. It:

- installs `podman`, `uidmap`, `fuse-overlayfs`, `slirp4netns`, `ca-certificates`,
  `curl`, and `jq` on first boot;
- creates the non-login `narwhal` user, subordinate UID/GID ranges, and linger;
- creates `/etc/narwhal`, `/etc/narwhal/trust`, `/var/lib/narwhal`, `/srv/narwhal`;
- writes `/etc/narwhal/bootstrap.json` and `/etc/narwhal/trust/<key_id>.pub`
  atomically and only when the bytes differ; pin files use the
  `narwhal-ed25519-v1` + `not_after` two-line format;
- writes the DigitalOcean token file on DigitalOcean only.

It does not install the agent binary, fetch the status key, or enable the user
units. Those are steps 3-9 of `docs/cloud.md` 5.1 and belong to the narwhal
runtime, which is not part of this repository. The script is idempotent on every
boot, and the GCP precondition rejects a script larger than the 256 KB limit.

## Usage

```sh
cd deploy/opentofu/roots/aws
cp terraform.tfvars.example terraform.tfvars   # then edit
tofu init
tofu plan
tofu apply

# DigitalOcean: keep the token out of tfvars.
export DIGITALOCEAN_TOKEN=...                  # provider credential
export TOFU_VAR_bootstrap_token=dop_v1_...     # least-privilege user-data token
tofu apply

# GCP: Application Default Credentials.
gcloud auth application-default login
```

Provider credentials for the deploy principal (firewall write, registry push)
are supplied outside these modules, as described in 4.2-4.4.

## Versions and sources

Pinned in each module and root:

| Provider | Constraint | Checked against registry |
| --- | --- | --- |
| OpenTofu | `>= 1.11.0` | 1.11.8 |
| `hashicorp/aws` | `~> 6.0` | 6.74.0 available |
| `digitalocean/digitalocean` | `~> 2.0` | 2.109.0 available |
| `hashicorp/google` | `~> 8.0` | 8.12.0 available |

Ubuntu image sources are not hard-coded IDs:

- AWS: Canonical SSM public parameter
  `/aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id`.
- DigitalOcean: `ubuntu-24-04-x64`.
- GCP: image family `ubuntu-os-cloud/ubuntu-2404-lts-amd64`.

## Validation

Run on 2026-11-24 with OpenTofu 1.11.8 from `nixpkgs`, from the
repository root:

```sh
nix shell nixpkgs#opentofu -c tofu fmt -check -recursive deploy/opentofu
cd deploy/opentofu/roots/aws && nix shell nixpkgs#opentofu -c tofu init -backend=false && nix shell nixpkgs#opentofu -c tofu validate
cd ../digitalocean && nix shell nixpkgs#opentofu -c tofu init -backend=false && nix shell nixpkgs#opentofu -c tofu validate
cd ../gcp && nix shell nixpkgs#opentofu -c tofu init -backend=false && nix shell nixpkgs#opentofu -c tofu validate
```

Results: formatting clean; all three roots `Success! The configuration is
valid.` Providers resolved to `hashicorp/aws` 6.74.0,
`digitalocean/digitalocean` 2.109.0, and `hashicorp/google` 8.12.0. No plan or
apply was run, so cloud APIs, credentials, AMI resolution, and quota behavior
were not exercised.

A scratch root also planned `modules/host` with sample inputs. The rendered
script contained the two-line `narwhal-ed25519-v1 ...` / `not_after=...Z` pin,
the per-package install loop, and the `{"stack":"prod"}` selector hash
`37cbdd65` matched the expected 8 hex characters.

A second scratch root evaluated the DigitalOcean canonicalization: uppercase and
trailing-dot DOCR hosts collapsed to `registry.digitalocean.com`, an explicit
port failed validation, and repository path case was preserved.

## Verified versus assumed

Verified by the sources in `docs/cloud.md` 4.7: Canonical SSM AMI hierarchy,
IMDSv2 and user-data behavior, ECR/Artifact Registry repositories and auth,
DigitalOcean metadata and user-data, `ubuntu-24-04-x64`, DOCR, DO Secrets
Manager being team-token based, GCP metadata and service-account tokens, GCP
startup script limits, Secret Manager IAM, GCP Ubuntu image family, firewall
source/destination family split, and separate AWS `cidr_blocks` /
`ipv6_cidr_blocks`.

Assumed and not verified here, carried from 4.7:

- The Ubuntu 24.04 rootless podman package set.
- `podman-restart.service` packaging (these modules do not install it).
- DigitalOcean metadata being enabled by default for new Droplets.
- DOCR accepting a fallback OCI manifest with a custom `artifactType` (runtime
  concern, not provisioning).
- ECR and Artifact Registry accepting the fallback desired/status layout
  (runtime concern).
- Provider firewall APIs allowing the deploy principal to reconcile rules.
- GCP container access to the metadata server and whether the metadata path is
  affected by the VPC egress rule.
- AWS hop limit 1 alone not blocking userspace networking. This module sets
  IMDSv2 and hop limit 1 but does not add the container-network block or probe.
- GCP firewall evaluation order for the priority-1000 allow and priority-65000
  deny-all egress rules is not exercised in a real project here. The rules
  follow the documented first-match semantics; the deny exists specifically so
  the implied allow-egress at 65535 is not reached.

Additional assumptions specific to this implementation:

- The Canonical `current` serial alias resolves to a valid Ubuntu 24.04 amd64
  AMI at plan time. The design says to use the serial current at plan time; the
  alias is used instead of a hard-coded serial.
- The optional SSM association assumes `amazon-ssm-agent` is present or will be
  installed on the Ubuntu AMI. Attaching `AmazonSSMManagedInstanceCore` does
  not install it.
- GCP egress is restricted by an explicit deny-all at priority 65000 beneath
  the allow rule at 1000. The allow includes TCP 80 and UDP 123 because 4.1
  requires package mirrors and NTP during bootstrap.
- AWS and DigitalOcean egress rules are IPv4 (`0.0.0.0/0`); DigitalOcean
  additionally allows `::/0`. Registries are assumed reachable over IPv4.
- DigitalOcean Secrets Manager resources are not managed because the
  `digitalocean` provider does not expose them in the pinned range; secret
  names are references only.
- `google_project_service` enables Compute, Artifact Registry, IAM, Secret
  Manager, and (when used) Logging APIs. Disable this with
  `enable_required_apis = false` when the project already manages APIs
  elsewhere.
- GCP service-account IDs are derived from `nw-<target_id>`, truncated to 30
  characters and trimmed. Two targets whose identifiers share the first 30
  characters collide; use distinct targets.
- The GCP logging sink destination must be an existing resource; the module does
  not create a log bucket or grant the sink writer identity.
- Each module invocation owns its registry resources (ECR repositories, Artifact
  Registry repositories, DOCR). Registering a second target in the same
  account/project would collide on the shared image repository. Provision the
  registry once per project or use distinct repository names/endpoints per
  target; this module set has no partial "create registries" switch.
- The manifest project rule permits dots and underscores. Provider repository
  naming rules may be stricter for pathological combinations (for example
  consecutive separators); those names are not exercised here.
- AWS sets `user_data_replace_on_change = false`; bootstrap changes do not
  replace the instance. The rendered script is bounded by a 16 KB precondition.
