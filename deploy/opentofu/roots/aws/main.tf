terraform {
  required_version = ">= 1.11.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}

provider "aws" {
  region = var.region
}

module "target" {
  source = "../../modules/aws"

  project               = var.project
  target_name           = var.target_name
  region                = var.region
  trust_keys            = var.trust_keys
  status_keys           = var.status_keys
  status_key_secret     = var.status_key_secret
  agent_image           = var.agent_image
  agent_digest          = var.agent_digest
  agent_binary_sha256   = var.agent_binary_sha256
  agent_mode            = var.agent_mode
  service_images        = var.service_images
  bootstrap_payload_ref = var.bootstrap_payload_ref

  enable_ssm_association      = var.enable_ssm_association
  enable_cloudwatch_log_group = var.enable_cloudwatch_log_group
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
