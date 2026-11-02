#!/usr/bin/env bash
set -euo pipefail

sh scripts/ci/require-release.sh
image=$(tr -d '\n' < .ci/backend-image.txt)
printf '%s\n' "$image" | grep -Eq '^ghcr\.io/scowalt/nailbiter/backend@sha256:[0-9a-f]{64}$'
: "${TAILSCALE_OAUTH_SECRET:?The existing tag:ci OAuth secret is required}"
umask 077
scratch=$(mktemp -d)
tailscaled_pid=
cleanup() {
  tailscale --socket="$scratch/tailscaled.sock" logout >/dev/null 2>&1 || true
  if [[ -n "$tailscaled_pid" ]]; then
    kill "$tailscaled_pid" 2>/dev/null || true
    wait "$tailscaled_pid" 2>/dev/null || true
  fi
  rm -rf "$scratch"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

printf '%s?ephemeral=true&preauthorized=true' "$TAILSCALE_OAUTH_SECRET" > "$scratch/auth-key"
unset TAILSCALE_OAUTH_SECRET

tailscaled --tun=userspace-networking --state=mem: --socket="$scratch/tailscaled.sock" >"$scratch/tailscaled.log" 2>&1 &
tailscaled_pid=$!
for attempt in {1..60}; do
  [[ ! -S "$scratch/tailscaled.sock" ]] || break
  sleep 1
done
test -S "$scratch/tailscaled.sock"
tailscale --socket="$scratch/tailscaled.sock" up \
  --auth-key="file:$scratch/auth-key" \
  --advertise-tags=tag:ci --hostname="nailbiter-ci-${GITHUB_RUN_ID}" --timeout=60s
rm "$scratch/auth-key"

git bundle create "$scratch/release.bundle" HEAD
tailscale --socket="$scratch/tailscaled.sock" ssh root@nailbiter-app-us "bash -c '
  set -euo pipefail
  umask 077
  bundle=\$(mktemp)
  trap '\''rm -f \"\$bundle\"'\'' EXIT
  cat > \"\$bundle\"
  exec 9>/opt/nailbiter-app-deploy.lock
  flock -w 600 9
  cd /opt/nailbiter-app
  git fetch \"\$bundle\" HEAD
  test \"\$(git rev-parse FETCH_HEAD)\" = \"$GITHUB_SHA\"
  git checkout --force --detach FETCH_HEAD
  if git remote get-url origin >/dev/null 2>&1; then
    git remote set-url origin https://github.com/scowalt/nailbiter.git
  else
    git remote add origin https://github.com/scowalt/nailbiter.git
  fi
  bash scripts/ci/deploy-backend.sh \"$GITHUB_SHA\" \"$image\"
'" < "$scratch/release.bundle"
