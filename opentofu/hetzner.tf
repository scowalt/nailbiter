
provider "hcloud" {}


provider "tailscale" {
  scopes = ["auth_keys"]
}

resource "tailscale_tailnet_key" "server" {
  reusable      = false
  ephemeral     = false
  preauthorized = true
  expiry        = 3600  
  tags          = ["tag:server"]
  description   = "nailbiter-app server provisioning key"

  recreate_if_invalid = "always"
}


resource "hcloud_firewall" "main" {
  name = "nailbiter-firewall"

  rule {
    direction  = "in"
    protocol   = "tcp"
    port       = "80"
    source_ips = ["0.0.0.0/0", "::/0"]
  }

  rule {
    direction  = "in"
    protocol   = "tcp"
    port       = "443"
    source_ips = ["0.0.0.0/0", "::/0"]
  }

  rule {
    direction  = "in"
    protocol   = "udp"
    port       = "41641"
    source_ips = ["0.0.0.0/0", "::/0"]
  }
}


resource "hcloud_server" "main" {
  name        = "nailbiter-app-us"
  server_type = var.hcloud_server_type
  location    = var.hcloud_location
  image       = var.hcloud_image

  firewall_ids = [hcloud_firewall.main.id]

  user_data = format("#cloud-config\n%s", templatefile("${path.module}/cloud-init.yaml.tftpl", {
    tailscale_auth_key = tailscale_tailnet_key.server.key
  }))

  lifecycle {
    ignore_changes = [user_data]
  }
}

