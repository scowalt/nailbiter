# Historical Cloudflare DNS Setup Notes

These reviewed nonsecret examples document an older origin configuration. They are not verification of current Hetzner routing or authorization to change DNS during the repository migration. The current deployment targets Tailscale host `nailbiter-app-us`; preserve existing production DNS/hosting. Obtain a separately approved current configuration read before applying any DNS command, and never print secret values. See [the migration boundary](deployment.md).

> **Note**: Secrets management has migrated from 1Password to [Doppler](https://www.doppler.com/) (project: `nailbiter`). Use `doppler run --project nailbiter --config prd --` to inject `CLOUDFLARE_API_TOKEN` into commands.

## Cloudflare DNS API

### Get Zone ID

```bash
doppler run --project nailbiter --config prd -- \
  bash -c 'curl -s "https://api.cloudflare.com/client/v4/zones?name=nailbiter.app" \
    -H "Authorization: Bearer $CLOUDFLARE_API_TOKEN" | jq ".result[0].id"'
```

### Create A Record

```bash
ZONE_ID="your_zone_id"
doppler run --project nailbiter --config prd -- \
  bash -c "curl -X POST \"https://api.cloudflare.com/client/v4/zones/$ZONE_ID/dns_records\" \
    -H \"Authorization: Bearer \$CLOUDFLARE_API_TOKEN\" \
    -H \"Content-Type: application/json\" \
    --data '{
      \"type\": \"A\",
      \"name\": \"api\",
      \"content\": \"109.199.126.33\",
      \"proxied\": true
    }'"
```

## DNS Records

| Type | Name | Content | Proxied |
|------|------|---------|---------|
| A | api | 109.199.126.33 | Yes |
| CNAME | www | nailbiter.app | Yes |
| A | live | 192.0.2.1 | Yes (Worker route placeholder) |

## VPS IP Address

The backend VPS public IP: `109.199.126.33`
