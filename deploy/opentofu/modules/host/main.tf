terraform {
  required_version = ">= 1.11.0"
}

locals {
  bootstrap_json = jsonencode(var.bootstrap)
  trust_keys     = var.bootstrap.trust_keys
  extra_files    = var.extra_files
}

output "user_data" {
  description = "Shell script that prepares the host and writes the public bootstrap material."
  value = templatefile("${path.module}/templates/host-prepare.sh.tftpl", {
    bootstrap_json = local.bootstrap_json
    trust_keys     = local.trust_keys
    extra_files    = local.extra_files
    packages       = join(" ", var.packages)
  })
}
