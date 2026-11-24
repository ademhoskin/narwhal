output "target" {
  description = "Provider-neutral target identity."
  value = {
    provider = "gcp"
    region   = var.region
    arch     = var.arch
    selector = { stack = var.target_name }
    id       = local.target_id
    zone     = local.zone
  }
}

output "public_address" {
  description = "Static address when enabled, otherwise the ephemeral external address."
  value       = var.enable_static_address ? google_compute_address.target[0].address : google_compute_instance.target.network_interface[0].access_config[0].nat_ip
}

output "firewall" {
  description = "Managed egress firewall. There is no ingress rule; narwhal deploy creates ingress rules after bootstrap."
  value = {
    id   = google_compute_firewall.egress.id
    kind = "google_compute_firewall"
  }
}

output "image_registry" {
  description = "Artifact Registry image repository endpoint."
  value       = local.image_registry
}

output "state_registry" {
  description = "Artifact Registry base endpoint for desired and status repositories."
  value       = local.state_registry
}

output "registry_repositories" {
  description = "Registry-relative repository names for images, desired state, and status."
  value = {
    images = {
      for name in toset(concat(["narwhald"], var.service_images)) :
      name => "${local.image_registry}/${var.project}/${name}"
    }
    desired = "${local.state_registry}/${local.desired_repo}"
    status  = "${local.state_registry}/${local.status_repo}"
  }
}

output "status_key_secret" {
  description = "Reference to the Secret Manager secret holding the per-host status private key."
  value       = var.status_key_secret_id
}

output "bootstrap" {
  description = "Public bootstrap payload written to /etc/narwhal/bootstrap.json. Contains no secret values."
  value       = local.bootstrap
}
