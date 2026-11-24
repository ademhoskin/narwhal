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

variable "gcp_project" {
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

variable "status_key_secret_id" {
  description = "Secret Manager secret ID holding the status private key."
  type        = string
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

variable "enable_static_address" {
  type    = bool
  default = false
}

variable "enable_logging_sink" {
  type    = bool
  default = false
}

variable "log_sink_destination" {
  type    = string
  default = ""
}
