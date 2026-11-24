output "target" {
  description = "Provider-neutral target identity."
  value = {
    provider = "aws"
    region   = var.region
    arch     = var.arch
    selector = local.selector
    id       = local.target_id
  }
}

output "public_address" {
  description = "Elastic IP attached to the target instance."
  value       = aws_eip.target.public_ip
}

output "firewall" {
  description = "Managed firewall. Ingress is empty; narwhal deploy reconciles rules after bootstrap."
  value = {
    id   = aws_security_group.target.id
    kind = "aws_security_group"
  }
}

output "image_registry" {
  description = "Image registry endpoint."
  value       = local.image_registry
}

output "state_registry" {
  description = "State registry endpoint."
  value       = local.state_registry
}

output "registry_repositories" {
  description = "Registry-relative repository names for images, desired state, and status."
  value = {
    images = {
      for name, repo in aws_ecr_repository.images : name => "${local.image_registry}/${repo.name}"
    }
    desired = "${local.state_registry}/${aws_ecr_repository.desired.name}"
    status  = "${local.state_registry}/${aws_ecr_repository.status.name}"
  }
}

output "status_key_secret" {
  description = "Reference to the SSM SecureString parameter holding the per-host status private key."
  value       = var.status_key_secret
}

output "bootstrap" {
  description = "Public bootstrap payload written to /etc/narwhal/bootstrap.json. Contains no secret values."
  value       = local.bootstrap
}
