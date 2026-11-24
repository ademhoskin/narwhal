resource "google_compute_network" "target" {
  project                 = var.gcp_project
  name                    = local.name_base
  auto_create_subnetworks = false
  routing_mode            = "REGIONAL"

  depends_on = [google_project_service.required]
}

resource "google_compute_subnetwork" "target" {
  project       = var.gcp_project
  name          = local.subnet_name
  region        = var.region
  network       = google_compute_network.target.id
  ip_cidr_range = var.subnet_cidr
}

resource "google_compute_firewall" "egress_allow" {
  project            = var.gcp_project
  name               = local.egress_allow_name
  network            = google_compute_network.target.id
  direction          = "EGRESS"
  priority           = 1000
  description        = "narwhal ${local.target_id}: bootstrap and registry egress; ingress is empty and managed by narwhal deploy"
  destination_ranges = ["0.0.0.0/0"]

  allow {
    protocol = "tcp"
    ports    = ["443"]
  }

  allow {
    protocol = "tcp"
    ports    = ["53"]
  }

  allow {
    protocol = "udp"
    ports    = ["53"]
  }

  allow {
    protocol = "tcp"
    ports    = ["80"]
  }

  allow {
    protocol = "udp"
    ports    = ["123"]
  }
}

resource "google_compute_firewall" "egress_deny" {
  project            = var.gcp_project
  name               = local.egress_deny_name
  network            = google_compute_network.target.id
  direction          = "EGRESS"
  priority           = 65000
  description        = "narwhal ${local.target_id}: deny all other egress"
  destination_ranges = ["0.0.0.0/0"]

  deny {
    protocol = "all"
  }
}

resource "google_compute_address" "target" {
  count = var.enable_static_address ? 1 : 0

  project = var.gcp_project
  name    = local.address_name
  region  = var.region

  depends_on = [google_project_service.required]
}

module "host" {
  source = "../host"

  bootstrap = local.bootstrap
}

resource "google_compute_instance" "target" {
  project      = var.gcp_project
  name         = local.name_base
  machine_type = var.machine_type
  zone         = local.zone
  labels       = local.labels

  tags = []

  metadata = {
    block-project-ssh-keys = "TRUE"
    enable-oslogin         = "FALSE"
    serial-port-enable     = "FALSE"
  }

  metadata_startup_script = module.host.user_data

  boot_disk {
    auto_delete = true

    initialize_params {
      image = "ubuntu-os-cloud/ubuntu-2404-lts-amd64"
      size  = var.root_disk_size_gb
      type  = "pd-balanced"
    }
  }

  network_interface {
    network    = google_compute_network.target.id
    subnetwork = google_compute_subnetwork.target.id

    access_config {
      nat_ip = var.enable_static_address ? google_compute_address.target[0].address : null
    }
  }

  service_account {
    email  = google_service_account.target.email
    scopes = ["cloud-platform"]
  }

  dynamic "shielded_instance_config" {
    for_each = var.enable_shielded_vm ? [1] : []

    content {
      enable_secure_boot          = true
      enable_vtpm                 = true
      enable_integrity_monitoring = true
    }
  }

  lifecycle {
    precondition {
      condition     = length(module.host.user_data) < 262144
      error_message = "The rendered startup script exceeds the 256 KB GCP startup-script limit."
    }

    precondition {
      condition     = !var.enable_logging_sink || var.log_sink_destination != ""
      error_message = "enable_logging_sink requires log_sink_destination."
    }
  }

  depends_on = [google_project_service.required]
}

resource "google_logging_project_sink" "target" {
  count = var.enable_logging_sink ? 1 : 0

  project                = var.gcp_project
  name                   = local.sink_name
  destination            = var.log_sink_destination
  filter                 = var.log_sink_filter
  unique_writer_identity = true

  depends_on = [google_project_service.required]
}
