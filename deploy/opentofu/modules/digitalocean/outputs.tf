output "target" {
  description = "Provider-neutral target identity."
  value = {
    provider = "digitalocean"
    region   = var.region
    arch     = var.arch
    selector = { stack = var.target_name }
    id       = local.target_id
  }
}

output "public_address" {
  description = "Reserved IP when enabled, otherwise the Droplet public IPv4 address."
  value       = var.enable_reserved_ip ? digitalocean_reserved_ip.target[0].ip_address : digitalocean_droplet.target.ipv4_address
}

output "firewall" {
  description = "Managed firewall. Ingress is empty; narwhal deploy reconciles rules after bootstrap."
  value = {
    id   = digitalocean_firewall.target.id
    kind = "digitalocean_firewall"
  }
}

output "image_registry" {
  description = "DOCR endpoint."
  value       = local.image_registry
}

output "state_registry" {
  description = "External state registry endpoint."
  value       = local.state_registry
}

output "registry_repositories" {
  description = "Registry-relative repository names for images, desired state, and status. DOCR creates repositories on first push."
  value = {
    images = {
      for name in toset(concat(["narwhald"], var.service_images)) :
      name => "${local.image_registry}/${var.project}/${name}"
    }
    desired = "${local.state_registry}/${local.desired_repo_name}"
    status  = "${local.state_registry}/${local.status_repo_name}"
  }
}

output "status_key_secret" {
  description = "Reference to the DigitalOcean secret holding the per-host status private key."
  value       = var.status_key_secret_name
}

output "state_registry_credential_secret" {
  description = "Reference to the DigitalOcean secret holding the external state registry credential."
  value       = var.state_registry_credential_secret_name
}

output "bootstrap" {
  description = "Public bootstrap payload written to /etc/narwhal/bootstrap.json. The DigitalOcean bootstrap token is not included."
  value       = local.bootstrap
}
