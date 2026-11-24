terraform {
  required_version = ">= 1.11.0"

  required_providers {
    digitalocean = {
      source  = "digitalocean/digitalocean"
      version = "~> 2.0"
    }
  }
}

# The provider reads DIGITALOCEAN_TOKEN from the environment.
provider "digitalocean" {}

module "target" {
  source = "../../modules/digitalocean"

  project                               = var.project
  target_name                           = var.target_name
  region                                = var.region
  trust_keys                            = var.trust_keys
  status_keys                           = var.status_keys
  status_key_secret_name                = var.status_key_secret_name
  bootstrap_token                       = var.bootstrap_token
  agent_image                           = var.agent_image
  agent_digest                          = var.agent_digest
  agent_binary_sha256                   = var.agent_binary_sha256
  agent_mode                            = var.agent_mode
  service_images                        = var.service_images
  bootstrap_payload_ref                 = var.bootstrap_payload_ref
  state_registry                        = var.state_registry
  allow_docr_state_registry             = var.allow_docr_state_registry
  state_registry_credential_secret_name = var.state_registry_credential_secret_name
  registry_name                         = var.registry_name
  image_registry                        = var.image_registry
  registry_tier                         = var.registry_tier
  droplet_size                          = var.droplet_size
  vpc_ip_range                          = var.vpc_ip_range

  enable_reserved_ip   = var.enable_reserved_ip
  enable_monitor_alert = var.enable_monitor_alert
  alert_emails         = var.alert_emails
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

output "state_registry_credential_secret" {
  value = module.target.state_registry_credential_secret
}

output "bootstrap" {
  value = module.target.bootstrap
}
