
resource "digitalocean_vpc" "target" {
  name     = local.target_id
  region   = var.region
  ip_range = var.vpc_ip_range
}

resource "digitalocean_container_registry" "target" {
  name                   = local.registry_name
  subscription_tier_slug = var.registry_tier
  region                 = var.region

  lifecycle {
    precondition {
      condition     = length(local.registry_name) >= 3 && length(local.registry_name) <= 63
      error_message = "The resolved DOCR registry name must be 3-63 characters; set registry_name when the project name is shorter."
    }
  }
}

module "host" {
  source = "../host"

  bootstrap = local.bootstrap

  extra_files = [
    {
      path    = "/etc/narwhal/do-token"
      content = var.bootstrap_token
      mode    = "0600"
      owner   = "narwhal"
      group   = "narwhal"
    },
  ]
}

resource "digitalocean_droplet" "target" {
  name       = local.target_id
  region     = var.region
  size       = var.droplet_size
  image      = "ubuntu-24-04-x64"
  vpc_uuid   = digitalocean_vpc.target.id
  user_data  = module.host.user_data
  ssh_keys   = []
  ipv6       = false
  monitoring = true
  backups    = false
  tags       = local.droplet_tags

  lifecycle {
    precondition {
      condition     = !var.enable_monitor_alert || length(var.alert_emails) > 0
      error_message = "enable_monitor_alert requires at least one alert_emails entry."
    }

    precondition {
      condition     = length(module.host.user_data) < 65536
      error_message = "The rendered user-data exceeds the 64 KB DigitalOcean limit."
    }

    precondition {
      condition     = local.state_registry != local.image_registry && (var.allow_docr_state_registry || !local.state_registry_is_docr)
      error_message = "state_registry must differ from image_registry; a DOCR state registry requires allow_docr_state_registry = true and a separate-account credential."
    }
  }
}

resource "digitalocean_firewall" "target" {
  name        = local.target_id
  droplet_ids = [digitalocean_droplet.target.id]

  outbound_rule {
    protocol              = "tcp"
    port_range            = "443"
    destination_addresses = ["0.0.0.0/0", "::/0"]
  }

  outbound_rule {
    protocol              = "tcp"
    port_range            = "80"
    destination_addresses = ["0.0.0.0/0", "::/0"]
  }

  outbound_rule {
    protocol              = "tcp"
    port_range            = "53"
    destination_addresses = ["0.0.0.0/0", "::/0"]
  }

  outbound_rule {
    protocol              = "udp"
    port_range            = "53"
    destination_addresses = ["0.0.0.0/0", "::/0"]
  }

  outbound_rule {
    protocol              = "udp"
    port_range            = "123"
    destination_addresses = ["0.0.0.0/0", "::/0"]
  }
}

resource "digitalocean_reserved_ip" "target" {
  count = var.enable_reserved_ip ? 1 : 0

  region = var.region
}

resource "digitalocean_reserved_ip_assignment" "target" {
  count = var.enable_reserved_ip ? 1 : 0

  ip_address = digitalocean_reserved_ip.target[0].ip_address
  droplet_id = digitalocean_droplet.target.id
}

resource "digitalocean_monitor_alert" "cpu" {
  count = var.enable_monitor_alert ? 1 : 0

  alerts {
    email = var.alert_emails
  }

  compare     = "GreaterThan"
  description = "narwhal ${local.target_id} CPU above ${var.monitor_alert_threshold}%"
  enabled     = true
  entities    = [digitalocean_droplet.target.id]
  type        = "v1/insights/droplet/cpu"
  value       = var.monitor_alert_threshold
  window      = "5m"
}
