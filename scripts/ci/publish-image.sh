#!/bin/sh
set -eu

sh scripts/ci/require-release.sh
: "${GHCR_TOKEN:?A package-write token for the canonical repository is required}"
[ "$(tr -d '\n' < .ci/backend.commit)" = "$GITHUB_SHA" ]
sha256sum --check .ci/backend.sha256
docker load --input .ci/backend.tar
[ "$(docker image inspect --format '{{.Id}}' nailbiter-backend:scan)" = "$(tr -d '\n' < .ci/backend.image-id)" ]
export DOCKER_CONFIG
DOCKER_CONFIG=$(mktemp -d)
trap 'rm -rf "$DOCKER_CONFIG"' EXIT
printf '%s' "$GHCR_TOKEN" | docker login ghcr.io --username "${GITHUB_ACTOR:?The workflow actor is required}" --password-stdin
image=ghcr.io/scowalt/nailbiter/backend
docker tag nailbiter-backend:scan "$image:sha-$GITHUB_SHA"
docker push "$image:sha-$GITHUB_SHA"
digest=$(docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$image:sha-$GITHUB_SHA" |
  grep -E '^ghcr\.io/scowalt/nailbiter/backend@sha256:[0-9a-f]{64}$')
[ "$(printf '%s\n' "$digest" | wc -l)" -eq 1 ]
printf '%s\n' "$digest" > .ci/backend-image.txt
docker tag nailbiter-backend:scan "$image:latest"
docker push "$image:latest"
