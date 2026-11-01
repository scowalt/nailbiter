# OpenTofu Infrastructure Configuration

## Clean-source migration boundary

The proposed canonical repository is `scowalt/nailbiter`. The original private repository must be preserved, not renamed or recreated by applying its old state with changed variables. Review a separate GitHub-resource/state adoption plan after exact-name repository creation is approved. Keep existing VPS/DNS/Worker/Tailscale resources and secrets unchanged. Do not run a broad `apply` for this migration; `.github/workflows/ci.yml` does not execute infrastructure changes.

Required target contexts are `Validate` and `Build and scan backend image`, but configure protections only after exact-head runs establish them. Source files are a proposal, not evidence that the new repository exists, checks passed remotely, or CD has been armed. See [the cutover runbook](../docs/deployment.md).

This OpenTofu configuration manages infrastructure for nailbiter.app:

- **GitHub**: Repository settings, branch protection, merge policies
- **Cloudflare**: DNS zone, Workers custom domains

## Prerequisites

1. **OpenTofu installed** (version >= 1.6)
   ```bash
   brew install opentofu  # macOS
   # or download from https://opentofu.org/docs/intro/install/
   ```

2. **GitHub authentication**
   - Set the `GITHUB_TOKEN` environment variable with a Personal Access Token (PAT)
   - The token needs `repo` scope permissions
   - Or use GitHub CLI: `gh auth login`

3. **Cloudflare authentication**
   - API token with permissions: Zone:Read, DNS:Edit, Workers Scripts:Read, Workers Routes:Edit
   - Token stored in Doppler (project: `nailbiter`, config: `prd`)

4. **Tailscale authentication**
   - OAuth client with the `auth_keys` scope and `tag:server`
   - Client ID and secret stored in Doppler as `TAILSCALE_OAUTH_CLIENT_ID` and `TAILSCALE_OAUTH_CLIENT_SECRET`

5. **Doppler CLI** (for injecting secrets)
   ```bash
   brew install dopplerhq/cli/doppler
   doppler login
   ```

## Setup

1. Edit `terraform.tfvars` with your configuration (auto-loaded by tofu):
   ```hcl
   # GitHub
   github_owner = "your-github-username"

   # Cloudflare - find account ID in dashboard URL
   cloudflare_account_id = "your-account-id"
   cloudflare_zone_name  = "nailbiter.app"
   ```

2. Initialize OpenTofu:
   ```bash
   cd opentofu
   tofu init
   ```

3. Import existing resources (first time only):
   ```bash
   # GitHub repository
   tofu import github_repository.repo nailbiter-app

   # Cloudflare resources will be imported automatically via data sources
   # Custom domains may need importing if they already exist:
   # tofu import cloudflare_worker_domain.live <account_id>/<domain_id>
   ```

## Usage

### Using the Helper Script (Recommended)

The `./tofu` helper script automatically loads credentials from Doppler:

```bash
cd opentofu
./tofu plan    # Preview changes
./tofu apply   # Apply changes
./tofu destroy # Destroy resources (use with caution)
```

The script uses `doppler run` to inject the infrastructure provider credentials and passes all arguments to the real `tofu` command.

### Manual Setup (Alternative)

If you prefer to set up credentials manually:

```bash
cd opentofu
doppler run --project nailbiter --config prd -- tofu plan
```

## What This Configuration Manages

### GitHub

- **Repository Settings:**
  - Enforces squash merge only (disables merge commits and rebase merges)
  - Enables auto-merge functionality
  - Preserves all existing repository settings

- **Branch Protection (main):**
  - Prevents force pushes
  - Prevents branch deletion
  - Requires linear history
  - Requires status checks to pass before merging

### Cloudflare

- **DNS Zone:** References nailbiter.app zone (managed by Cloudflare)

- **Worker Custom Domains:**
  - `nailbiter.app` - Marketing site (www worker)
  - `www.nailbiter.app` - Marketing site alias

- **DNS Records:** Template for email and verification records (commented out, enable as needed)

> **Note:** Worker scripts are deployed via `wrangler deploy` in CI, not OpenTofu.
> OpenTofu manages the custom domain bindings and DNS configuration.

## Configuration Options

Edit `terraform.tfvars` to customize:

| Variable | Description |
|----------|-------------|
| `github_owner` | GitHub username or organization |
| `repository_name` | Repository name (default: nailbiter-app) |
| `required_status_checks` | CI checks required before merge |
| `cloudflare_account_id` | Your Cloudflare account ID |
| `cloudflare_zone_name` | Domain name (default: nailbiter.app) |

## Files

| File | Purpose |
|------|---------|
| `main.tf` | Provider configuration and GitHub resources |
| `cloudflare.tf` | Cloudflare provider, DNS, and Workers config |
| `variables.tf` | Input variable definitions |
| `outputs.tf` | Output values |
| `terraform.tfvars` | Variable values (auto-loaded, check into git) |

## Adding DNS Records

To add DNS records, edit `cloudflare.tf` and add resources like:

```hcl
resource "cloudflare_dns_record" "example" {
  zone_id = data.cloudflare_zone.main.zone_id
  name    = "subdomain"
  type    = "CNAME"
  content = "target.example.com"
  ttl     = 3600
  proxied = true
}
```

Common record types: A, AAAA, CNAME, MX, TXT
