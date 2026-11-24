data "aws_caller_identity" "current" {}

data "aws_partition" "current" {}

data "aws_ssm_parameter" "ubuntu" {
  name = var.ami_ssm_parameter
}

resource "aws_vpc" "target" {
  cidr_block           = var.vpc_cidr
  enable_dns_support   = true
  enable_dns_hostnames = true

  tags = merge(local.common_tags, { Name = "${local.target_id}-vpc" })
}

resource "aws_subnet" "target" {
  vpc_id                  = aws_vpc.target.id
  cidr_block              = var.subnet_cidr
  map_public_ip_on_launch = false

  tags = merge(local.common_tags, { Name = "${local.target_id}-subnet" })
}

resource "aws_internet_gateway" "target" {
  vpc_id = aws_vpc.target.id

  tags = merge(local.common_tags, { Name = "${local.target_id}-igw" })
}

resource "aws_route_table" "target" {
  vpc_id = aws_vpc.target.id

  tags = merge(local.common_tags, { Name = "${local.target_id}-rt" })
}

resource "aws_route" "default" {
  route_table_id         = aws_route_table.target.id
  destination_cidr_block = "0.0.0.0/0"
  gateway_id             = aws_internet_gateway.target.id
}

resource "aws_route_table_association" "target" {
  subnet_id      = aws_subnet.target.id
  route_table_id = aws_route_table.target.id
}

resource "aws_security_group" "target" {
  name_prefix = local.name_prefix
  description = "narwhal target ${local.target_id}: egress only, empty ingress managed by narwhal deploy"
  vpc_id      = aws_vpc.target.id

  tags = merge(local.common_tags, { Name = "${local.target_id}-sg" })
}

resource "aws_vpc_security_group_egress_rule" "target" {
  for_each = local.egress_rules

  security_group_id = aws_security_group.target.id
  description       = "narwhal bootstrap and registry egress"
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = each.value.protocol
  from_port         = each.value.from_port
  to_port           = each.value.to_port
}

resource "aws_eip" "target" {
  domain = "vpc"

  tags = merge(local.common_tags, { Name = "${local.target_id}-eip" })
}

resource "aws_eip_association" "target" {
  instance_id   = aws_instance.target.id
  allocation_id = aws_eip.target.id
}

resource "aws_instance" "target" {
  ami                         = data.aws_ssm_parameter.ubuntu.value
  instance_type               = var.instance_type
  subnet_id                   = aws_subnet.target.id
  vpc_security_group_ids      = [aws_security_group.target.id]
  iam_instance_profile        = aws_iam_instance_profile.instance.name
  user_data                   = base64encode(module.host.user_data)
  user_data_replace_on_change = true

  metadata_options {
    http_endpoint               = "enabled"
    http_tokens                 = "required"
    http_put_response_hop_limit = 1
    http_protocol_ipv6          = "disabled"
    instance_metadata_tags      = "disabled"
  }

  root_block_device {
    volume_size           = var.root_volume_size_gb
    volume_type           = "gp3"
    encrypted             = true
    delete_on_termination = true
  }

  tags = merge(local.common_tags, { Name = local.target_id })

  depends_on = [
    aws_route.default,
    aws_route_table_association.target,
  ]
}

module "host" {
  source = "../host"

  bootstrap = local.bootstrap
}

resource "aws_cloudwatch_log_group" "target" {
  count = var.enable_cloudwatch_log_group ? 1 : 0

  name              = "/narwhal/${local.target_id}"
  retention_in_days = var.log_retention_days
  tags              = local.common_tags
}

resource "aws_ssm_association" "reconcile" {
  count = var.enable_ssm_association ? 1 : 0

  name                = "AWS-RunShellScript"
  schedule_expression = var.ssm_schedule_expression

  targets {
    key    = "InstanceIds"
    values = [aws_instance.target.id]
  }

  parameters = {
    commands = jsonencode([
      "runuser -u narwhal -- env XDG_RUNTIME_DIR=/run/user/$(id -u narwhal) /var/lib/narwhal/agent/current/narwhald reconcile --config /etc/narwhal/agent.toml",
    ])
  }
}
