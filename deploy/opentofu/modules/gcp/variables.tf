variable "project" {
  description = "narwhal project name. Used in registry paths and labels."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9]([a-z0-9._-]{0,61}[a-z0-9])?$", var.project))
    error_message = "project must match the manifest rule ^[a-z0-9]([a-z0-9._-]{0,61}[a-z0-9])?$."
  }
}

variable "target_name" {
  description = "Target name. Becomes target.selector.stack in the module contract."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9]([a-z0-9-]{0,28}[a-z0-9])?$", var.target_name))
    error_message = "target_name must be 1-30 lowercase alphanumeric characters or hyphens and must start and end alphanumeric."
  }
}

variable "gcp_project" {
  description = "Google Cloud project ID that owns the target resources."
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{4,28}[a-z0-9]$", var.gcp_project))
    error_message = "gcp_project must be a valid Google Cloud project ID."
  }
}

variable "region" {
  description = "Google Cloud region."
  type        = string

  validation {
    condition     = can(regex("^[a-z]+(-[a-z]+)+[0-9]+$", var.region))
    error_message = "region must be a Google Cloud region slug such as us-central1."
  }
}

variable "zone" {
  description = "Google Cloud zone. Defaults to the first zone in region."
  type        = string
  default     = ""
}

variable "arch" {
  description = "Target architecture. Only amd64 is supported by the cloud design."
  type        = string
  default     = "amd64"

  validation {
    condition     = var.arch == "amd64"
    error_message = "arch must be amd64; the ubuntu-2404-lts-amd64 image family is amd64 only."
  }
}

variable "trust_keys" {
  description = "Boot pins for desired-state signing: map of 16-hex key id to the 32-byte Ed25519 public key and its not_after timestamp."
  type = map(object({
    public_key = string
    not_after  = string
  }))

  validation {
    condition = length(var.trust_keys) > 0 && alltrue([
      for id, key in var.trust_keys :
      can(regex("^[0-9a-f]{16}$", id)) &&
      can(regex("^[A-Za-z0-9+/]{43}=$", key.public_key)) &&
      can(regex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", key.not_after))
    ])
    error_message = "trust_keys must be non-empty with 16-hex key ids, base64 32-byte public keys, and RFC 3339 UTC not_after values."
  }
}

variable "status_keys" {
  description = "Registered per-host status signing public keys: map of 16-hex key id to the 32-byte Ed25519 public key and its not_after timestamp."
  type = map(object({
    public_key = string
    not_after  = string
  }))

  validation {
    condition = length(var.status_keys) > 0 && alltrue([
      for id, key in var.status_keys :
      can(regex("^[0-9a-f]{16}$", id)) &&
      can(regex("^[A-Za-z0-9+/]{43}=$", key.public_key)) &&
      can(regex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", key.not_after))
    ])
    error_message = "status_keys must be non-empty with 16-hex key ids, base64 32-byte public keys, and RFC 3339 UTC not_after values."
  }
}

variable "status_key_secret_id" {
  description = "Secret Manager secret ID holding the per-host Ed25519 status private key. The value is never read by OpenTofu."
  type        = string

  validation {
    condition     = can(regex("^[A-Za-z0-9_-]{1,255}$", var.status_key_secret_id))
    error_message = "status_key_secret_id must be a Secret Manager secret ID."
  }
}

variable "secret_ids" {
  description = "Additional Secret Manager secret IDs the target service account may read at runtime."
  type        = list(string)
  default     = []
}

variable "agent_image" {
  description = "Registry reference of the narwhal agent image, without tag or digest."
  type        = string

  validation {
    condition     = can(regex("^[a-zA-Z0-9][a-zA-Z0-9._:/-]*$", var.agent_image))
    error_message = "agent_image must be a registry image reference without scheme or digest."
  }
}

variable "agent_digest" {
  description = "Agent image digest, sha256:<64 hex>."
  type        = string

  validation {
    condition     = can(regex("^sha256:[0-9a-f]{64}$", var.agent_digest))
    error_message = "agent_digest must be sha256 followed by 64 lowercase hex characters."
  }
}

variable "agent_binary_sha256" {
  description = "SHA-256 of the narwhal binary extracted from the agent image, 64 lowercase hex characters."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-f]{64}$", var.agent_binary_sha256))
    error_message = "agent_binary_sha256 must be 64 lowercase hex characters."
  }
}

variable "agent_mode" {
  description = "Agent runtime mode: daemon (narwhal.service) or timer (narwhal-reconcile.timer)."
  type        = string
  default     = "daemon"

  validation {
    condition     = contains(["daemon", "timer"], var.agent_mode)
    error_message = "agent_mode must be daemon or timer."
  }
}

variable "bootstrap_payload_ref" {
  description = "Optional opaque reference to an externally stored bootstrap payload. Written to bootstrap.json as payload_ref."
  type        = string
  default     = ""
}

variable "service_images" {
  description = "Service image names to expose in the image repository."
  type        = list(string)
  default     = []

  validation {
    condition = alltrue([
      for name in var.service_images : can(regex("^[a-z0-9][a-z0-9._-]{0,127}$", name))
    ])
    error_message = "service_images entries must be lowercase repository names without slashes."
  }
}

variable "image_repository_name" {
  description = "Artifact Registry repository ID for images."
  type        = string
  default     = "narwhal-images"

  validation {
    condition     = can(regex("^[a-z]([a-z0-9-]{0,61}[a-z0-9])$", var.image_repository_name))
    error_message = "image_repository_name must be a valid Artifact Registry repository ID."
  }
}

variable "desired_repository_name" {
  description = "Artifact Registry repository ID for desired state."
  type        = string
  default     = "narwhal-desired"

  validation {
    condition     = can(regex("^[a-z]([a-z0-9-]{0,61}[a-z0-9])$", var.desired_repository_name))
    error_message = "desired_repository_name must be a valid Artifact Registry repository ID."
  }
}

variable "status_repository_name" {
  description = "Artifact Registry repository ID for status."
  type        = string
  default     = "narwhal-status"

  validation {
    condition     = can(regex("^[a-z]([a-z0-9-]{0,61}[a-z0-9])$", var.status_repository_name))
    error_message = "status_repository_name must be a valid Artifact Registry repository ID."
  }
}

variable "image_registry" {
  description = "Optional image registry endpoint override. Defaults to the Artifact Registry image repository."
  type        = string
  default     = ""
}

variable "state_registry" {
  description = "Optional state registry base override. Defaults to the Artifact Registry location/project base."
  type        = string
  default     = ""
}

variable "network_cidr" {
  description = "CIDR for the dedicated VPC network."
  type        = string
  default     = "10.20.0.0/16"

  validation {
    condition     = can(cidrhost(var.network_cidr, 0))
    error_message = "network_cidr must be a valid IPv4 CIDR."
  }
}

variable "subnet_cidr" {
  description = "CIDR for the target subnet."
  type        = string
  default     = "10.20.1.0/24"

  validation {
    condition     = can(cidrhost(var.subnet_cidr, 0))
    error_message = "subnet_cidr must be a valid IPv4 CIDR."
  }
}

variable "machine_type" {
  description = "Compute Engine machine type."
  type        = string
  default     = "e2-small"
}

variable "root_disk_size_gb" {
  description = "Boot disk size in GiB."
  type        = number
  default     = 20
}

variable "enable_shielded_vm" {
  description = "Enable Secure Boot, vTPM, and integrity monitoring."
  type        = bool
  default     = true
}

variable "enable_static_address" {
  description = "Reserve and attach an external static IP."
  type        = bool
  default     = false
}

variable "enable_logging_sink" {
  description = "Create an optional Cloud Logging sink. Requires log_sink_destination."
  type        = bool
  default     = false
}

variable "log_sink_destination" {
  description = "Destination for the optional logging sink, for example a Cloud Logging bucket, Pub/Sub topic, or BigQuery dataset."
  type        = string
  default     = ""
}

variable "log_sink_filter" {
  description = "Log filter for the optional logging sink."
  type        = string
  default     = "resource.type=\"gce_instance\""
}

variable "enable_required_apis" {
  description = "Enable the Compute, Artifact Registry, IAM, Secret Manager, and (when used) Logging APIs."
  type        = bool
  default     = true
}

variable "labels" {
  description = "Additional labels applied to supported resources."
  type        = map(string)
  default     = {}
}
