locals {
  selector      = { stack = var.target_name }
  selector_hash = substr(sha256(jsonencode(local.selector)), 0, 8)
  target_id     = lower(replace("digitalocean-${var.region}-${var.arch}-${local.selector_hash}", "/[^a-z0-9-]/", "-"))

  registry_name        = var.registry_name != "" ? var.registry_name : trim(substr(replace(replace(var.project, ".", "-"), "_", "-"), 0, 63), "-")
  image_registry_raw   = var.image_registry != "" ? var.image_registry : "registry.digitalocean.com/${local.registry_name}"
  state_registry_raw   = var.state_registry
  image_registry_parts = split("/", trimsuffix(local.image_registry_raw, "/"))
  state_registry_parts = split("/", trimsuffix(local.state_registry_raw, "/"))

  image_registry_host = replace(lower(local.image_registry_parts[0]), "/\\.+$/", "")
  state_registry_host = replace(lower(local.state_registry_parts[0]), "/\\.+$/", "")
  image_registry_path = join("/", slice(local.image_registry_parts, 1, length(local.image_registry_parts)))
  state_registry_path = join("/", slice(local.state_registry_parts, 1, length(local.state_registry_parts)))

  image_registry = local.image_registry_path == "" ? local.image_registry_host : "${local.image_registry_host}/${local.image_registry_path}"
  state_registry = local.state_registry_path == "" ? local.state_registry_host : "${local.state_registry_host}/${local.state_registry_path}"

  state_registry_is_docr = local.state_registry_host == "registry.digitalocean.com" || endswith(local.state_registry_host, ".registry.digitalocean.com")

  desired_repo_name = "${var.project}/state/${local.target_id}/desired"
  status_repo_name  = "${var.project}/state/${local.target_id}/status"

  droplet_tags = compact(concat(["narwhal", local.target_id], var.tags))

  bootstrap = {
    provider                         = "digitalocean"
    project                          = var.project
    target_id                        = local.target_id
    desired_repo                     = local.desired_repo_name
    status_repo                      = local.status_repo_name
    trust_keys                       = var.trust_keys
    status_keys                      = var.status_keys
    mode                             = var.agent_mode
    image_registry                   = local.image_registry
    state_registry                   = local.state_registry
    agent_image                      = var.agent_image
    agent_digest                     = var.agent_digest
    agent_binary_sha256              = var.agent_binary_sha256
    payload_ref                      = var.bootstrap_payload_ref
    status_key_secret                = var.status_key_secret_name
    state_registry_credential_secret = var.state_registry_credential_secret_name
  }
}
