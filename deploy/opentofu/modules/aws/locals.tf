locals {
  target_id    = "${var.project}-${var.target_name}"
  name_prefix  = "${local.target_id}-"
  iam_prefix   = substr(local.target_id, 0, 32)
  ecr_endpoint = "${data.aws_caller_identity.current.account_id}.dkr.ecr.${var.region}.amazonaws.com"

  image_registry = var.image_registry != "" ? var.image_registry : local.ecr_endpoint
  state_registry = var.state_registry != "" ? var.state_registry : local.ecr_endpoint

  image_repositories = toset(concat(["narwhald"], var.service_images))

  desired_repo_name = "${var.project}/state/${local.target_id}/desired"
  status_repo_name  = "${var.project}/state/${local.target_id}/status"

  egress_rules = {
    https   = { protocol = "tcp", from_port = 443, to_port = 443 }
    http    = { protocol = "tcp", from_port = 80, to_port = 80 }
    dns_tcp = { protocol = "tcp", from_port = 53, to_port = 53 }
    dns_udp = { protocol = "udp", from_port = 53, to_port = 53 }
    ntp     = { protocol = "udp", from_port = 123, to_port = 123 }
  }

  common_tags = merge(var.tags, {
    "narwhal-project" = var.project
    "narwhal-target"  = var.target_name
  })

  bootstrap = {
    provider                         = "aws"
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
    status_key_secret                = var.status_key_secret
    state_registry_credential_secret = ""
  }
}
