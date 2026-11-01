output "repository_name" {
  description = "The name of the protected repository"
  value       = github_repository.repo.full_name
}

output "ruleset_id" {
  description = "The ID of the repository ruleset"
  value       = github_repository_ruleset.main.id
}


output "cloudflare_zone_id" {
  description = "The Cloudflare zone ID for the domain"
  value       = data.cloudflare_zone.main.zone_id
}

output "worker_domains" {
  description = "Custom domains configured for Workers"
  value = {
    www_apex = cloudflare_workers_custom_domain.www_apex.hostname
    www      = cloudflare_workers_custom_domain.www.hostname
  }
}

output "dns_records" {
  description = "DNS records pointing to the VPS"
  value = {
    api  = cloudflare_dns_record.api.name
    live = cloudflare_dns_record.live.name
  }
}


output "server_ipv4" {
  description = "Public IPv4 address of the new US Hetzner server"
  value       = hcloud_server.main.ipv4_address
}

output "server_status" {
  description = "Current status of the new US Hetzner server"
  value       = hcloud_server.main.status
}

