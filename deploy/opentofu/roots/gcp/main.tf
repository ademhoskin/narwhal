terraform {
  required_version = ">= 1.11.0"

  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 8.0"
    }
  }
}

provider "google" {
  project = var.gcp_project
  region  = var.region
}

module "target" {
  source = "../../modules/gcp"

  project               = var.project
  target_name           = var.target_name
  gcp_project           = var.gcp_project
  region                = var.region
  trust_keys            = var.trust_keys
  status_keys           = var.status_keys
  status_key_secret_id  = var.status_key_secret_id
  agent_image           = var.agent_image
  agent_digest          = var.agent_digest
  agent_binary_sha256   = var.agent_binary_sha256
  agent_mode            = var.agent_mode
  service_images        = var.service_images
  bootstrap_payload_ref = var.bootstrap_payload_ref

  enable_static_address = var.enable_static_address
  enable_logging_sink   = var.enable_logging_sink
  log_sink_destination  = var.log_sink_destination
}

output "target" {
  value = module.target.target
}

output "public_address" {
  value = module.target.public_address
}

output "firewall" {
  value = module.target.firewall
}

output "image_registry" {
  value = module.target.image_registry
}

output "state_registry" {
  value = module.target.state_registry
}

output "registry_repositories" {
  value = module.target.registry_repositories
}

output "status_key_secret" {
  value = module.target.status_key_secret
}

output "bootstrap" {
  value = module.target.bootstrap
}
