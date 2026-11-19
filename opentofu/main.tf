terraform {
  required_version = ">= 1.6"

  backend "s3" {
    bucket = "opentofu-state"
    key    = "nailbiter/terraform.tfstate"
    region = "auto"
    endpoints = {
      s3 = "https://a2dd27ff56de48a73e384881d22e60e2.r2.cloudflarestorage.com"
    }
    skip_credentials_validation = true
    skip_requesting_account_id  = true
    skip_metadata_api_check     = true
    skip_region_validation      = true
    use_path_style              = true
    use_lockfile                = true
  }

  required_providers {
    github = {
      source  = "integrations/github"
      version = "~> 6.0"
    }
    cloudflare = {
      source  = "cloudflare/cloudflare"
      version = "~> 5.0"
    }
    hcloud = {
      source  = "hetznercloud/hcloud"
      version = "~> 1.49"
    }
    tailscale = {
      source  = "tailscale/tailscale"
      version = "~> 0.29"
    }
  }
}


provider "github" {
  owner = var.github_owner
}

data "github_repository" "repo" {
  full_name = "${var.github_owner}/${var.repository_name}"
}

resource "github_repository" "repo" {
  name         = var.repository_name
  description  = data.github_repository.repo.description
  visibility   = data.github_repository.repo.visibility
  has_issues   = data.github_repository.repo.has_issues
  has_projects = data.github_repository.repo.has_projects
  has_wiki     = data.github_repository.repo.has_wiki
  has_downloads = data.github_repository.repo.has_downloads
  has_discussions = data.github_repository.repo.has_discussions
  is_template   = data.github_repository.repo.is_template
  homepage_url  = data.github_repository.repo.homepage_url

  allow_merge_commit = false
  allow_rebase_merge = false
  allow_squash_merge = true

  allow_auto_merge = false

  merge_commit_message = data.github_repository.repo.merge_commit_message
  merge_commit_title    = data.github_repository.repo.merge_commit_title
  squash_merge_commit_message = data.github_repository.repo.squash_merge_commit_message
  squash_merge_commit_title   = data.github_repository.repo.squash_merge_commit_title

  lifecycle {
    ignore_changes = [vulnerability_alerts]
  }
}

resource "github_repository_ruleset" "main" {
  name        = "main-branch-protection"
  repository  = github_repository.repo.name
  target      = "branch"
  enforcement = "active"

  conditions {
    ref_name {
      include = ["~DEFAULT_BRANCH"]
      exclude = []
    }
  }

  rules {
    deletion = true

    non_fast_forward = true

    required_linear_history = true

    required_status_checks {
      strict_required_status_checks_policy = false

      dynamic "required_check" {
        for_each = var.required_status_checks
        content {
          context        = required_check.value
          integration_id = 15368
        }
      }
    }
  }
}

