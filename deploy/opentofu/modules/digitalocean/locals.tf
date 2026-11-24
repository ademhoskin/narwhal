locals {
  target_id = "${var.project}-${var.target_name}"

  registry_name  = var.registry_name != "" ? var.registry_name : var.project
  image_registry = var.image_registry != "" ? var.image_registry : "registry.digitalocean.com/${local.registry_name}"
  state_registry = trimsuffix(var.state_registry, "/")

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
