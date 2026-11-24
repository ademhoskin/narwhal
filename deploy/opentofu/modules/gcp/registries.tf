resource "google_project_service" "required" {
  for_each = toset(local.required_services)

  project            = var.gcp_project
  service            = each.value
  disable_on_destroy = false
}

resource "google_artifact_registry_repository" "images" {
  project       = var.gcp_project
  location      = var.region
  repository_id = var.image_repository_name
  format        = "DOCKER"
  description   = "narwhal ${local.target_id} service and agent images"
  labels        = local.labels

  depends_on = [google_project_service.required]
}

resource "google_artifact_registry_repository" "desired" {
  project       = var.gcp_project
  location      = var.region
  repository_id = var.desired_repository_name
  format        = "DOCKER"
  description   = "narwhal ${local.target_id} desired-state artifacts"
  labels        = local.labels

  depends_on = [google_project_service.required]
}

resource "google_artifact_registry_repository" "status" {
  project       = var.gcp_project
  location      = var.region
  repository_id = var.status_repository_name
  format        = "DOCKER"
  description   = "narwhal ${local.target_id} status artifacts"
  labels        = local.labels

  depends_on = [google_project_service.required]
}
