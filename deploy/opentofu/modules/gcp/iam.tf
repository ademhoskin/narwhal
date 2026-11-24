resource "google_service_account" "target" {
  project      = var.gcp_project
  account_id   = local.sa_id
  display_name = "narwhal ${local.target_id}"

  depends_on = [google_project_service.required]
}

resource "google_artifact_registry_repository_iam_member" "image_reader" {
  project    = var.gcp_project
  location   = google_artifact_registry_repository.images.location
  repository = google_artifact_registry_repository.images.repository_id
  role       = "roles/artifactregistry.reader"
  member     = "serviceAccount:${google_service_account.target.email}"
}

resource "google_artifact_registry_repository_iam_member" "desired_reader" {
  project    = var.gcp_project
  location   = google_artifact_registry_repository.desired.location
  repository = google_artifact_registry_repository.desired.repository_id
  role       = "roles/artifactregistry.reader"
  member     = "serviceAccount:${google_service_account.target.email}"
}

resource "google_artifact_registry_repository_iam_member" "status_writer" {
  project    = var.gcp_project
  location   = google_artifact_registry_repository.status.location
  repository = google_artifact_registry_repository.status.repository_id
  role       = "roles/artifactregistry.writer"
  member     = "serviceAccount:${google_service_account.target.email}"
}

resource "google_secret_manager_secret_iam_member" "status_key" {
  project   = var.gcp_project
  secret_id = var.status_key_secret_id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.target.email}"

  depends_on = [google_project_service.required]
}

resource "google_secret_manager_secret_iam_member" "workload_secrets" {
  for_each = toset(var.secret_ids)

  project   = var.gcp_project
  secret_id = each.value
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.target.email}"

  depends_on = [google_project_service.required]
}

resource "google_project_iam_member" "logging_writer" {
  count = var.enable_logging_sink ? 1 : 0

  project = var.gcp_project
  role    = "roles/logging.logWriter"
  member  = "serviceAccount:${google_service_account.target.email}"

  depends_on = [google_project_service.required]
}
