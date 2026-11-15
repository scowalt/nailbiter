#!/bin/sh
set -eu

version=$(awk -F '"' '/"version":/ {split($4, parts, "."); print parts[1] "." parts[2]; exit}' apps/backend/global.json)
test -n "$version"
revision=${GITHUB_SHA:?The commit the image is built from is required}
printf '%s\n' "$revision" | grep -Eqx '[0-9a-f]{40}'
mkdir -p .ci
image=${SCAN_IMAGE:-nailbiter-backend:scan}
docker build --pull --file apps/backend/Dockerfile --build-arg "DOTNET_VERSION=$version" \
  --build-arg "SOURCE_REVISION=$revision" --tag "$image" apps
docker image inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$image" | grep -Fqx "SourceRevision=$revision"
docker save --output .ci/backend.tar "$image"
