variable "project" {
  type = string
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
  description = "Boot pins: map of key id to Ed25519 public key."
  type        = map(string)
}

variable "status_keys" {
  description = "Registered status public keys: map of key id to Ed25519 public key."
  type        = map(string)
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
