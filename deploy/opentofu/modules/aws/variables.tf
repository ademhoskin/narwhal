variable "project" {
  description = "narwhal project name. Used in registry paths and resource tags."
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

variable "region" {
  description = "AWS region."
  type        = string
}

variable "arch" {
  description = "Target architecture. Only amd64 is supported by the cloud design."
  type        = string
  default     = "amd64"

  validation {
    condition     = var.arch == "amd64"
    error_message = "arch must be amd64; the Ubuntu images in docs/cloud.md section 4 are amd64 only."
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

variable "status_key_secret" {
  description = "ARN of the existing SSM SecureString parameter holding the per-host Ed25519 status private key. The value is never read by OpenTofu."
  type        = string

  validation {
    condition     = can(regex("^arn:aws[a-z-]*:ssm:[a-z0-9-]+:[0-9]{12}:parameter/.+$", var.status_key_secret))
    error_message = "status_key_secret must be an SSM parameter ARN."
  }
}

variable "agent_image" {
  description = "Registry reference of the narwhald agent image, without tag or digest."
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
  description = "Service image names that receive an ECR repository, in addition to the agent, desired, and status repositories."
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
  description = "Optional image registry endpoint override. Defaults to the account ECR endpoint."
  type        = string
  default     = ""
}

variable "state_registry" {
  description = "Optional state registry endpoint override. Defaults to the account ECR endpoint; the state repositories are still created there."
  type        = string
  default     = ""
}

variable "vpc_cidr" {
  description = "CIDR for the dedicated VPC."
  type        = string
  default     = "10.20.0.0/16"

  validation {
    condition     = can(cidrhost(var.vpc_cidr, 0))
    error_message = "vpc_cidr must be a valid IPv4 CIDR."
  }
}

variable "subnet_cidr" {
  description = "CIDR for the public subnet."
  type        = string
  default     = "10.20.1.0/24"

  validation {
    condition     = can(cidrhost(var.subnet_cidr, 0))
    error_message = "subnet_cidr must be a valid IPv4 CIDR."
  }
}

variable "instance_type" {
  description = "EC2 instance type."
  type        = string
  default     = "t3.micro"
}

variable "root_volume_size_gb" {
  description = "Encrypted gp3 root volume size."
  type        = number
  default     = 20
}

variable "ami_ssm_parameter" {
  description = "Canonical Ubuntu 24.04 amd64 SSM public parameter. The serial alias is resolved at plan time; no AMI ID is hard-coded."
  type        = string
  default     = "/aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id"
}

variable "secret_parameter_arns" {
  description = "Additional SSM SecureString parameter ARNs the workload may read at runtime."
  type        = list(string)
  default     = []
}

variable "kms_key_arns" {
  description = "KMS key ARNs used by the status and workload parameters, granted kms:Decrypt to the instance role."
  type        = list(string)
  default     = []
}

variable "enable_ssm_association" {
  description = "Create the optional SSM State Manager association that runs a local reconcile on a schedule."
  type        = bool
  default     = false
}

variable "ssm_schedule_expression" {
  description = "Schedule expression for the optional SSM association."
  type        = string
  default     = "rate(5 minutes)"
}

variable "enable_cloudwatch_log_group" {
  description = "Create the optional CloudWatch log group and grant the instance role write access."
  type        = bool
  default     = false
}

variable "log_retention_days" {
  description = "Retention for the optional CloudWatch log group."
  type        = number
  default     = 30
}

variable "tags" {
  description = "Additional tags applied to every taggable resource."
  type        = map(string)
  default     = {}
}
