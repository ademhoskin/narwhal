variable "bootstrap" {
  description = "Public bootstrap payload written to /etc/narwhal/bootstrap.json."
  type = object({
    provider            = string
    project             = string
    target_id           = string
    desired_repo        = string
    status_repo         = string
    mode                = string
    image_registry      = string
    state_registry      = string
    agent_image         = string
    agent_digest        = string
    agent_binary_sha256 = string
    trust_keys = map(object({
      public_key = string
      not_after  = string
    }))
    status_keys = map(object({
      public_key = string
      not_after  = string
    }))
    payload_ref                      = optional(string, "")
    status_key_secret                = optional(string, "")
    state_registry_credential_secret = optional(string, "")
  })
}

variable "extra_files" {
  description = "Additional root-written files, such as the DigitalOcean bootstrap token. Contents are placed verbatim in user-data and therefore in state."
  type = list(object({
    path    = string
    content = string
    mode    = optional(string, "0600")
    owner   = optional(string, "narwhal")
    group   = optional(string, "narwhal")
  }))
  default   = []
  sensitive = true

  validation {
    condition = alltrue([
      for f in var.extra_files :
      can(regex("^/etc/narwhal/([A-Za-z0-9_][A-Za-z0-9._-]*/)*[A-Za-z0-9_][A-Za-z0-9._-]*$", f.path))
    ])
    error_message = "extra_files paths must be absolute paths under /etc/narwhal, with no traversal or empty segments."
  }

  validation {
    condition = alltrue([
      for f in var.extra_files :
      can(regex("^0[0-7]{3}$", f.mode)) &&
      can(regex("^[a-z_][a-z0-9_-]{0,31}$", f.owner)) &&
      can(regex("^[a-z_][a-z0-9_-]{0,31}$", f.group))
    ])
    error_message = "extra_files modes must be four-digit octal strings and owner/group must be valid account names."
  }
}

variable "packages" {
  description = "Distribution packages installed on first boot."
  type        = list(string)
  default = [
    "podman",
    "uidmap",
    "fuse-overlayfs",
    "slirp4netns",
    "ca-certificates",
    "curl",
    "jq",
  ]
}
