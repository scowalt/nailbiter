#!/bin/sh
set -eu

version=$(awk -F '"' '/"version":/ {split($4, parts, "."); print parts[1] "." parts[2]; exit}' apps/backend/global.json)
test -n "$version"
mkdir -p .ci
image=${SCAN_IMAGE:-nailbiter-backend:scan}
docker build --pull --file apps/backend/Dockerfile --build-arg "DOTNET_VERSION=$version" --tag "$image" apps
docker save --output .ci/backend.tar "$image"
