data "aws_iam_policy_document" "instance" {
  statement {
    sid       = "EcrAuthorizationToken"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }

  statement {
    sid = "PullImagesAndDesiredState"
    actions = [
      "ecr:BatchCheckLayerAvailability",
      "ecr:BatchGetImage",
      "ecr:DescribeImages",
      "ecr:DescribeRepositories",
      "ecr:GetDownloadUrlForLayer",
      "ecr:GetRepositoryPolicy",
    ]
    resources = concat(
      [for repo in aws_ecr_repository.images : repo.arn],
      [aws_ecr_repository.desired.arn],
    )
  }

  statement {
    sid = "WriteStatus"
    actions = [
      "ecr:BatchCheckLayerAvailability",
      "ecr:CompleteLayerUpload",
      "ecr:InitiateLayerUpload",
      "ecr:PutImage",
      "ecr:UploadLayerPart",
    ]
    resources = [aws_ecr_repository.status.arn]
  }

  statement {
    sid = "ReadStatusKeyAndSecrets"
    actions = [
      "ssm:GetParameter",
      "ssm:GetParameters",
    ]
    resources = concat([var.status_key_secret], var.secret_parameter_arns)
  }

  dynamic "statement" {
    for_each = length(var.kms_key_arns) > 0 ? [1] : []

    content {
      sid       = "DecryptSecretParameters"
      actions   = ["kms:Decrypt"]
      resources = var.kms_key_arns
    }
  }

  dynamic "statement" {
    for_each = var.enable_cloudwatch_log_group ? [1] : []

    content {
      sid = "WriteCloudWatchLogs"
      actions = [
        "logs:CreateLogStream",
        "logs:DescribeLogStreams",
        "logs:PutLogEvents",
      ]
      resources = ["${aws_cloudwatch_log_group.target[0].arn}:*"]
    }
  }

  statement {
    sid       = "DenyRemoteCommand"
    effect    = "Deny"
    actions   = ["ssm:SendCommand"]
    resources = ["*"]
  }
}

resource "aws_iam_role" "instance" {
  name_prefix = local.iam_prefix

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Action    = "sts:AssumeRole"
      Principal = { Service = "ec2.amazonaws.com" }
    }]
  })

  tags = local.common_tags
}

resource "aws_iam_role_policy" "instance" {
  name   = "narwhal-target"
  role   = aws_iam_role.instance.id
  policy = data.aws_iam_policy_document.instance.json
}

resource "aws_iam_role_policy_attachment" "ssm_core" {
  count = var.enable_ssm_association ? 1 : 0

  role       = aws_iam_role.instance.name
  policy_arn = "arn:${data.aws_partition.current.partition}:iam::aws:policy/AmazonSSMManagedInstanceCore"
}

resource "aws_iam_instance_profile" "instance" {
  name_prefix = local.iam_prefix
  role        = aws_iam_role.instance.name
  tags        = local.common_tags
}
