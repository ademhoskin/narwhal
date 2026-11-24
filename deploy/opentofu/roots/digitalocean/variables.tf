variable "project" {
  type = string
}

variable "target_name" {
  type = string
}

variable "region" {
  type = string
}

variable "trust_keys" {
  description = "Boot pins: map of key id to Ed25519 public key."
  type        = map(string)
}

variable "status_keys" {
  description = "Registered status public keys: map of key id to Ed25519 public key."
  type        = map(string)
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
  description = "External state registry endpoint. Must not be DOCR."
  type        = string
}

variable "state_registry_credential_secret_name" {
  type    = string
  default = ""
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
