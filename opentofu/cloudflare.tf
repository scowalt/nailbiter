provider "cloudflare" {
}


data "cloudflare_zone" "main" {
  filter = {
    account_id = var.cloudflare_account_id
    name       = var.cloudflare_zone_name
  }
}



data "cloudflare_workers_script" "www" {
  account_id  = var.cloudflare_account_id
  script_name = "nailbiter"
}


resource "cloudflare_workers_custom_domain" "www_apex" {
  account_id = var.cloudflare_account_id
  hostname   = var.cloudflare_zone_name
  service    = "nailbiter"
  zone_id    = data.cloudflare_zone.main.zone_id

  lifecycle { ignore_changes = [environment] }
}

resource "cloudflare_workers_custom_domain" "www" {
  account_id = var.cloudflare_account_id
  hostname   = "www.${var.cloudflare_zone_name}"
  service    = "nailbiter"
  zone_id    = data.cloudflare_zone.main.zone_id

  lifecycle { ignore_changes = [environment] }
}



resource "cloudflare_dns_record" "api" {
  zone_id = data.cloudflare_zone.main.zone_id
  name    = "api"
  type    = "A"
  content = hcloud_server.main.ipv4_address
  ttl     = 1   
  proxied = true
}

resource "cloudflare_dns_record" "live" {
  zone_id = data.cloudflare_zone.main.zone_id
  name    = "live"
  type    = "A"
  content = hcloud_server.main.ipv4_address
  ttl     = 1   
  proxied = true
}






resource "cloudflare_zone_setting" "ssl" {
  zone_id    = data.cloudflare_zone.main.zone_id
  setting_id = "ssl"
  value      = "strict"
}

resource "cloudflare_zone_setting" "always_use_https" {
  zone_id    = data.cloudflare_zone.main.zone_id
  setting_id = "always_use_https"
  value      = "on"
}

resource "cloudflare_zone_setting" "min_tls_version" {
  zone_id    = data.cloudflare_zone.main.zone_id
  setting_id = "min_tls_version"
  value      = "1.2"
}

resource "cloudflare_zone_setting" "tls_1_3" {
  zone_id    = data.cloudflare_zone.main.zone_id
  setting_id = "tls_1_3"
  value      = "on"
}

resource "cloudflare_zone_setting" "automatic_https_rewrites" {
  zone_id    = data.cloudflare_zone.main.zone_id
  setting_id = "automatic_https_rewrites"
  value      = "on"
}


resource "cloudflare_zone_setting" "security_level" {
  zone_id    = data.cloudflare_zone.main.zone_id
  setting_id = "security_level"
  value      = "medium"
}

resource "cloudflare_zone_setting" "brotli" {
  zone_id    = data.cloudflare_zone.main.zone_id
  setting_id = "brotli"
  value      = "on"
}

resource "cloudflare_zone_setting" "early_hints" {
  zone_id    = data.cloudflare_zone.main.zone_id
  setting_id = "early_hints"
  value      = "on"
}


resource "cloudflare_zone_setting" "http3" {
  zone_id    = data.cloudflare_zone.main.zone_id
  setting_id = "http3"
  value      = "on"
}

resource "cloudflare_zone_setting" "zero_rtt" {
  zone_id    = data.cloudflare_zone.main.zone_id
  setting_id = "0rtt"
  value      = "on"
}

resource "cloudflare_zone_setting" "opportunistic_encryption" {
  zone_id    = data.cloudflare_zone.main.zone_id
  setting_id = "opportunistic_encryption"
  value      = "on"
}


resource "cloudflare_zone_dnssec" "main" {
  zone_id = data.cloudflare_zone.main.zone_id
}


resource "cloudflare_ruleset" "redirects" {
  zone_id = data.cloudflare_zone.main.zone_id
  name    = "Redirect rules"
  kind    = "zone"
  phase   = "http_request_dynamic_redirect"

  rules = [{
    action = "redirect"
    action_parameters = {
      from_value = {
        status_code = 301
        target_url = {
          expression = "concat(\"https://${var.cloudflare_zone_name}\", http.request.uri.path)"
        }
        preserve_query_string = true
      }
    }
    expression  = "(http.host eq \"www.${var.cloudflare_zone_name}\")"
    description = "Redirect www to apex"
    enabled     = true
  }]
}


resource "cloudflare_ruleset" "security_headers" {
  zone_id = data.cloudflare_zone.main.zone_id
  name    = "Security headers"
  kind    = "zone"
  phase   = "http_response_headers_transform"

  rules = [{
    action = "rewrite"
    action_parameters = {
      headers = {
        "X-Content-Type-Options" = {
          operation = "set"
          value     = "nosniff"
        }
        "X-Frame-Options" = {
          operation = "set"
          value     = "SAMEORIGIN"
        }
        "Referrer-Policy" = {
          operation = "set"
          value     = "strict-origin-when-cross-origin"
        }
        "Permissions-Policy" = {
          operation = "set"
          value     = "geolocation=(), microphone=(), camera=()"
        }
      }
    }
    expression  = "true"
    description = "Add security headers to all responses"
    enabled     = true
  }]
}
