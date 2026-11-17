#!/bin/sh
set -eu

revision=${GITHUB_SHA:?The commit the image is built from is required}
printf '%s\n' "$revision" | grep -Eqx '[0-9a-f]{40}'
mkdir -p .ci
image=${SCAN_IMAGE:-nailbiter-backend:scan}
docker build --pull --file apps/backend/Dockerfile \
  --build-arg "SOURCE_REVISION=$revision" --tag "$image" apps
docker image inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$image" | grep -Fqx "SourceRevision=$revision"
docker save --output .ci/backend.tar "$image"
