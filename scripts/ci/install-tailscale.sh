#!/bin/sh
set -eu

image=tailscale/tailscale:v1.104.4@sha256:ed6480b33c6eb67c9cbd203ed42b9fb57e361986725b6856def71b7a3d9e276c
container=$(docker create "$image")
trap 'docker rm "$container" >/dev/null' EXIT
mkdir -p .ci/bin
docker cp "$container:/usr/local/bin/tailscale" .ci/bin/tailscale
docker cp "$container:/usr/local/bin/tailscaled" .ci/bin/tailscaled
chmod 755 .ci/bin/tailscale .ci/bin/tailscaled
printf '%s/.ci/bin\n' "$PWD" >> "${GITHUB_PATH:?GitHub path export is required}"
