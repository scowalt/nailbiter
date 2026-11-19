import {
  to = cloudflare_workers_custom_domain.www_apex
  id = "${var.cloudflare_account_id}/a8d8e5ed81b8798929d7901f5fb1a2dd43a16f2a"
}

import {
  to = cloudflare_workers_custom_domain.www
  id = "${var.cloudflare_account_id}/88ba8d2c6c098b0df7ca6211c0acee037e368c57"
}

import {
  to = cloudflare_dns_record.api
  id = "80c3e02b79dab6725446bfda98900b22/e801982c6369f45a380a9addc74229ed"
}

import {
  to = cloudflare_dns_record.live
  id = "80c3e02b79dab6725446bfda98900b22/2aa039a3fe08d84b65dd8fdb9705b966"
}

import {
  to = cloudflare_zone_setting.ssl
  id = "80c3e02b79dab6725446bfda98900b22/ssl"
}

import {
  to = cloudflare_zone_setting.always_use_https
  id = "80c3e02b79dab6725446bfda98900b22/always_use_https"
}

import {
  to = cloudflare_zone_setting.min_tls_version
  id = "80c3e02b79dab6725446bfda98900b22/min_tls_version"
}

import {
  to = cloudflare_zone_setting.tls_1_3
  id = "80c3e02b79dab6725446bfda98900b22/tls_1_3"
}

import {
  to = cloudflare_zone_setting.automatic_https_rewrites
  id = "80c3e02b79dab6725446bfda98900b22/automatic_https_rewrites"
}

import {
  to = cloudflare_zone_setting.security_level
  id = "80c3e02b79dab6725446bfda98900b22/security_level"
}

import {
  to = cloudflare_zone_setting.brotli
  id = "80c3e02b79dab6725446bfda98900b22/brotli"
}

import {
  to = cloudflare_zone_setting.early_hints
  id = "80c3e02b79dab6725446bfda98900b22/early_hints"
}

import {
  to = cloudflare_zone_setting.http3
  id = "80c3e02b79dab6725446bfda98900b22/http3"
}

import {
  to = cloudflare_zone_setting.zero_rtt
  id = "80c3e02b79dab6725446bfda98900b22/0rtt"
}

import {
  to = cloudflare_zone_setting.opportunistic_encryption
  id = "80c3e02b79dab6725446bfda98900b22/opportunistic_encryption"
}

import {
  to = cloudflare_zone_dnssec.main
  id = "80c3e02b79dab6725446bfda98900b22"
}

import {
  to = cloudflare_ruleset.redirects
  id = "zones/80c3e02b79dab6725446bfda98900b22/f4a0bde9692643518408db4b659eb649"
}

import {
  to = cloudflare_ruleset.security_headers
  id = "zones/80c3e02b79dab6725446bfda98900b22/2a5da4950db54f9286745c8af389c9c1"
}

import {
  to = hcloud_firewall.main
  id = "10736060"
}

import {
  to = hcloud_server.main
  id = "124485435"
}

import {
  to = github_repository.repo
  id = var.repository_name
}

import {
  to = github_repository_ruleset.main
  id = "${var.repository_name}:26326277"
}
