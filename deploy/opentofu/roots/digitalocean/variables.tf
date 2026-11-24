variable "project" {
  type = string

  validation {
    condition     = can(regex("^[a-z0-9]([a-z0-9._-]{0,61}[a-z0-9])?$", var.project))
    error_message = "project must match the manifest rule ^[a-z0-9]([a-z0-9._-]{0,61}[a-z0-9])?$."
  }
}

variable "target_name" {
  type = string
}

variable "region" {
  type = string
}

variable "trust_keys" {
  description = "Boot pins: map of 16-hex key id to public_key and not_after."
  type = map(object({
    public_key = string
    not_after  = string
  }))
}

variable "status_keys" {
  description = "Registered status public keys: map of 16-hex key id to public_key and not_after."
  type = map(object({
    public_key = string
    not_after  = string
  }))
}

variable "status_key_secret_name" {
  description = "DigitalOcean Secrets Manager secret name holding the status private key."
  type        = string
}

variable "bootstrap_token" {
  description = "Least-privilege DigitalOcean API token delivered only in user-data. Prefer TOFU_VAR_bootstrap_token over a tfvars file."
  type        = string
  sensitive   = true
}

variable "state_registry" {
  description = "State registry endpoint, distinct from image_registry with its own credential. Same-account DOCR is forbidden; separate-account DOCR requires allow_docr_state_registry = true."
  type        = string
}

variable "state_registry_credential_secret_name" {
  description = "DigitalOcean Secrets Manager secret name holding the external state registry credential."
  type        = string
}

variable "allow_docr_state_registry" {
  description = "Allow a state registry hosted by DOCR in a separate DigitalOcean account with its own credential."
  type        = bool
  default     = false
}

variable "agent_image" {
  type = string
}

variable "agent_digest" {
  type = string
}

variable "agent_binary_sha256" {
  type = string
}

variable "agent_mode" {
  type    = string
  default = "daemon"
}

variable "service_images" {
  type    = list(string)
  default = []
}

variable "bootstrap_payload_ref" {
  type    = string
  default = ""
}

variable "enable_reserved_ip" {
  type    = bool
  default = false
}

variable "enable_monitor_alert" {
  type    = bool
  default = false
}

variable "alert_emails" {
  type    = list(string)
  default = []
}

variable "registry_name" {
  type    = string
  default = ""
}

variable "image_registry" {
  type    = string
  default = ""
}

variable "registry_tier" {
  type    = string
  default = "starter"
}

variable "droplet_size" {
  type    = string
  default = "s-1vcpu-1gb"
}

variable "vpc_ip_range" {
  type    = string
  default = "10.20.0.0/20"
}
