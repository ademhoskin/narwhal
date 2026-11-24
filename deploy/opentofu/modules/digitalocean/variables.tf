variable "project" {
  description = "narwhal project name. Used in registry paths and resource tags."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9]([a-z0-9-]{0,28}[a-z0-9])?$", var.project))
    error_message = "project must be 1-30 lowercase alphanumeric characters or hyphens and must start and end alphanumeric."
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

variable "region" {
  description = "DigitalOcean region slug."
  type        = string
}

variable "arch" {
  description = "Target architecture. Only amd64 is supported by the cloud design."
  type        = string
  default     = "amd64"

  validation {
    condition     = var.arch == "amd64"
    error_message = "arch must be amd64; the ubuntu-24-04-x64 image is amd64 only."
  }
}

variable "trust_keys" {
  description = "Boot pins for desired-state signing: map of key id to Ed25519 public key."
  type        = map(string)

  validation {
    condition = length(var.trust_keys) > 0 && alltrue([
      for id, key in var.trust_keys :
      can(regex("^[A-Za-z0-9._-]{1,64}$", id)) && length(trimspace(key)) > 0
    ])
    error_message = "trust_keys must be non-empty; key ids may contain only alphanumerics, dot, underscore, and hyphen."
  }
}

variable "status_keys" {
  description = "Registered per-host status signing public keys: map of key id to Ed25519 public key."
  type        = map(string)

  validation {
    condition = length(var.status_keys) > 0 && alltrue([
      for id, key in var.status_keys :
      can(regex("^[A-Za-z0-9._-]{1,64}$", id)) && length(trimspace(key)) > 0
    ])
    error_message = "status_keys must be non-empty; key ids may contain only alphanumerics, dot, underscore, and hyphen."
  }
}

variable "status_key_secret_name" {
  description = "DigitalOcean Secrets Manager secret name holding the per-host Ed25519 status private key. The value is never read by OpenTofu."
  type        = string

  validation {
    condition     = can(regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", var.status_key_secret_name))
    error_message = "status_key_secret_name must be a DigitalOcean secret name."
  }
}

variable "bootstrap_token" {
  description = "Least-privilege DigitalOcean API token delivered only in user-data. It must not have firewall or registry write permission. This is the single documented secret permitted in a bootstrap payload."
  type        = string
  sensitive   = true

  validation {
    condition     = can(regex("^dop_v1_[A-Za-z0-9]{32,}$", var.bootstrap_token))
    error_message = "bootstrap_token must be a DigitalOcean personal access token beginning with dop_v1_."
  }
}

variable "agent_image" {
  description = "Registry reference of the narwhald agent image, without tag or digest."
  type        = string

  validation {
    condition     = can(regex("^[a-zA-Z0-9][a-zA-Z0-9._:/-]*$", var.agent_image)) && !can(regex("://", var.agent_image))
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
  description = "SHA-256 of the narwhald binary extracted from the agent image, 64 lowercase hex characters."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-f]{64}$", var.agent_binary_sha256))
    error_message = "agent_binary_sha256 must be 64 lowercase hex characters."
  }
}

variable "agent_mode" {
  description = "Agent runtime mode: daemon (narwhald.service) or timer (narwhald-reconcile.timer)."
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
  description = "Service image names exposed as DOCR repositories on first push."
  type        = list(string)
  default     = []

  validation {
    condition = alltrue([
      for name in var.service_images : can(regex("^[a-z0-9][a-z0-9._-]{0,127}$", name))
    ])
    error_message = "service_images entries must be lowercase repository names without slashes."
  }
}

variable "image_registry" {
  description = "Optional image registry endpoint override. Defaults to the DOCR endpoint."
  type        = string
  default     = ""
}

variable "state_registry" {
  description = "External state registry endpoint. DOCR must not be used for state; supply a non-DOCR registry (or a separate DigitalOcean account registry)."
  type        = string

  validation {
    condition     = can(regex("^[A-Za-z0-9][A-Za-z0-9._:/-]*$", var.state_registry)) && !can(regex("://", var.state_registry))
    error_message = "state_registry must be a registry host or host/path without a URL scheme or trailing slash."
  }
}

variable "state_registry_credential_secret_name" {
  description = "DigitalOcean Secrets Manager secret name holding the external state registry credential. Read at runtime with bootstrap_token."
  type        = string
  default     = ""
}

variable "registry_name" {
  description = "DOCR registry name. Defaults to the project name."
  type        = string
  default     = ""

  validation {
    condition     = var.registry_name == "" || can(regex("^[a-z0-9]([a-z0-9-]{1,61}[a-z0-9])$", var.registry_name))
    error_message = "registry_name must be 3-63 lowercase alphanumeric characters or hyphens, starting and ending alphanumeric."
  }
}

variable "registry_tier" {
  description = "DOCR subscription tier slug."
  type        = string
  default     = "starter"
}

variable "droplet_size" {
  description = "Droplet size slug."
  type        = string
  default     = "s-1vcpu-1gb"
}

variable "vpc_ip_range" {
  description = "CIDR for the dedicated VPC."
  type        = string
  default     = "10.20.0.0/20"

  validation {
    condition     = can(cidrhost(var.vpc_ip_range, 0))
    error_message = "vpc_ip_range must be a valid IPv4 CIDR."
  }
}

variable "enable_reserved_ip" {
  description = "Allocate and assign a reserved IP."
  type        = bool
  default     = false
}

variable "enable_monitor_alert" {
  description = "Create an optional CPU monitor alert."
  type        = bool
  default     = false
}

variable "alert_emails" {
  description = "Email addresses for the optional monitor alert."
  type        = list(string)
  default     = []
}

variable "monitor_alert_threshold" {
  description = "CPU percentage threshold for the optional monitor alert."
  type        = number
  default     = 80
}

variable "tags" {
  description = "Additional tags applied to the Droplet."
  type        = list(string)
  default     = []
}
