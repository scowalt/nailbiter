variable "github_owner" {
  description = "The GitHub organization or user name"
  type        = string
  default     = ""
}

variable "repository_name" {
  description = "The name of the GitHub repository"
  type        = string
  default     = "nailbiter"
}

variable "protected_branch_name" {
  description = "The name of the branch to protect"
  type        = string
  default     = "main"
}

variable "required_status_checks" {
  description = "List of status checks that must pass before merging"
  type        = list(string)
  default     = []
}

variable "enforce_admins" {
  description = "DEPRECATED: No longer used with repository rulesets. Rulesets use bypass_actors instead."
  type        = bool
  default     = true
}


variable "cloudflare_api_token" {
  description = "Cloudflare API token. Pass via CLOUDFLARE_API_TOKEN env var (injected by Doppler)."
  type        = string
  sensitive   = true
  default     = ""
}

variable "cloudflare_account_id" {
  description = "Cloudflare account ID. Find this in the Cloudflare dashboard URL or account settings."
  type        = string
}

variable "cloudflare_zone_name" {
  description = "The domain name managed by Cloudflare (e.g., nailbiter.app)"
  type        = string
  default     = "nailbiter.app"
}


variable "hcloud_server_type" {
  description = "Hetzner Cloud server type (e.g., cpx11, cpx21)"
  type        = string
  default     = "cpx11"
}

variable "hcloud_location" {
  description = "Hetzner Cloud datacenter location (e.g., hil, nbg1, fsn1)"
  type        = string
  default     = "hil"
}

variable "hcloud_image" {
  description = "Hetzner Cloud server OS image"
  type        = string
  default     = "ubuntu-24.04"
}

