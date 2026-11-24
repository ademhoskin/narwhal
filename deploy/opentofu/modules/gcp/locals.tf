locals {
  selector      = { stack = var.target_name }
  selector_hash = substr(sha256(jsonencode(local.selector)), 0, 8)
  target_id     = lower(replace("gcp-${var.region}-${var.arch}-${local.selector_hash}", "/[^a-z0-9-]/", "-"))
  zone          = var.zone != "" ? var.zone : "${var.region}-a"

  name_base         = trim(substr("nw-${local.target_id}", 0, 63), "-")
  sa_id             = trim(substr("nw-${local.target_id}", 0, 30), "-")
  address_name      = trim(substr("${local.name_base}-ip", 0, 63), "-")
  sink_name         = trim(substr("${local.name_base}-logs", 0, 63), "-")
  subnet_name       = trim(substr("${local.name_base}-subnet", 0, 63), "-")
  egress_allow_name = trim(substr("${local.name_base}-egress-allow", 0, 63), "-")
  egress_deny_name  = trim(substr("${local.name_base}-egress-deny", 0, 63), "-")

  registry_base  = "${var.region}-docker.pkg.dev/${var.gcp_project}"
  image_registry = var.image_registry != "" ? var.image_registry : "${local.registry_base}/${var.image_repository_name}"
  state_registry = var.state_registry != "" ? trimsuffix(var.state_registry, "/") : local.registry_base

  desired_repo_name = "${var.project}/state/${local.target_id}/desired"
  status_repo_name  = "${var.project}/state/${local.target_id}/status"

  desired_repo = "${var.desired_repository_name}/${local.desired_repo_name}"
  status_repo  = "${var.status_repository_name}/${local.status_repo_name}"

  labels = merge(var.labels, {
    "narwhal-project" = trim(substr(replace(var.project, ".", "-"), 0, 63), "-")
    "narwhal-target"  = var.target_name
  })

  required_services = var.enable_required_apis ? concat(
    [
      "compute.googleapis.com",
      "artifactregistry.googleapis.com",
      "iam.googleapis.com",
      "secretmanager.googleapis.com",
    ],
    var.enable_logging_sink ? ["logging.googleapis.com"] : [],
  ) : []

  bootstrap = {
    provider                         = "gcp"
    project                          = var.project
    target_id                        = local.target_id
    desired_repo                     = local.desired_repo
    status_repo                      = local.status_repo
    trust_keys                       = var.trust_keys
    status_keys                      = var.status_keys
    mode                             = var.agent_mode
    image_registry                   = local.image_registry
    state_registry                   = local.state_registry
    agent_image                      = var.agent_image
    agent_digest                     = var.agent_digest
    agent_binary_sha256              = var.agent_binary_sha256
    payload_ref                      = var.bootstrap_payload_ref
    status_key_secret                = var.status_key_secret_id
    state_registry_credential_secret = ""
  }
}
